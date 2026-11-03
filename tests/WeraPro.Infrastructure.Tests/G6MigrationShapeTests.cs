namespace WeraPro.Infrastructure.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WeraPro.Infrastructure.Migrations;
using Xunit;

/// <summary>
/// فحوص شكل ترحيل V017 (G6) على قاعدة فريدة: فهارس Idempotivity الفريدة
/// الجزئية، حارس توزيع السداد على الفواتير، قيود نوع التسوية وعدم السالب،
/// عمود وقت اعتماد الجرد، وبذور الصلاحيات السبعة عشرة.
/// </summary>
public sealed class G6MigrationShapeTests : IAsyncLifetime
{
    private readonly string _scratchDb = $"wera_g6_scratch_{Guid.NewGuid().ToString("N")[..10]}";
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
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_indexes WHERE indexname = @n", conn);
        cmd.Parameters.AddWithValue("n", indexName);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 1, $"الفهرس {indexName} مفقود");
    }

    [Fact]
    public async Task V017_IdempotencyPartialUniqueIndexes_Exist()
    {
        await AssertIndexExistsAsync("uq_receipts_idempotency");
        await AssertIndexExistsAsync("uq_supplier_invoices_idempotency");
        await AssertIndexExistsAsync("uq_supplier_payments_idempotency");
        await AssertIndexExistsAsync("uq_supplier_returns_idempotency");
    }

    /// <summary>بذرة سلسلة المفاتيح: فرع ← مخزن، ومورد، وحساب خزينة — تُعاد معرفاتها الحقيقية.</summary>
    private static async Task<(long SupplierId, long WarehouseId, long TreasuryId, long UserId, long DeciderId)> SeedFkChainAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        await using var branch = new NpgsqlCommand(
            "INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G6TB' || nextval('purch.g6_doc_seq')::text, 'فرع البذرة', 'Africa/Cairo') RETURNING branch_id", conn, tx);
        var branchId = Convert.ToInt64(await branch.ExecuteScalarAsync());
        await using var wh = new NpgsqlCommand(
            $"INSERT INTO inv.warehouses (branch_id, code, name_ar) VALUES ({branchId}, 'G6TW' || nextval('purch.g6_doc_seq')::text, 'مخزن البذرة') RETURNING warehouse_id", conn, tx);
        var warehouseId = Convert.ToInt64(await wh.ExecuteScalarAsync());
        await using var sup = new NpgsqlCommand(
            "INSERT INTO purch.suppliers (code, name_ar) VALUES ('G6TS' || nextval('purch.g6_doc_seq')::text, 'مورد البذرة') RETURNING supplier_id", conn, tx);
        var supplierId = Convert.ToInt64(await sup.ExecuteScalarAsync());
        await using var tre = new NpgsqlCommand(
            "INSERT INTO finance.treasury_accounts (code, name_ar, account_kind) VALUES ('G6TT' || nextval('purch.g6_doc_seq')::text, 'خزينة البذرة', 'SAFE') RETURNING treasury_account_id", conn, tx);
        var treasuryId = Convert.ToInt64(await tre.ExecuteScalarAsync());
        await using var usr = new NpgsqlCommand(
            "INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('g6u' || nextval('purch.g6_doc_seq')::text, 'مستخدم البذرة', 'x', true) RETURNING user_id", conn, tx);
        var userId = Convert.ToInt64(await usr.ExecuteScalarAsync());
        await using var dec = new NpgsqlCommand(
            "INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('g6d' || nextval('purch.g6_doc_seq')::text, 'بتار البذرة', 'x', true) RETURNING user_id", conn, tx);
        var deciderId = Convert.ToInt64(await dec.ExecuteScalarAsync());
        return (supplierId, warehouseId, treasuryId, userId, deciderId);
    }

    [Fact]
    public async Task V017_IdempotencyIndex_RejectsSecondRowWithSameKey_AllowsMultipleNulls()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (supplierId, warehouseId, _, userId, _) = await SeedFkChainAsync(conn, tx);

        // بلا مفتاح: يُقبل مراراً (فهرس جزئي WHERE NOT NULL) — قبل أي خطأ يجهض المعاملة
        await using (var noKey1 = new NpgsqlCommand(
            $"INSERT INTO purch.purchase_receipts (receipt_number, supplier_id, warehouse_id, status, received_by) " +
            $"VALUES ('RC-T3', {supplierId}, {warehouseId}, 'DRAFT', {userId})", conn, tx))
        {
            await noKey1.ExecuteNonQueryAsync();
        }
        await using (var noKey2 = new NpgsqlCommand(
            $"INSERT INTO purch.purchase_receipts (receipt_number, supplier_id, warehouse_id, status, received_by) " +
            $"VALUES ('RC-T4', {supplierId}, {warehouseId}, 'DRAFT', {userId})", conn, tx))
        {
            await noKey2.ExecuteNonQueryAsync();
        }

        // الأول بالمفتاح يُقبل
        await using (var first = new NpgsqlCommand(
            $"INSERT INTO purch.purchase_receipts (receipt_number, supplier_id, warehouse_id, status, received_by, idempotency_key) " +
            $"VALUES ('RC-T1', {supplierId}, {warehouseId}, 'POSTED', {userId}, 'dup-key')", conn, tx))
        {
            await first.ExecuteNonQueryAsync();
        }
        // الثاني بنفس المفتاح → 23505 (لا ترحيل مزدوج بمفتاح واحد) — وخطأ المعاملة يجهضها فنتراجع كلياً
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var second = new NpgsqlCommand(
                $"INSERT INTO purch.purchase_receipts (receipt_number, supplier_id, warehouse_id, status, received_by, idempotency_key) " +
                $"VALUES ('RC-T2', {supplierId}, {warehouseId}, 'POSTED', {userId}, 'dup-key')", conn, tx);
            await second.ExecuteNonQueryAsync();
        });
        Assert.Equal("23505", ex.SqlState, StringComparer.Ordinal);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task V017_InvoiceAllocationGuard_Exists()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM pg_trigger WHERE tgname = 'trg_invoice_allocation_guard'", conn);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 1, "حارس توزيع السداد على الفواتير مفقود");
    }

    [Fact]
    public async Task V017_InvoiceAllocationGuard_RejectsOverPaymentAtCommit()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (supplierId, _, treasuryId, userId, _) = await SeedFkChainAsync(conn, tx);

        // فاتورة 100 + سداد مرحّل 60 + تخصيص 60 عليها → 120 > 100 عند الالتزام
        long invoiceId, paymentId;
        await using (var inv = new NpgsqlCommand(
            $"INSERT INTO purch.supplier_invoices (invoice_number, supplier_id, subtotal, tax_total, grand_total, status, invoice_date, created_by, posted_at) " +
            $"VALUES ('INV-G' || nextval('purch.g6_doc_seq')::text, {supplierId}, 100, 0, 100, 'POSTED', current_date, {userId}, now()) RETURNING invoice_id", conn, tx))
        {
            invoiceId = Convert.ToInt64(await inv.ExecuteScalarAsync());
        }
        long paymentId2;
        await using (var pay = new NpgsqlCommand(
            $"INSERT INTO purch.supplier_payments (voucher_number, supplier_id, treasury_account_id, amount, payment_method, payment_date, status, created_by, posted_at) " +
            $"VALUES ('SP-G' || nextval('purch.g6_doc_seq')::text, {supplierId}, {treasuryId}, 60, 'CASH', current_date, 'POSTED', {userId}, now()) RETURNING payment_id", conn, tx))
        {
            paymentId = Convert.ToInt64(await pay.ExecuteScalarAsync());
        }
        await using (var pay2 = new NpgsqlCommand(
            $"INSERT INTO purch.supplier_payments (voucher_number, supplier_id, treasury_account_id, amount, payment_method, payment_date, status, created_by, posted_at) " +
            $"VALUES ('SP-G' || nextval('purch.g6_doc_seq')::text, {supplierId}, {treasuryId}, 60, 'CASH', current_date, 'POSTED', {userId}, now()) RETURNING payment_id", conn, tx))
        {
            paymentId2 = Convert.ToInt64(await pay2.ExecuteScalarAsync());
        }
        // تخصيص 60 + 60 = 120 > قيمة الفاتورة 100 → يُرفض عند الالتزام (مؤجل V017)
        await using (var alloc1 = new NpgsqlCommand(
            $"INSERT INTO purch.supplier_payment_allocations (payment_id, invoice_id, amount) " +
            $"VALUES ({paymentId}, {invoiceId}, 60)", conn, tx))
        {
            await alloc1.ExecuteNonQueryAsync();
        }
        await using (var alloc2 = new NpgsqlCommand(
            $"INSERT INTO purch.supplier_payment_allocations (payment_id, invoice_id, amount) " +
            $"VALUES ({paymentId2}, {invoiceId}, 60)", conn, tx))
        {
            await alloc2.ExecuteNonQueryAsync();
        }
        var ex = await Assert.ThrowsAsync<PostgresException>(async () => await tx.CommitAsync());
        Assert.Contains("INVOICE_OVER_PAYMENT", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V017_AdjustmentKindDomain_AndNonNegativeNewQty()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        // كل فحص سالب في معاملة مستقلة: الخطأ المتوقع يجهض معاملته فلا يعطل بقية الفحوص
        async Task ExpectViolationAsync(string sql, params string[] expectedTokens)
        {
            await using var tx = await conn.BeginTransactionAsync();
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Contains(ex.SqlState, expectedTokens);
            await tx.RollbackAsync();
        }

        long warehouseId, skuId, approvalId, userId;
        await using (var setup = await conn.BeginTransactionAsync())
        {
            var (supplierId, whId, _, usrId, deciderId) = await SeedFkChainAsync(conn, setup);
            warehouseId = whId;
            userId = usrId;
            await using (var approval = new NpgsqlCommand(
                $"INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, decided_at) " +
                $"VALUES ('inv.adjustment', 1, {userId}, {deciderId}, 'APPROVED', now()) RETURNING approval_id", conn, setup))
            {
                approvalId = Convert.ToInt64(await approval.ExecuteScalarAsync());
            }
            await using (var cat = new NpgsqlCommand(
                "INSERT INTO catalog.categories (code, name_ar) VALUES ('G6TC' || nextval('purch.g6_doc_seq')::text, 'فئة الفحص') RETURNING category_id", conn, setup))
            {
                var categoryId = Convert.ToInt64(await cat.ExecuteScalarAsync());
                await using (var prod = new NpgsqlCommand(
                    $"INSERT INTO catalog.products (code, name_ar, status, category_id) VALUES ('G6TP', 'منتج الفحص', 'ACTIVE', {categoryId}) RETURNING product_id", conn, setup))
                {
                    var productId = Convert.ToInt64(await prod.ExecuteScalarAsync());
                    await using var sku = new NpgsqlCommand(
                        $"INSERT INTO catalog.skus (product_id, sku_code, sale_mode, base_uom_id, sale_uom_id, status) VALUES ({productId}, 'G6TP-1', 'COUNT', 1, 1, 'ACTIVE') RETURNING sku_id", conn, setup);
                    skuId = Convert.ToInt64(await sku.ExecuteScalarAsync());
                }
            }
            _ = supplierId;
            await setup.CommitAsync();   // البذرة تبقى حية للفحوص السالبة والإيجابية
        }

        // نوع غير معروف صريح → adjust_kind_domain (إغفال العمود يعطي الافتراضي QUICK السليم)
        await ExpectViolationAsync(
            $"INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, approval_id, adjustment_kind, created_by) " +
            $"VALUES ({warehouseId}, {skuId}, 5, 'اختبار', {approvalId}, 'GALAXY', {userId})", "23514");
        // newQty سالب → adjust_new_qty_nonneg
        await ExpectViolationAsync(
            $"INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, approval_id, adjustment_kind, created_by) " +
            $"VALUES ({warehouseId}, {skuId}, -5, 'اختبار', {approvalId}, 'QUICK', {userId})", "23514");
        // بلا اعتماد → adjust_approval_required (قيد V003 الحاسم)
        await ExpectViolationAsync(
            $"INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, adjustment_kind, created_by) " +
            $"VALUES ({warehouseId}, {skuId}, 5, 'اختبار بلا اعتماد', 'QUICK', {userId})", "23502", "23514");

        // السالم مقبول بصنف التسوية الصحيح ويلتزم
        await using (var good = new NpgsqlCommand(
            $"INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, approval_id, adjustment_kind, created_by, posted_at) " +
            $"VALUES ({warehouseId}, {skuId}, 5, 'اختبار سليم', {approvalId}, 'DAMAGE', {userId}, now())", conn, null))
        {
            await good.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task V017_CountApprovedAt_ColumnExists()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='inv' AND table_name='count_sessions' AND column_name='approved_at'", conn);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 1, "عمود approved_at مفقود");
    }

    [Fact]
    public async Task V017_PermissionGrants_MatchTheGateMatrix()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        long Count(string permissionCode, string roleCodesCsv)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT COUNT(DISTINCT r.code) FROM core.roles r " +
                "JOIN core.role_permissions rp ON rp.role_id = r.role_id " +
                "JOIN core.permissions p ON p.permission_id = rp.permission_id " +
                "WHERE p.code = @p AND r.code = ANY(@roles)", conn);
            cmd.Parameters.AddWithValue("p", permissionCode);
            cmd.Parameters.AddWithValue("roles", roleCodesCsv.Split(','));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        Assert.Equal(7, Count("inventory.read", "OWNER,MANAGER,SUPERVISOR,CASHIER,STOREKEEPER,ACCOUNTANT,AUDITOR"));
        Assert.Equal(7, Count("purchasing.read", "OWNER,MANAGER,SUPERVISOR,CASHIER,STOREKEEPER,ACCOUNTANT,AUDITOR"));
        Assert.Equal(2, Count("inventory.warehouse.manage", "OWNER,MANAGER"));
        Assert.Equal(3, Count("inventory.stocktake.run", "OWNER,MANAGER,STOREKEEPER"));
        Assert.Equal(2, Count("inventory.stocktake.approve", "OWNER,MANAGER"));
        Assert.Equal(3, Count("inventory.reservation.manage", "OWNER,MANAGER,STOREKEEPER"));
        Assert.Equal(3, Count("purchasing.manage", "OWNER,MANAGER,STOREKEEPER"));
        Assert.Equal(3, Count("purchasing.receive", "OWNER,MANAGER,STOREKEEPER"));
        Assert.Equal(3, Count("purchasing.invoice.manage", "OWNER,MANAGER,ACCOUNTANT"));
        Assert.Equal(2, Count("purchasing.payment.post", "OWNER,ACCOUNTANT"));
        Assert.Equal(3, Count("purchasing.return.post", "OWNER,MANAGER,STOREKEEPER"));
        Assert.Equal(2, Count("suppliers.manage", "OWNER,MANAGER"));
        Assert.Equal(2, Count("customers.manage", "OWNER,MANAGER"));
        Assert.Equal(5, Count("parties.read", "OWNER,MANAGER,STOREKEEPER,ACCOUNTANT,AUDITOR"));
        Assert.Equal(3, Count("security.approval.decide", "OWNER,MANAGER,SUPERVISOR"));
        Assert.Equal(3, Count("finance.treasury.read", "OWNER,MANAGER,ACCOUNTANT"));
    }
}
