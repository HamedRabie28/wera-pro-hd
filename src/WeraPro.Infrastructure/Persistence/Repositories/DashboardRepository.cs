namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using WeraPro.Application.Analytics;
using WeraPro.Contracts.V1;

/// <summary>
/// مستودع لوحة الرئيسية HOME-01 — استعلامات SQL مباشرة على سجلات المصدر
/// (sales/inv/purch/finance/ops) بلا جداول تجميعية: البيانات «مباشرة عند الطلب»
/// وتتفق مع تعريفات FINANCIAL_DEFINITIONS.md (صافي دون الضريبة، تحصيل مؤكد، مجمل WAC).
/// </summary>
public sealed class DashboardRepository : IDashboardRepository
{
    private readonly WeraDbContext _db;

    public DashboardRepository(WeraDbContext db) => _db = db;

    private static string BranchFilter(string column, long? branchId)
        => branchId is null ? "" : $" AND {column} = {branchId.Value}";

    public async Task<KpiBlockDto?> GetKpisAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default)
    {
        var bf = BranchFilter("i.branch_id", branchId);
        // صافي المبيعات دون الضريبة: net_total مجموع net_amount (بلا ضريبة) — المرتجعات تُخصم
        // بقيمتها المسجلة على الفاتورة (returned_total). الفواتير المحتسبة: المرحّلة وعائلاتها.
        var sql = $"""
            SELECT
                COALESCE(SUM(i.net_total - i.returned_total), 0)::numeric AS "NetSalesExclVat",
                COALESCE(SUM(p.captured), 0)::numeric AS "ConfirmedCollection",
                COUNT(*)::bigint AS "CompletedInvoices",
                COALESCE(SUM(l.cogs), 0)::numeric AS "Cogs",
                COALESCE(SUM(l.cost_missing), 0)::bigint AS "CostMissingLines",
                COUNT(*) FILTER (WHERE l.invoice_id IS NULL)::bigint AS "InvoicesWithoutLines"
            FROM sales.invoices i
            LEFT JOIN (
                SELECT il.invoice_id,
                       SUM(il.cost_snapshot * il.base_qty) AS cogs,
                       COUNT(*) FILTER (WHERE il.cost_snapshot IS NULL) AS cost_missing
                FROM sales.invoice_lines il GROUP BY il.invoice_id
            ) l ON l.invoice_id = i.invoice_id
            LEFT JOIN (
                SELECT pi.invoice_id, SUM(pi.amount) FILTER (WHERE pi.status IN ('CAPTURED','SETTLED') AND pi.payment_method <> 'CREDIT') AS captured
                FROM sales.payments pi GROUP BY pi.invoice_id
            ) p ON p.invoice_id = i.invoice_id
            WHERE i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
              AND i.created_at >= {Format(fromUtc)} AND i.created_at < {Format(toUtc)}{bf}
            """;
        var row = (await _db.Database.SqlQueryRaw<KpiRow>(sql).ToListAsync(ct)).FirstOrDefault();
        if (row is null)
        {
            return new KpiBlockDto(0, null, null, 0, 0);
        }

        // مجمل الربح (WAC): غير متاح بصدق إن نقصت لقطة تكلفة أو غابت أسطر فاتورة (قاموس المؤشرات ق7)
        decimal? grossProfit = null;
        string? reason = null;
        if (row.CostMissingLines == 0 && row.InvoicesWithoutLines == 0)
        {
            grossProfit = row.NetSalesExclVat - row.Cogs;
        }
        else if (row.InvoicesWithoutLines > 0)
        {
            reason = $"غير متاح — {row.InvoicesWithoutLines} فاتورة بلا أسطر/تكلفة";
        }
        else
        {
            reason = $"غير متاح — {row.CostMissingLines} سطراً بلقطة تكلفة ناقصة";
        }
        return new KpiBlockDto(row.NetSalesExclVat, grossProfit, reason, row.ConfirmedCollection, row.CompletedInvoices);
    }

    private sealed record KpiRow(decimal NetSalesExclVat, decimal ConfirmedCollection, long CompletedInvoices, decimal Cogs, long CostMissingLines, long InvoicesWithoutLines);

    public async Task<IReadOnlyList<SalesPointDto>> GetSeriesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default)
    {
        var bf = BranchFilter("i.branch_id", branchId);
        var sql = $"""
            SELECT (i.created_at AT TIME ZONE 'Africa/Cairo')::date AS "DateUtc",
                   COALESCE(SUM(i.net_total - i.returned_total), 0)::numeric AS "NetSales",
                   COUNT(*)::bigint AS "Invoices"
            FROM sales.invoices i
            WHERE i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
              AND i.created_at >= {Format(fromUtc)} AND i.created_at < {Format(toUtc)}{bf}
            GROUP BY 1 ORDER BY 1
            """;
        var rows = await _db.Database.SqlQueryRaw<SeriesRow>(sql).ToListAsync(ct);
        return [.. rows.Select(r => new SalesPointDto(DateOnly.FromDateTime(r.DateUtc), r.NetSales, r.Invoices))];
    }

    private sealed record SeriesRow(DateTime DateUtc, decimal NetSales, long Invoices);

    public async Task<decimal?> GetNetSalesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default)
    {
        var bf = BranchFilter("i.branch_id", branchId);
        var sql = $"""
            SELECT COALESCE(SUM(i.net_total - i.returned_total), 0)::numeric AS "NetSales"
            FROM sales.invoices i
            WHERE i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
              AND i.created_at >= {Format(fromUtc)} AND i.created_at < {Format(toUtc)}{bf}
            """;
        var rows = await _db.Database.SqlQueryRaw<NetRow>(sql).ToListAsync(ct);
        return rows.Count == 0 ? 0m : rows[0].NetSales;
    }

    private sealed record NetRow(decimal NetSales);

    public async Task<IReadOnlyList<TopProductDto>> GetTopProductsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, int limit, CancellationToken ct = default)
    {
        var bf = BranchFilter("i.branch_id", branchId);
        var sql = $"""
            SELECT il.sku_id AS "SkuId", s.sku_code AS "SkuCode", s.sku_name_ar AS "NameAr",
                   SUM(il.net_amount)::numeric AS "NetSales", SUM(il.base_qty)::numeric AS "QtySold"
            FROM sales.invoice_lines il
            JOIN sales.invoices i ON i.invoice_id = il.invoice_id
            LEFT JOIN catalog.skus s ON s.sku_id = il.sku_id
            WHERE i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
              AND i.created_at >= {Format(fromUtc)} AND i.created_at < {Format(toUtc)}{bf}
            GROUP BY il.sku_id, s.sku_code, s.sku_name_ar
            ORDER BY "NetSales" DESC
            LIMIT {limit}
            """;
        return [.. await _db.Database.SqlQueryRaw<TopProductDto>(sql).ToListAsync(ct)];
    }

    public async Task<ShiftsBlockDto> GetShiftsBlockAsync(CancellationToken ct = default)
    {
        var open = $"""
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(CASE WHEN s.opened_at < now() - interval '12 hours' THEN 1 ELSE 0 END), 0)::int AS "Long"
            FROM sales.shifts s WHERE s.status = 'OPEN'
            """;
        var openRow = (await _db.Database.SqlQueryRaw<OpenRow>(open).ToListAsync(ct)).First();

        var unsettled = """
            SELECT COUNT(*)::int AS "Count", COALESCE(SUM(s.variance), 0)::numeric AS "Total"
            FROM sales.shifts s
            WHERE s.status = 'CLOSED' AND s.variance <> 0 AND s.variance_settled_by_voucher_id IS NULL
            """;
        var unRow = (await _db.Database.SqlQueryRaw<UnsettledRow>(unsettled).ToListAsync(ct)).First();

        var longShifts = """
            SELECT s.shift_id::bigint AS "ShiftId", s.station_id::bigint AS "StationId",
                   COALESCE(u.username, '؟') AS "OpenedBy", s.opened_at AS "OpenedAtUtc"
            FROM sales.shifts s LEFT JOIN core.users u ON u.user_id = s.opened_by
            WHERE s.status = 'OPEN' AND s.opened_at < now() - interval '12 hours'
            ORDER BY s.opened_at LIMIT 5
            """;
        var longRows = await _db.Database.SqlQueryRaw<OpenShiftDto>(longShifts).ToListAsync(ct);

        return new ShiftsBlockDto(openRow.Count, unRow.Count, unRow.Total, [.. longRows]);
    }

    private sealed record OpenRow(int Count, int Long);
    private sealed record UnsettledRow(int Count, decimal Total);

    public async Task<TreasuryBlockDto> GetTreasuryBlockAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default)
    {
        var bf = BranchFilter("i.branch_id", branchId);
        var sql = $"""
            SELECT
                COALESCE(SUM(p.amount) FILTER (WHERE p.payment_method IN ('CASH','SPLIT_CASH')), 0)::numeric AS "CashCollected",
                COALESCE(SUM(p.amount) FILTER (WHERE p.payment_method IN ('CARD','SPLIT_CARD')), 0)::numeric AS "CardCollected",
                COALESCE(SUM(p.amount) FILTER (WHERE p.payment_method = 'CREDIT'), 0)::numeric AS "CreditSold"
            FROM sales.payments p
            JOIN sales.invoices i ON i.invoice_id = p.invoice_id
            WHERE p.status IN ('CAPTURED','SETTLED')
              AND p.received_at >= {Format(fromUtc)} AND p.received_at < {Format(toUtc)}{bf}
            """;
        var row = (await _db.Database.SqlQueryRaw<TreasuryRow>(sql).ToListAsync(ct)).First();
        return new TreasuryBlockDto(row.CashCollected, row.CardCollected, row.CreditSold);
    }

    private sealed record TreasuryRow(decimal CashCollected, decimal CardCollected, decimal CreditSold);

    public async Task<IReadOnlyList<CriticalItemDto>> GetCriticalStockAsync(long? branchId, int limit, CancellationToken ct = default)
    {
        var bf = BranchFilter("w.branch_id", branchId);
        // النافد/قرب النفاد: المتاح (base_qty − reserved_qty) ≤ حد إعادة الطلب — سجل المصدر inv.stock_balances
        var sql = $"""
            SELECT s.sku_id::bigint AS "SkuId", s.sku_code AS "SkuCode", s.sku_name_ar AS "NameAr",
                   MIN(sb.base_qty - sb.reserved_qty)::numeric AS "AvailableQty",
                   MIN(s.reorder_level)::numeric AS "ReorderLevel",
                   MIN(COALESCE(u.name_ar, '')) AS "UnitAr"
            FROM catalog.skus s
            JOIN inv.stock_balances sb ON sb.sku_id = s.sku_id
            JOIN inv.warehouses w ON w.warehouse_id = sb.warehouse_id
            LEFT JOIN catalog.units u ON u.uom_id = s.base_uom_id
            WHERE s.status = 'ACTIVE' AND s.reorder_level IS NOT NULL{bf}
            GROUP BY s.sku_id, s.sku_code, s.sku_name_ar
            HAVING MIN(sb.base_qty - sb.reserved_qty) <= MIN(s.reorder_level)
            ORDER BY "AvailableQty" ASC
            LIMIT {limit}
            """;
        var rows = await _db.Database.SqlQueryRaw<CriticalRow>(sql).ToListAsync(ct);
        return [.. rows.Select(r => new CriticalItemDto(r.SkuId, r.SkuCode,
            string.IsNullOrWhiteSpace(r.NameAr) ? null : r.NameAr, r.AvailableQty, r.ReorderLevel,
            string.IsNullOrWhiteSpace(r.UnitAr) ? null : r.UnitAr))];
    }

    private sealed record CriticalRow(long SkuId, string SkuCode, string? NameAr, decimal AvailableQty, decimal? ReorderLevel, string? UnitAr);

    public async Task<(int Pending, int Overdue)> GetPurchaseOrderCountsAsync(CancellationToken ct = default)
    {
        var sql = """
            SELECT
                COUNT(*) FILTER (WHERE status IN ('ORDERED','PARTIAL'))::int AS "Pending",
                COUNT(*) FILTER (WHERE status IN ('ORDERED','PARTIAL') AND expected_date < now()::date)::int AS "Overdue"
            FROM purch.purchase_orders
            """;
        var row = (await _db.Database.SqlQueryRaw<PoRow>(sql).ToListAsync(ct)).First();
        return (row.Pending, row.Overdue);
    }

    private sealed record PoRow(int Pending, int Overdue);

    public async Task<IReadOnlyList<AlertDto>> GetAlertsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default)
    {
        var alerts = new List<AlertDto>();

        // 1) ورديات مفتوحة منذ أكثر من 12 ساعة — حرج
        var longShiftsAlerts = """
            SELECT s.shift_id::bigint AS "ShiftId", s.opened_at AS "OpenedAt"
            FROM sales.shifts s
            WHERE s.status = 'OPEN' AND s.opened_at < now() - interval '12 hours'
            ORDER BY s.opened_at LIMIT 5
            """;
        foreach (var s in await _db.Database.SqlQueryRaw<ShiftAlertRow>(longShiftsAlerts).ToListAsync(ct))
        {
            alerts.Add(new AlertDto(1, "وردية مفتوحة منذ أكثر من 12 ساعة",
                $"الوردية #{s.ShiftId}", s.OpenedAt, "sales"));
        }

        // 2) فروق خزينة غير مسوّاة — مرتفع
        var variances = """
            SELECT s.shift_id::bigint AS "ShiftId", s.variance::numeric AS "Variance", s.closed_at AS "ClosedAt"
            FROM sales.shifts s
            WHERE s.status = 'CLOSED' AND s.variance <> 0 AND s.variance_settled_by_voucher_id IS NULL
            ORDER BY s.closed_at DESC LIMIT 5
            """;
        foreach (var v in await _db.Database.SqlQueryRaw<VarianceRow>(variances).ToListAsync(ct))
        {
            alerts.Add(new AlertDto(2, "فرق خزينة يحتاج تسوية",
                $"الوردية #{v.ShiftId}: عجز/زيادة {v.Variance:0.00} ج.م", v.ClosedAt ?? DateTimeOffset.UtcNow, "finance"));
        }

        // 3) نفاد أصناف — مرتفع (متاح ≤ 0 عند توفر حد طلب)
        var bf = BranchFilter("w.branch_id", branchId);
        var outOfStock = $"""
            SELECT s.sku_code AS "SkuCode", s.sku_name_ar AS "NameAr", MIN(sb.base_qty - sb.reserved_qty)::numeric AS "Available"
            FROM catalog.skus s
            JOIN inv.stock_balances sb ON sb.sku_id = s.sku_id
            JOIN inv.warehouses w ON w.warehouse_id = sb.warehouse_id
            WHERE s.status = 'ACTIVE' AND s.reorder_level IS NOT NULL{bf}
            GROUP BY s.sku_id, s.sku_code, s.sku_name_ar
            HAVING MIN(sb.base_qty - sb.reserved_qty) <= 0
            ORDER BY 3 LIMIT 5
            """;
        foreach (var o in await _db.Database.SqlQueryRaw<StockAlertRow>(outOfStock).ToListAsync(ct))
        {
            alerts.Add(new AlertDto(2, "صنف نافد من المخزون",
                $"{o.SkuCode} — {o.NameAr}", DateTimeOffset.UtcNow, "inventory"));
        }

        // 4) أوامر شراء متأخرة — متوسط
        var overduePos = """
            SELECT po_number AS "PoNumber", expected_date AS "ExpectedDate" FROM purch.purchase_orders
            WHERE status IN ('ORDERED','PARTIAL') AND expected_date < now()::date
            ORDER BY expected_date LIMIT 5
            """;
        foreach (var po in await _db.Database.SqlQueryRaw<PoAlertRow>(overduePos).ToListAsync(ct))
        {
            alerts.Add(new AlertDto(3, "أمر شراء متأخر عن تاريخ التسليم",
                $"{po.PoNumber} — كان متوقعاً {po.ExpectedDate:yyyy-MM-dd}", DateTimeOffset.UtcNow, "purchasing"));
        }

        // 5) نسخ احتياطية فاشلة آخر 7 أيام — حرج
        var failedBackups = """
            SELECT b.storage_location_ref AS "FileName", b.created_at AS "CreatedAt" FROM ops.backup_records b
            WHERE b.status = 'FAILED' AND b.created_at > now() - interval '7 days'
            ORDER BY b.created_at DESC LIMIT 3
            """;
        foreach (var fb in await _db.Database.SqlQueryRaw<BackupAlertRow>(failedBackups).ToListAsync(ct))
        {
            alerts.Add(new AlertDto(1, "فشل نسخة احتياطية", fb.FileName, fb.CreatedAt, "maintenance"));
        }

        return [.. alerts.OrderBy(a => a.Severity).ThenBy(a => a.AtUtc)];
    }

    private sealed record ShiftAlertRow(long ShiftId, DateTimeOffset OpenedAt);
    private sealed record VarianceRow(long ShiftId, decimal Variance, DateTimeOffset? ClosedAt);
    private sealed record StockAlertRow(string SkuCode, string? NameAr, decimal Available);
    private sealed record PoAlertRow(string PoNumber, DateOnly ExpectedDate);
    private sealed record BackupAlertRow(string FileName, DateTimeOffset CreatedAt);

    public async Task<string?> GetBranchNameAsync(long branchId, CancellationToken ct = default)
    {
        var sql = $"SELECT name_ar AS \"NameAr\" FROM core.branches WHERE branch_id = {branchId}";
        var rows = await _db.Database.SqlQueryRaw<NameRow>(sql).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0].NameAr;
    }

    private sealed record NameRow(string NameAr);

    private static string Format(DateTimeOffset value)
        => $"'{value.UtcDateTime:yyyy-MM-dd HH:mm:ss}'::timestamptz";
}
