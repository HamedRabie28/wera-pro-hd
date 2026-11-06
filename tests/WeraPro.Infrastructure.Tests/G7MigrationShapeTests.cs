namespace WeraPro.Infrastructure.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WeraPro.Infrastructure.Migrations;
using Xunit;

/// <summary>
/// فحوص شكل ترحيل V018 (G7) على قاعدة فريدة + مشغلات V005 الحرجة للفواتير:
/// Idempotency للمرتجعات، التسلسلات، قالبا إيصال العميل، مصفوفة صلاحيات V009
/// كما تتحقق V018، تحقق الترحيل (مجاميع/تكلفة/وزن/مدفوع)، تجميد المرحلة،
/// حارس المرتجع، وردية واحدة مفتوحة لكل محطة.
/// </summary>
public sealed class G7MigrationShapeTests : IAsyncLifetime
{
    private readonly string _scratchDb = $"wera_g7_scratch_{Guid.NewGuid().ToString("N")[..10]}";
    private string ScratchUri => $"Host=localhost;Port=55432;Username=postgres;Database={_scratchDb};Include Error Detail=true";
    private const string AdminUri = "Host=localhost;Port=55432;Username=postgres;Database=postgres";
    private static readonly SqlMigrationRunner Runner = new(NullLogger<SqlMigrationRunner>.Instance);

    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(AdminUri);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_scratchDb}\"", admin);
        await create.ExecuteNonQueryAsync();
        await Runner.ApplyPendingAsync(ScratchUri);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminUri);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_scratchDb}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private async Task AssertIndexExistsAsync(string indexName)
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE indexname = @n", conn);
        cmd.Parameters.AddWithValue("n", indexName);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 1, $"الفهرس {indexName} مفقود");
    }

    /// <summary>خطأ متوقع داخل معاملة: SAVEPOINT يحمي بقية الخطوات من إجهاض المعاملة (25P02).</summary>
    private static void ExpectTxError(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string substring)
    {
        using (var sp = new NpgsqlCommand("SAVEPOINT g7step", conn, tx))
        {
            sp.ExecuteNonQuery();
        }
        var ex = Assert.Throws<PostgresException>(() =>
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.ExecuteNonQuery();
        });
        using (var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT g7step", conn, tx))
        {
            rb.ExecuteNonQuery();
        }
        Assert.Contains(substring, ex.Message);
    }

    /// <summary>بذرة سلسلة POS: فرع ← محطة ← مخزن، عميل، صنف بسعر، مستخدمان — تُعاد معرفاتها.</summary>
    private static async Task<(long BranchId, long StationId, long WarehouseId, long CustomerId, long SkuId, long UserId, long DeciderId)>
        SeedPosChainAsync(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var branchId = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G7MS-B', 'فرع فحص V018', 'Africa/Cairo') RETURNING branch_id");
        var stationId = Scalar($"INSERT INTO core.stations (branch_id, code, name_ar) VALUES ({branchId}, 'G7MS-ST', 'محطة فحص') RETURNING station_id");
        var warehouseId = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({branchId}, 'G7MS-W', 'مخزن الفحص', 'MAIN') RETURNING warehouse_id");
        var customerId = Scalar("INSERT INTO sales.customers (code, name_ar, credit_enabled, credit_limit) VALUES ('G7MS-C1', 'عميل الفحص', true, 500) RETURNING customer_id");
        var categoryId = Scalar("INSERT INTO catalog.categories (code, name_ar) VALUES ('G7MS-CAT', 'فئة الفحص') RETURNING category_id");
        var productId = Scalar($"INSERT INTO catalog.products (code, name_ar, category_id) VALUES ('G7MS-P', 'منتج الفحص', {categoryId}) RETURNING product_id");
        var skuId = Scalar($"INSERT INTO catalog.skus (product_id, sku_code, sale_mode, base_uom_id, sale_uom_id) VALUES ({productId}, 'G7MS-SKU', 'COUNT', 1, 1) RETURNING sku_id");
        var priceListId = Scalar("SELECT price_list_id FROM catalog.price_lists WHERE code = 'DEFAULT_EGP'");
        Scalar($"INSERT INTO catalog.product_prices (sku_id, price_list_id, unit_id, unit_price, tax_treatment) VALUES ({skuId}, {priceListId}, 1, 10.00, 'TAXABLE')");
        var userId = Scalar("INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('g7ms.user', 'مستخدم الفحص', 'x', true) RETURNING user_id");
        var deciderId = Scalar("INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('g7ms.chief', 'معتمد الفحص', 'x', true) RETURNING user_id");
        return (branchId, stationId, warehouseId, customerId, skuId, userId, deciderId);
    }

    [Fact]
    public async Task V018_RefundIdempotencyIndex_RejectsDupKey_AllowsMultipleNulls()
    {
        await AssertIndexExistsAsync("uq_refunds_idempotency");
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (branchId, _, _, _, skuId, userId, _) = await SeedPosChainAsync(conn, tx);

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var invoiceId = Scalar($"INSERT INTO sales.invoices (invoice_number, branch_id, station_id, cashier_user_id, status) VALUES ('SI-1', {branchId}, (SELECT station_id FROM core.stations WHERE code='G7MS-ST'), {userId}, 'POSTED') RETURNING invoice_id");
        var approvalId = Scalar($"INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, reason, decided_at) VALUES ('sales.refund', 1, {userId}, {Scalar($"SELECT user_id FROM core.users WHERE username='g7ms.chief'")}, 'APPROVED', 'سبب الفحص', now()) RETURNING approval_id");

        // بلا مفتاح: مرتان مقبولتان (فهرس جزئي WHERE NOT NULL)
        Scalar($"INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, approval_id, status, refund_method, total, reason, created_by) VALUES ('SR-1', {invoiceId}, {branchId}, {approvalId}, 'DRAFT', 'CASH', 5, 'سبب', {userId})");
        Scalar($"INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, approval_id, status, refund_method, total, reason, created_by) VALUES ('SR-2', {invoiceId}, {branchId}, {approvalId}, 'DRAFT', 'CASH', 5, 'سبب', {userId})");

        // بمفتاح مكرر → 23505
        Scalar($"INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, approval_id, status, refund_method, total, reason, created_by, idempotency_key) VALUES ('SR-3', {invoiceId}, {branchId}, {approvalId}, 'DRAFT', 'CASH', 5, 'سبب', {userId}, 'g7ms-key')");
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            using var cmd = new NpgsqlCommand(
                $"INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, approval_id, status, refund_method, total, reason, created_by, idempotency_key) VALUES ('SR-4', {invoiceId}, {branchId}, {approvalId}, 'DRAFT', 'CASH', 5, 'سبب', {userId}, 'g7ms-key')", conn, tx);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("23505", ex.SqlState, StringComparer.Ordinal);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task V018_SequencesAndTemplates_AndHeldCartNote_Seeded()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        await using (var seq1 = new NpgsqlCommand("SELECT nextval('sales.invoice_number_seq'), nextval('sales.invoice_number_seq') FROM (SELECT 1) x", conn))
        await using (var reader = await seq1.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            Assert.NotEqual(reader.GetInt64(0), reader.GetInt64(1)); // أرقام فريدة
        }

        await using var tpl = new NpgsqlCommand(
            "SELECT COUNT(*) FROM print.document_templates WHERE document_type='CUSTOMER_RECEIPT' AND is_active AND version = 1 AND media_class IN ('ROLL_80','ROLL_58')", conn);
        Assert.Equal(2L, Convert.ToInt64(await tpl.ExecuteScalarAsync()));

        await using var note = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='sales' AND table_name='held_carts' AND column_name='note_ar'", conn);
        Assert.Equal(1L, Convert.ToInt64(await note.ExecuteScalarAsync()));

        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM core.schema_migrations", conn);
        Assert.Equal(19L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task V018_V009PermissionMatrix_AsVerifiedByV018()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        async Task<long> GrantCount(string permission, string role)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT COUNT(*) FROM core.role_permissions rp
                  JOIN core.roles r ON r.role_id = rp.role_id
                  JOIN core.permissions p ON p.permission_id = rp.permission_id
                 WHERE p.code = @p AND r.code = @r", conn);
            cmd.Parameters.AddWithValue("p", permission);
            cmd.Parameters.AddWithValue("r", role);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        Assert.Equal(1L, await GrantCount("pos.use", "CASHIER"));
        Assert.Equal(1L, await GrantCount("sales.discount.upto_15", "SUPERVISOR"));
        Assert.Equal(1L, await GrantCount("sales.discount.over_15", "OWNER"));
        Assert.Equal(1L, await GrantCount("sales.return.approve", "MANAGER"));
        Assert.Equal(1L, await GrantCount("sales.credit.sell", "OWNER"));
        Assert.Equal(1L, await GrantCount("sales.price.override", "OWNER"));

        // الكاشير بلا صلاحيات تجاوز (مصفوفة V009)
        Assert.Equal(0L, await GrantCount("sales.price.override", "CASHIER"));
        Assert.Equal(0L, await GrantCount("sales.credit.sell", "CASHIER"));
        Assert.Equal(0L, await GrantCount("sales.return.approve", "CASHIER"));
        Assert.Equal(0L, await GrantCount("sales.discount.upto_15", "CASHIER"));
        Assert.Equal(0L, await GrantCount("pos.use", "STOREKEEPER"));
    }

    [Fact]
    public async Task V005_PostValidate_RejectsEmptyTotalsMismatchMissingCost()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (branchId, stationId, _, _, skuId, userId, _) = await SeedPosChainAsync(conn, tx);

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var stationRef = Scalar($"SELECT station_id FROM core.stations WHERE station_id = {stationId}");
        var invoiceId = Scalar($"INSERT INTO sales.invoices (invoice_number, branch_id, station_id, cashier_user_id, status) VALUES ('SI-V1', {branchId}, {stationRef}, {userId}, 'DRAFT') RETURNING invoice_id");

        // (أ) ترحيل بلا سطور → INVOICE_POST_NO_LINES
        ExpectTxError(conn, tx, $"UPDATE sales.invoices SET status='POSTED' WHERE invoice_id={invoiceId}", "INVOICE_POST_NO_LINES");

        // (ب) سطر بلا لقطة تكلفة → INVOICE_POST_MISSING_COST
        Scalar($"INSERT INTO sales.invoice_lines (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, unit_price, tax_rate_snapshot, net_amount) VALUES ({invoiceId}, 1, {skuId}, 'COUNT', 2, 1, 1, 10.00, 0.14, 17.54)");
        ExpectTxError(conn, tx, $"UPDATE sales.invoices SET status='POSTED', net_total=17.54 WHERE invoice_id={invoiceId}", "INVOICE_POST_MISSING_COST");

        // (ج) مجاميع غير متسقة → INVOICE_TOTALS_MISMATCH
        Scalar($"UPDATE sales.invoice_lines SET cost_snapshot=20.00, cost_method_snapshot='WAC' WHERE invoice_id={invoiceId}");
        ExpectTxError(conn, tx, $"UPDATE sales.invoices SET status='POSTED', net_total=99.99 WHERE invoice_id={invoiceId}", "INVOICE_TOTALS_MISMATCH");

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task V005_PostedInvoiceFrozen_RefundGuardAndSingleOpenShift()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (branchId, stationId, _, _, skuId, userId, deciderId) = await SeedPosChainAsync(conn, tx);

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var invoiceId = Scalar($"INSERT INTO sales.invoices (invoice_number, branch_id, station_id, cashier_user_id, status, net_total, tax_total, gross_total) VALUES ('SI-V2', {branchId}, {stationId}, {userId}, 'DRAFT', 17.54, 2.46, 20.00) RETURNING invoice_id");
        var lineId = Scalar($"INSERT INTO sales.invoice_lines (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, unit_price, tax_rate_snapshot, net_amount, cost_snapshot, cost_method_snapshot) VALUES ({invoiceId}, 1, {skuId}, 'COUNT', 2, 1, 1, 10.00, 0.14, 17.54, 12.00, 'WAC') RETURNING line_id");
        var paymentId = Scalar($"INSERT INTO sales.payments (invoice_id, payment_method, amount, status, created_by) VALUES ({invoiceId}, 'CASH', 20.00, 'CAPTURED', {userId}) RETURNING payment_id");
        Scalar($"UPDATE sales.invoices SET status='POSTED', paid_total=20.00 WHERE invoice_id={invoiceId}");

        // (أ) المرحلة مجمّدة: تعديل مالي مرفوض
        ExpectTxError(conn, tx, $"UPDATE sales.invoices SET net_total=1.00 WHERE invoice_id={invoiceId}", "INVOICE_IMMUTABLE");

        // (ب) حارس المرتجع: رد أكثر من المباع مرفوض (REFUND_EXCEEDS_SOLD)
        var approvalId = Scalar($"INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, reason, decided_at) VALUES ('sales.refund', {invoiceId}, {userId}, {deciderId}, 'APPROVED', 'فحص', now()) RETURNING approval_id");
        var refundId = Scalar($"INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, approval_id, status, refund_method, total, reason, created_by) VALUES ('SR-V1', {invoiceId}, {branchId}, {approvalId}, 'DRAFT', 'CASH', 15, 'فحص الحارس', {userId}) RETURNING refund_id");
        ExpectTxError(conn, tx,
            $"INSERT INTO sales.refund_lines (refund_id, original_line_id, qty, unit_refund, line_total) VALUES ({refundId}, {lineId}, 3, 10.00, 30.00)",
            "REFUND_EXCEEDS_SOLD");
        // في الحدود: يُقبل
        Scalar($"INSERT INTO sales.refund_lines (refund_id, original_line_id, qty, unit_refund, line_total) VALUES ({refundId}, {lineId}, 1, 10.00, 10.00)");

        // (ج) وردية واحدة مفتوحة لكل محطة
        Scalar($"INSERT INTO sales.shifts (station_id, opened_by, status, opening_float) VALUES ({stationId}, {userId}, 'OPEN', 100)");
        ExpectTxError(conn, tx,
            $"INSERT INTO sales.shifts (station_id, opened_by, status, opening_float) VALUES ({stationId}, {userId}, 'OPEN', 50)",
            "uq_shifts_open_per_station");

        // (د) الآجل بلا مزود: pay_credit_no_provider
        ExpectTxError(conn, tx,
            $"INSERT INTO sales.payments (invoice_id, payment_method, amount, status, created_by, provider_name) VALUES ({invoiceId}, 'CREDIT', 5.00, 'CAPTURED', {userId}, 'X')",
            "pay_credit_no_provider");

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task V005_WeightLineConstraints_SourceRequiredAndOnlyForWeight()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (branchId, stationId, _, _, skuId, userId, _) = await SeedPosChainAsync(conn, tx);

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var invoiceId = Scalar($"INSERT INTO sales.invoices (invoice_number, branch_id, station_id, cashier_user_id, status) VALUES ('SI-V3', {branchId}, {stationId}, {userId}, 'DRAFT') RETURNING invoice_id");

        // وزني بلا مصدر → line_weight_fields يرفض
        ExpectTxError(conn, tx,
            $"INSERT INTO sales.invoice_lines (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, unit_price, tax_rate_snapshot, net_amount, weight_kg) VALUES ({invoiceId}, 1, {skuId}, 'WEIGHT', 0.35, 2, 1000, 200, 0.14, 61.40, 0.350)",
            "line_weight_fields");

        // مصدر وزن على سطر عددي → line_weight_only_for_weight يرفض
        ExpectTxError(conn, tx,
            $"INSERT INTO sales.invoice_lines (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, unit_price, tax_rate_snapshot, net_amount, weight_kg, weight_source) VALUES ({invoiceId}, 2, {skuId}, 'COUNT', 1, 1, 1, 10, 0.14, 8.77, 0.350, 'SCALE_READING')",
            "line_weight_only_for_weight");

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task V007_PrintJobIdempotency_UniquePerDocumentCopy()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (_, stationId, _, _, _, userId, _) = await SeedPosChainAsync(conn, tx);

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        var printerId = Scalar($"INSERT INTO print.printer_profiles (printer_id, windows_queue_name, device_model, driver_name, station_id, media_class, media_width_mm, printable_width_mm, dpi_x, dpi_y, adapter_kind) VALUES ('G7MS-PRN', 'Q1', 'M1', 'D1', {stationId}, 'ROLL_80', 80, 72, 203, 203, 'ESC_POS') RETURNING printer_profile_id");
        var templateId = Scalar("SELECT template_id FROM print.document_templates WHERE template_code='ROLL_MEDIUM_80' AND document_type='CUSTOMER_RECEIPT' AND version=1");
        Scalar($"INSERT INTO print.print_jobs (document_id, document_type, template_id, template_version, printer_profile_id, printer_profile_version, media_class, printable_width_mm, dpi, copy_type, status, idempotency_key, requested_by) VALUES (1, 'CUSTOMER_RECEIPT', {templateId}, 1, {printerId}, 1, 'ROLL_80', 72, 203, 'CUSTOMER', 'QUEUED', 'receipt:1:CUSTOMER', {userId})");

        var dupJob = Assert.Throws<PostgresException>(() =>
        {
            using var cmd = new NpgsqlCommand(
                $"INSERT INTO print.print_jobs (document_id, document_type, template_id, template_version, printer_profile_id, printer_profile_version, media_class, printable_width_mm, dpi, copy_type, status, idempotency_key, requested_by) VALUES (1, 'CUSTOMER_RECEIPT', {templateId}, 1, {printerId}, 1, 'ROLL_80', 72, 203, 'CUSTOMER', 'QUEUED', 'receipt:1:CUSTOMER', {userId})", conn, tx);
            cmd.ExecuteNonQuery();
        });
        Assert.Equal("23505", dupJob.SqlState, StringComparer.Ordinal);

        await tx.RollbackAsync();
    }
}
