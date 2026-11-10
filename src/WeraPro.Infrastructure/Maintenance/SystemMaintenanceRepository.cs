namespace WeraPro.Infrastructure.Maintenance;

using Microsoft.EntityFrameworkCore;
using WeraPro.Application.Abstractions;
using WeraPro.Infrastructure.Persistence;

/// <summary>
/// عمليات صيانة خام (PAGE-51): إحصاءات حقيقية + حذف كامل لبيانات التشغيل +
/// تنظيف مقيد بحد احتفاظ — كلها SQL صريح عبر اتصال EF/Npgsql داخل معاملة.
/// الحذف الكامل يفرغ جداول التشغيل بإعادة تسلسل، ويبقي الهوية والحوكمة والهيكل.
/// </summary>
public sealed class SystemMaintenanceRepository : ISystemMaintenanceRepository
{
    /// <summary>جداول بيانات التشغيل التي تُفرَّغ بالحذف الكامل (ترتيب لا يهم مع CASCADE).</summary>
    private static readonly string[] WipeTables =
    [
        // مبيعات وورديات وعملاء
        "sales.invoice_lines", "sales.invoices", "sales.payments", "sales.refund_lines", "sales.refunds",
        "sales.held_carts", "sales.drawer_events", "sales.shifts", "sales.customers",
        // مخزون
        "inv.stock_movements", "inv.stock_balances", "inv.stock_adjustments", "inv.stock_reservations",
        "inv.count_lines", "inv.count_sessions", "inv.lots", "inv.serials",
        "inv.transfer_lines", "inv.transfer_orders",
        // مشتريات
        "purch.purchase_order_lines", "purch.purchase_orders", "purch.purchase_receipt_lines", "purch.purchase_receipts",
        "purch.supplier_invoice_lines", "purch.supplier_invoices",
        "purch.supplier_payment_allocations", "purch.supplier_payments",
        "purch.supplier_return_lines", "purch.supplier_returns", "purch.suppliers",
        // مالية تشغيلية
        "finance.journal_lines", "finance.journal_entries", "finance.vouchers",
        "finance.treasury_transactions", "finance.expenses", "finance.capital_events",
        "finance.provider_settlements", "finance.cost_layers",
        // كتالوج ووسائط
        "catalog.product_images", "catalog.product_prices", "catalog.packages", "catalog.barcodes", "catalog.plus",
        "catalog.skus", "catalog.products", "catalog.media_import_files", "catalog.media_import_jobs", "catalog.media_assets",
        // طباعة تشغيلية وتدقيق وجلسات ورموز
        "print.print_jobs",
        "audit.audit_logs", "audit.idempotency_keys", "audit.user_sessions",
        "core.refresh_tokens",
    ];

    private readonly WeraDbContext _db;

    public SystemMaintenanceRepository(WeraDbContext db) => _db = db;

    public async Task<SystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        var sql = """
            SELECT
                (SELECT count(*) FROM catalog.products)::bigint AS products,
                (SELECT count(*) FROM catalog.skus)::bigint AS skus,
                (SELECT count(*) FROM inv.stock_movements)::bigint AS movements,
                (SELECT count(*) FROM sales.invoices)::bigint AS invoices,
                (SELECT count(*) FROM catalog.media_assets)::bigint AS media,
                (SELECT count(*) FROM audit.audit_logs)::bigint AS audit_events,
                (SELECT count(*) FROM audit.user_sessions WHERE closed_at IS NULL)::bigint AS open_sessions,
                (SELECT count(*) FROM print.print_jobs)::bigint AS print_jobs,
                (SELECT count(*) FROM ops.backup_records)::bigint AS backups,
                (SELECT count(*) FROM core.users)::bigint AS users,
                current_database() AS db_name
            """;
        var row = await _db.Database.SqlQueryRaw<StatsRow>(sql).SingleAsync(ct);
        return new SystemStats(
            row.Products, row.Skus, row.Movements, row.Invoices, row.Media, row.AuditEvents,
            row.OpenSessions, row.PrintJobs, row.Backups, row.Users, row.DbName);
    }

    /// <summary>صف إحصاءات — أسماء الأعمدة مطابقة لأسماء الخصائص (ربط SqlQueryRaw).</summary>
    private sealed record StatsRow(
        long Products, long Skus, long Movements, long Invoices, long Media,
        long AuditEvents, long OpenSessions, long PrintJobs, long Backups, long Users, string DbName);

    public async Task<IReadOnlyList<string>> WipeBusinessDataAsync(CancellationToken ct = default)
    {
        var done = new List<string>();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var table in WipeTables)
        {
            try
            {
                await _db.Database.ExecuteSqlRawAsync(
                    $"TRUNCATE TABLE {table} RESTART IDENTITY CASCADE", ct);
                done.Add(table);
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                // جدول غير منشأ في هذه القاعدة (بوابة لم تُبن بعد) — يُتخطى بصدق
            }
        }
        await tx.CommitAsync(ct);
        return done;
    }

    public async Task<int> PurgeAuditEventsAsync(int retentionDays, CancellationToken ct = default)
        => await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM audit.audit_logs WHERE occurred_at < now() - make_interval(days => {retentionDays})", ct);

    public async Task<int> CloseStaleSessionsAsync(int retentionDays, CancellationToken ct = default)
        => await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE audit.user_sessions SET closed_at = now() WHERE closed_at IS NULL AND opened_at < now() - make_interval(days => {retentionDays})", ct);

    public async Task<int> RemoveOrphanMediaAsync(int retentionDays, CancellationToken ct = default)
        => await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM catalog.media_assets
             WHERE created_at < now() - make_interval(days => {retentionDays})
               AND NOT EXISTS (SELECT 1 FROM catalog.product_images pi WHERE pi.media_asset_id = catalog.media_assets.media_asset_id)
             """, ct);

    public async Task<int> PurgeCompletedPrintJobsAsync(int retentionDays, CancellationToken ct = default)
        => await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM print.print_jobs WHERE created_at < now() - make_interval(days => {retentionDays}) AND status IN ('DONE','FAILED','CANCELLED')", ct);

    private static async Task<SystemStats> ReadStatsAsync(System.Data.Common.DbCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new SystemStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "؟");
        }
        return new SystemStats(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10));
    }
}
