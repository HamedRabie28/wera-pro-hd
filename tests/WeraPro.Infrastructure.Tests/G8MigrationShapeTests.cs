namespace WeraPro.Infrastructure.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WeraPro.Infrastructure.Migrations;
using Xunit;

/// <summary>
/// فحوص شكل ترحيل V019 (G8) على قاعدة فريدة + المشغلات الحرجة: آلة إغلاق الوردية
/// وتجميدها، أرجل السندات المؤجلة (لا ترحيل بأثر ناقص)، حراسة توزيعات سند القبض
/// (≤ المتبقي)، آلة المصروف وعكسه الموثق، حدث رأس المال والافتتاحي باعتماد،
/// فهرس منع الأثر المالي المكرر، درج المحطة، القوالب الستة عشر وصلاحيات G8.
/// </summary>
public sealed class G8MigrationShapeTests : IAsyncLifetime
{
    private readonly string _scratchDb = $"wera_g8_scratch_{Guid.NewGuid().ToString("N")[..10]}";
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

    private static long Scalar(string sql, NpgsqlConnection conn, NpgsqlTransaction? tx = null)
    {
        using var cmd = tx is null ? new NpgsqlCommand(sql, conn) : new NpgsqlCommand(sql, conn, tx);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>فشل الالتزام يُكمل المعاملة تلقائياً — التخلص الآمن من مقبضها دون رمية ثانية.</summary>
    private static async Task SafeDisposeAsync(NpgsqlTransaction tx)
    {
        try
        {
            await tx.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // المعاملة مكتملة/ملغاة بعد فشل الالتزام — لا شيء للتراجع عنه
        }
    }

    private static void ExpectTxError(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string substring)
    {
        using (var sp = new NpgsqlCommand("SAVEPOINT g8step", conn, tx))
        {
            sp.ExecuteNonQuery();
        }
        var ex = Assert.Throws<PostgresException>(() =>
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.ExecuteNonQuery();
        });
        using (var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT g8step", conn, tx))
        {
            rb.ExecuteNonQuery();
        }
        Assert.Contains(substring, ex.Message);
    }

    private async Task<(long BranchId, long StationId, long UserId, long OtherUserId, long DrawerId, long SafeId, long ShiftId, long CategoryId, long CustomerId)>
        SeedG8ChainAsync()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var userId = Scalar("INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('g8ms.u1', 'مستخدم G8-1', 'x', true) RETURNING user_id", conn, tx);
        var otherUserId = Scalar("INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('g8ms.u2', 'مستخدم G8-2', 'x', true) RETURNING user_id", conn, tx);
        var branchId = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G8MS-B', 'فرع فحص V019', 'Africa/Cairo') RETURNING branch_id", conn, tx);
        var stationId = Scalar($"INSERT INTO core.stations (branch_id, code, name_ar) VALUES ({branchId}, 'G8MS-ST', 'محطة فحص G8') RETURNING station_id", conn, tx);
        var drawerId = Scalar($"INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, station_id) VALUES ('G8MS-DRW', 'درج الفحص', 'CASH_DRAWER', {branchId}, {stationId}) RETURNING treasury_account_id", conn, tx);
        var safeId = Scalar($"INSERT INTO finance.treasury_accounts (code, name_ar, account_kind) VALUES ('G8MS-SAFE', 'خزينة الفحص', 'SAFE') RETURNING treasury_account_id", conn, tx);
        var shiftId = Scalar($"INSERT INTO sales.shifts (station_id, opened_by, status, opening_float) VALUES ({stationId}, {userId}, 'OPEN', 200) RETURNING shift_id", conn, tx);
        var categoryId = Scalar("INSERT INTO finance.expense_categories (code, name_ar) VALUES ('G8MS-CAT', 'فئة فحص') RETURNING category_id", conn, tx);
        var customerId = Scalar("INSERT INTO sales.customers (code, name_ar, credit_enabled, credit_limit) VALUES ('G8MS-CUST', 'عميل فحص G8', true, 1000) RETURNING customer_id", conn, tx);

        await tx.CommitAsync();
        return (branchId, stationId, userId, otherUserId, drawerId, safeId, shiftId, categoryId, customerId);
    }

    // ═══ 1) شكل V019: جداول وأعمدة وفهارس ومشغلات وقوالب وصلاحيات ═══════════

    [Fact]
    public async Task T01_Shape_TablesColumnsIndexesTriggers()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        foreach (var table in new[] { "finance.shift_cash_counts", "finance.voucher_allocations" })
        {
            var count = Scalar($"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema||'.'||table_name = '{table}'", conn);
            Assert.True(count == 1, $"الجدول {table} مفقود");
        }
        foreach (var (schema, table, column) in new[]
        {
            ("sales", "shifts", "variance_settled_by_voucher_id"),
            ("finance", "treasury_transactions", "shift_id"),
            ("finance", "treasury_accounts", "station_id"),
            ("finance", "expenses", "shift_id"),
            ("finance", "vouchers", "shift_id"),
        })
        {
            var count = Scalar($"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='{schema}' AND table_name='{table}' AND column_name='{column}'", conn);
            Assert.True(count == 1, $"العمود {schema}.{table}.{column} مفقود");
        }
        foreach (var index in new[] { "uq_treasury_txn_source", "uq_treasury_drawer_per_station", "idx_treasury_txn_shift", "idx_expenses_shift", "idx_vouchers_shift", "idx_shift_cash_counts_shift", "idx_voucher_allocations_invoice" })
        {
            var count = Scalar($"SELECT COUNT(*) FROM pg_indexes WHERE schemaname IN ('finance','sales') AND indexname='{index}'", conn);
            Assert.True(count == 1, $"الفهرس {index} مفقود");
        }
        foreach (var trigger in new[]
        {
            "trg_shift_close_validate", "trg_voucher_lifecycle", "trg_voucher_posted_legs",
            "trg_voucher_allocation_guard", "trg_voucher_allocations_frozen",
            "trg_expense_lifecycle", "trg_expense_financials_frozen", "trg_expense_posted_leg",
            "trg_capital_lifecycle", "trg_capital_posted_leg",
        })
        {
            var count = Scalar($"SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_name='{trigger}'", conn);
            Assert.True(count >= 1, $"المشغل {trigger} مفقود");
        }
    }

    [Fact]
    public async Task T02_Shape_TemplatesAndPrintRulesAndSeeds()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        // 16 قالباً مستقلاً (8 مستندات × 80/58) — بوابة القبول: «قوالب 58/80 للسندات والرواتب»
        var templates = Scalar("""
            SELECT COUNT(*) FROM print.document_templates
             WHERE document_type IN ('FINANCE_RECEIPT_VOUCHER','FINANCE_DISBURSEMENT_VOUCHER','FINANCE_TRANSFER_VOUCHER',
                                     'EXPENSE_VOUCHER','PAYROLL_SLIP','PAYROLL_PAYMENT_VOUCHER',
                                     'CASHIER_SALES_APPENDIX','SHIFT_CLOSE_SUMMARY')
               AND media_class IN ('ROLL_80','ROLL_58') AND version = 1 AND is_active
            """, conn);
        Assert.Equal(16L, templates);

        // قوالب الرواتب موسومة سرية وتوجيهها للطابعة المؤمنة فقط
        var confidential = Scalar("""
            SELECT COUNT(*) FROM print.document_templates
             WHERE document_type IN ('PAYROLL_SLIP','PAYROLL_PAYMENT_VOUCHER')
               AND media_class IN ('ROLL_80','ROLL_58')
               AND (body_definition->>'privacy') = 'CONFIDENTIAL_PAYROLL_ONLY'
            """, conn);
        Assert.Equal(4L, confidential);

        // قواعد الطباعة: السندات صريحة على المالية، وملخص الإقفال صريح (§29.10)
        var rules = Scalar("""
            SELECT COUNT(*) FROM print.auto_print_rules
             WHERE document_type IN ('FINANCE_RECEIPT_VOUCHER','FINANCE_DISBURSEMENT_VOUCHER',
                                     'FINANCE_TRANSFER_VOUCHER','SHIFT_CLOSE_SUMMARY')
               AND policy = 'EXPLICIT_USER'
            """, conn);
        Assert.Equal(4L, rules);

        // البذور: خزينة وبنك مرجعيان + 6 فئات مصروفات + تسلسلات
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM finance.treasury_accounts WHERE code IN ('MAIN_SAFE','MAIN_BANK') AND is_active", conn));
        Assert.Equal(6L, Scalar("SELECT COUNT(*) FROM finance.expense_categories WHERE code IN ('RENT','UTILITIES','MAINTENANCE','CLEANING','TRANSPORT','MISC_OPS')", conn));
        foreach (var seq in new[] { "finance.voucher_number_seq", "finance.expense_number_seq", "finance.capital_event_number_seq" })
        {
            Assert.Equal(1L, Scalar($"SELECT COUNT(*) FROM information_schema.sequences WHERE sequence_schema||'.'||sequence_name = '{seq}'", conn));
        }
    }

    [Fact]
    public async Task T03_Shape_G8PermissionGrants()
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        // مصفوفة V019 كما هي: 14 إسناداً
        var grants = Scalar("""
            SELECT COUNT(*) FROM core.role_permissions rp
              JOIN core.roles r ON r.role_id = rp.role_id
              JOIN core.permissions p ON p.permission_id = rp.permission_id
             WHERE p.code IN ('sales.shift.close','sales.shift.approve_variance','finance.treasury.manage','finance.expense.create')
            """, conn);
        Assert.Equal(14L, grants);

        // الكاشير: إغلاق وإنشاء مصروف فقط — لا سندات ولا اعتماد فوارق ولا إدارة خزائن
        Assert.Equal(2L, Scalar("""
            SELECT COUNT(*) FROM core.role_permissions rp
              JOIN core.roles r ON r.role_id = rp.role_id
              JOIN core.permissions p ON p.permission_id = rp.permission_id
             WHERE r.code='CASHIER' AND p.code IN ('sales.shift.close','sales.shift.approve_variance','finance.treasury.manage','finance.expense.create')
            """, conn));
        // الكاشير بلا صلاحيات مالية من V009 إطلاقاً
        Assert.Equal(0L, Scalar("""
            SELECT COUNT(*) FROM core.role_permissions rp
              JOIN core.roles r ON r.role_id = rp.role_id
              JOIN core.permissions p ON p.permission_id = rp.permission_id
             WHERE r.code='CASHIER' AND p.code IN ('finance.voucher.create','finance.voucher.post','finance.expense.approve','finance.expense.post','finance.capital.manage','finance.treasury.view')
            """, conn));
    }

    // ═══ 2) آلة إغلاق الوردية (§14.1) ═══════════════════════════════════

    [Fact]
    public async Task T04_ShiftClose_ValidationAndFreeze()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // إغلاق بلا عدّ → رفض
        ExpectTxError(conn, tx, $"UPDATE sales.shifts SET status='CLOSED' WHERE shift_id={seed.ShiftId}", "SHIFT_CLOSE_COUNT_REQUIRED");
        // عدّ بفارق بلا سبب → رفض (الفارق −5)
        ExpectTxError(conn, tx, $"""
            UPDATE sales.shifts SET status='CLOSED', counted_cash=195, expected_cash=200,
                   closed_by={seed.UserId}, closed_at=now() WHERE shift_id={seed.ShiftId}
            """, "SHIFT_VARIANCE_REASON_REQUIRED");
        // إغلاق صحيح بفارق وسبب → يمر
        using (var ok = new NpgsqlCommand($"""
            UPDATE sales.shifts SET status='CLOSED', counted_cash=195, expected_cash=200,
                   variance_reason='فرق عدّ موثق', closed_by={seed.UserId}, closed_at=now()
             WHERE shift_id={seed.ShiftId}
            """, conn, tx))
        {
            Assert.Equal(1, ok.ExecuteNonQuery());
        }
        // تعديل مالي على المغلقة → رفض
        ExpectTxError(conn, tx, $"UPDATE sales.shifts SET counted_cash=999 WHERE shift_id={seed.ShiftId}", "SHIFT_CLOSED_IMMUTABLE");
        // إعادة فتح → رفض
        ExpectTxError(conn, tx, $"UPDATE sales.shifts SET status='OPEN' WHERE shift_id={seed.ShiftId}", "SHIFT_CLOSED_IMMUTABLE");
        // حذف وردية → ممنوع دائماً
        ExpectTxError(conn, tx, $"DELETE FROM sales.shifts WHERE shift_id={seed.ShiftId}", "SHIFT_IMMUTABLE");

        await tx.CommitAsync();
    }

    [Fact]
    public async Task T05_ShiftCashCounts_TableShape()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var countId = Scalar($"INSERT INTO finance.shift_cash_counts (shift_id, counted_cash, counted_by, notes) VALUES ({seed.ShiftId}, 195.50, {seed.UserId}, 'عدة أولى') RETURNING count_id", conn, tx);
        Assert.True(countId > 0);
        // عدّة سالبة → مرفوضة
        ExpectTxError(conn, tx, $"INSERT INTO finance.shift_cash_counts (shift_id, counted_cash, counted_by) VALUES ({seed.ShiftId}, -5, {seed.UserId})", "counted_cash");
        await tx.CommitAsync();
    }

    // ═══ 3) السندات: الأرجل المؤجلة ومنع التكرار (§14.2) ════════════════

    [Fact]
    public async Task T06_VoucherPostingLegs_DeferredEnforcement()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        // السند يُنشأ خارج المعاملة الفاشلة — فشل الترحيل الأول لا يمس مستنده (لا حذف ضمنياً)
        var voucherId = Scalar($"""
            INSERT INTO finance.vouchers (voucher_number, voucher_type, treasury_account_id, amount, reason, status, created_by, shift_id)
            VALUES ('G8MS-DV1', 'DISBURSEMENT', {seed.SafeId}, 60, 'صرف نظافة وردية الفحص', 'DRAFT', {seed.UserId}, {seed.ShiftId})
            RETURNING voucher_id
            """, conn);

        // ترحيل بلا أثر → يُرفض عند COMMIT (المشغل المؤجل) — والمسودة تبقى سليمة
        await using var tx = await conn.BeginTransactionAsync();
        using (var post = new NpgsqlCommand($"UPDATE finance.vouchers SET status='POSTED', posted_at=now() WHERE voucher_id={voucherId}", conn, tx))
        {
            post.ExecuteNonQuery();
        }
        var commitEx = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Contains("VOUCHER_POST_LEG_INVALID", commitEx.Message);
        await SafeDisposeAsync(tx);

        // الترحيل الصحيح: حركة واحدة مطابقة — وسياق الوردية محفوظ عليها
        await using var tx2 = await conn.BeginTransactionAsync();
        using (var post = new NpgsqlCommand($"UPDATE finance.vouchers SET status='POSTED', posted_at=now() WHERE voucher_id={voucherId}", conn, tx2))
        {
            post.ExecuteNonQuery();
        }
        using (var leg = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id, shift_id)
            VALUES ({seed.SafeId}, -1, 60, 'finance.voucher', {voucherId}, 'سند صرف فحص', {seed.UserId}, {seed.BranchId}, {seed.ShiftId})
            """, conn, tx2))
        {
            leg.ExecuteNonQuery();
        }
        tx2.Commit();

        // أثر مكرر لنفس السند على نفس الخزينة → الفهرس الفريد يمنع (لا ترحيل مكرر)
        var dupEx = Assert.Throws<PostgresException>(() =>
        {
            using var dup = new NpgsqlCommand($"""
                INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id)
                VALUES ({seed.SafeId}, -1, 60, 'finance.voucher', {voucherId}, 'مكرر', {seed.UserId}, {seed.BranchId})
                """, conn);
            dup.ExecuteNonQuery();
        });
        Assert.Equal("23505", dupEx.SqlState);

        // السند المرحَّل نهائي: لا تعديل ولا حذف
        Assert.Throws<PostgresException>(() =>
        {
            using var upd = new NpgsqlCommand($"UPDATE finance.vouchers SET amount=99 WHERE voucher_id={voucherId}", conn);
            upd.ExecuteNonQuery();
        });
        Assert.Throws<PostgresException>(() =>
        {
            using var del = new NpgsqlCommand($"DELETE FROM finance.vouchers WHERE voucher_id={voucherId}", conn);
            del.ExecuteNonQuery();
        });
    }

    [Fact]
    public async Task T07_VoucherTransfer_TwoLegsEnforced()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        // السند خارج المعاملة الفاشلة — المحاولة الناقصة تُرفض وتبقى المسودة
        var voucherId = Scalar($"""
            INSERT INTO finance.vouchers (voucher_number, voucher_type, treasury_account_id, counter_treasury_account_id, amount, reason, status, created_by)
            VALUES ('G8MS-TV1', 'TRANSFER', {seed.DrawerId}, {seed.SafeId}, 150, 'إيداع فائض الدرج في الخزينة', 'DRAFT', {seed.UserId})
            RETURNING voucher_id
            """, conn);

        // رجل واحدة فقط (بلا رجل الوجهة) → الالتزام يُرفض
        await using var tx = await conn.BeginTransactionAsync();
        using (var post = new NpgsqlCommand($"UPDATE finance.vouchers SET status='POSTED', posted_at=now() WHERE voucher_id={voucherId}", conn, tx))
        {
            post.ExecuteNonQuery();
        }
        using (var leg = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id)
            VALUES ({seed.DrawerId}, -1, 150, 'finance.voucher', {voucherId}, 'سحب من الدرج', {seed.UserId}, {seed.BranchId})
            """, conn, tx))
        {
            leg.ExecuteNonQuery();
        }
        var ex = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Contains("VOUCHER_TRANSFER_LEGS_INVALID", ex.Message);
        await SafeDisposeAsync(tx);

        // الرجلان المتقابلتان تمر — والمجموع الكلي للخزائن لا يتغير (لا إيراد ولا مصروف)
        await using var tx2 = await conn.BeginTransactionAsync();
        using (var post = new NpgsqlCommand($"UPDATE finance.vouchers SET status='POSTED', posted_at=now() WHERE voucher_id={voucherId}", conn, tx2))
        {
            post.ExecuteNonQuery();
        }
        using (var outLeg = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id, shift_id)
            VALUES ({seed.DrawerId}, -1, 150, 'finance.voucher', {voucherId}, 'إيداع من الدرج', {seed.UserId}, {seed.BranchId}, {seed.ShiftId})
            """, conn, tx2))
        {
            outLeg.ExecuteNonQuery();
        }
        using (var inLeg = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id)
            VALUES ({seed.SafeId}, 1, 150, 'finance.voucher', {voucherId}, 'إيداع في الخزينة', {seed.UserId}, {seed.BranchId})
            """, conn, tx2))
        {
            inLeg.ExecuteNonQuery();
        }
        tx2.Commit();

        var net = Scalar($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE source_doc_type='finance.voucher' AND source_doc_id={voucherId}", conn);
        Assert.Equal(0L, net); // صفر كقيمة صحيحة: التحويل لا يولد إيراداً ولا مصروفاً
    }

    // ═══ 4) توزيع سند القبض ≤ المتبقي (§13/PRT-003) ═════════════════════

    [Fact]
    public async Task T08_VoucherAllocation_Guarded()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // فاتورة آجلة مرحّلة 300 لعميل الفحص (مستحقة الدفع آجلاً)
        var invoiceId = Scalar($"""
            INSERT INTO sales.invoices (invoice_number, branch_id, station_id, shift_id, cashier_user_id, customer_id, status, currency, tax_mode_snapshot,
                                        gross_total, line_discount_total, invoice_discount, tax_total, net_total, paid_total, created_at, posted_at)
            VALUES ('G8MS-INV1', {seed.BranchId}, {seed.StationId}, {seed.ShiftId}, {seed.UserId}, {seed.CustomerId}, 'POSTED', 'EGP', 'VAT_INCLUSIVE',
                    300, 0, 0, 36.84, 263.16, 0, now(), now())
            RETURNING invoice_id
            """, conn, tx);
        Scalar($"""
            INSERT INTO sales.payments (invoice_id, payment_method, amount, status, received_at, created_by)
            VALUES ({invoiceId}, 'CREDIT', 300, 'CAPTURED', now(), {seed.UserId})
            """, conn, tx);

        var voucherId = Scalar($"""
            INSERT INTO finance.vouchers (voucher_number, voucher_type, treasury_account_id, amount, counterparty_type, counterparty_id, reason, status, created_by)
            VALUES ('G8MS-RV1', 'RECEIPT', {seed.SafeId}, 250, 'CUSTOMER', {seed.CustomerId}, 'تحصيل دفعة عميل الفحص', 'DRAFT', {seed.UserId})
            RETURNING voucher_id
            """, conn, tx);

        // توزيع ضمن المتبقي (250 ≤ 300) → يمر
        Scalar($"INSERT INTO finance.voucher_allocations (voucher_id, invoice_id, amount) VALUES ({voucherId}, {invoiceId}, 250) RETURNING allocation_id", conn, tx);
        // سند قبض ثانٍ (100) لتوزيع يتجاوز متبقي الفاتورة (المتبقي 50 والتوزيع 60) → رفض
        var voucher2 = Scalar($"""
            INSERT INTO finance.vouchers (voucher_number, voucher_type, treasury_account_id, amount, counterparty_type, counterparty_id, reason, status, created_by)
            VALUES ('G8MS-RV2', 'RECEIPT', {seed.SafeId}, 100, 'CUSTOMER', {seed.CustomerId}, 'دفعة ثانية', 'DRAFT', {seed.UserId})
            RETURNING voucher_id
            """, conn, tx);
        ExpectTxError(conn, tx, $"INSERT INTO finance.voucher_allocations (voucher_id, invoice_id, amount) VALUES ({voucher2}, {invoiceId}, 60)", "ALLOCATION_EXCEEDS_REMAINING");
        // مجموع توزيعات السند فوق مبلغه (سند 250 وتوزيعه 250 — توزيع إضافي 10) → رفض
        ExpectTxError(conn, tx, $"INSERT INTO finance.voucher_allocations (voucher_id, invoice_id, amount) VALUES ({voucherId}, {invoiceId}, 10)", "VOUCHER_OVER_ALLOCATED");
        await tx.CommitAsync();
    }

    // ═══ 5) المصروفات: الاعتماد ثم السداد والعكس الموثق (§14.4) ══════════

    [Fact]
    public async Task T09_ExpenseLifecycle_ReversalLeavesNetZero()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var expenseId = Scalar($"""
            INSERT INTO finance.expenses (expense_number, category_id, branch_id, expense_date, amount, tax_amount, payment_method, treasury_account_id, status, created_by, shift_id)
            VALUES ('G8MS-EX1', {seed.CategoryId}, {seed.BranchId}, CURRENT_DATE, 100, 14, 'CASH', {seed.DrawerId}, 'DRAFT', {seed.UserId}, {seed.ShiftId})
            RETURNING expense_id
            """, conn, tx);

        // ترحيل مباشر لمصروف وردية من درج المحطة (القاعدة الموثقة G8): DRAFT→POSTED بأثر (100+14)
        using (var post = new NpgsqlCommand($"UPDATE finance.expenses SET status='POSTED', posted_at=now() WHERE expense_id={expenseId}", conn, tx))
        {
            post.ExecuteNonQuery();
        }
        using (var leg = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id, shift_id)
            VALUES ({seed.DrawerId}, -1, 114, 'finance.expense', {expenseId}, 'مصروف وردية فحص', {seed.UserId}, {seed.BranchId}, {seed.ShiftId})
            """, conn, tx))
        {
            leg.ExecuteNonQuery();
        }
        tx.Commit();

        // حذف المصروف المرحّل ممنوع دائماً — والإلغاء بلا سبب مرفوض (§14.4)
        await using var txPosted = await conn.BeginTransactionAsync();
        ExpectTxError(conn, txPosted, $"DELETE FROM finance.expenses WHERE expense_id={expenseId}", "EXPENSE_IMMUTABLE");
        ExpectTxError(conn, txPosted, $"UPDATE finance.expenses SET status='CANCELLED' WHERE expense_id={expenseId}", "EXPENSE_REVERSAL_REASON_REQUIRED");
        await txPosted.RollbackAsync();   // فحوص سالبة فقط — لا تغييرات لتثبيتها

        // الإلغاء الموثق: حركة عكسية (finance.expense.reversal) — الأثر الصافي صفر عند الالتزام
        await using var tx2 = await conn.BeginTransactionAsync();
        using (var cancel = new NpgsqlCommand($"UPDATE finance.expenses SET status='CANCELLED', notes='إلغاء: خطأ في الفئة' WHERE expense_id={expenseId}", conn, tx2))
        {
            cancel.ExecuteNonQuery();
        }
        using (var reverse = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id)
            VALUES ({seed.DrawerId}, 1, 114, 'finance.expense.reversal', {expenseId}, 'عكس إلغاء مصروف فحص', {seed.UserId}, {seed.BranchId})
            """, conn, tx2))
        {
            reverse.ExecuteNonQuery();
        }
        tx2.Commit();

        var net = Scalar($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE source_doc_id={expenseId} AND source_doc_type IN ('finance.expense','finance.expense.reversal')", conn);
        Assert.Equal(0m, Convert.ToDecimal(net));
    }

    // ═══ 6) رأس المال: الافتتاحي باعتماد فقط (§14.3/D-14) ═══════════════

    [Fact]
    public async Task T10_CapitalOpeningBalance_RequiresApproval()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // افتتاحي بلا اعتماد → قيد V006 يرفض عند الإدراج
        ExpectTxError(conn, tx, $"""
            INSERT INTO finance.capital_events (event_number, event_type, amount, treasury_account_id, event_date, description, status, created_by)
            VALUES ('G8MS-CE1', 'OPENING_BALANCE', 50000, {seed.SafeId}, CURRENT_DATE, 'رصيد افتتاحي بلا اعتماد', 'DRAFT', {seed.UserId})
            """, "capital_opening_needs_approval");

        // اعتماد ثنائي موثق (الطالب ≠ المعتمد — no_self)
        var approvalId = Scalar($"""
            INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, reason, decided_at)
            VALUES ('finance.capital_event', 0, {seed.OtherUserId}, {seed.UserId}, 'APPROVED', 'اعتماد الرصيد الافتتاحي للفحص', now())
            RETURNING approval_id
            """, conn, tx);

        // بالاعتماد يُنشأ المسودة
        var capitalId = Scalar($"""
            INSERT INTO finance.capital_events (event_number, event_type, amount, treasury_account_id, event_date, description, status, approval_id, created_by)
            VALUES ('G8MS-CE2', 'OPENING_BALANCE', 50000, {seed.SafeId}, CURRENT_DATE, 'رصيد افتتاحي معتمد للفحص', 'DRAFT', {approvalId}, {seed.OtherUserId})
            RETURNING capital_id
            """, conn, tx);

        // ترحيل الافتتاحي بأثر قبض واحد (+50000 على الخزينة) عند الالتزام
        using (var post = new NpgsqlCommand($"UPDATE finance.capital_events SET status='POSTED', posted_at=now() WHERE capital_id={capitalId}", conn, tx))
        {
            post.ExecuteNonQuery();
        }
        using (var leg = new NpgsqlCommand($"""
            INSERT INTO finance.treasury_transactions (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id)
            VALUES ({seed.SafeId}, 1, 50000, 'finance.capital_event', {capitalId}, 'رصيد افتتاحي معتمد', {seed.OtherUserId}, {seed.BranchId})
            """, conn, tx))
        {
            leg.ExecuteNonQuery();
        }
        tx.Commit();

        // المرحَّل موثق نهائي: لا تعديل ولا حذف
        Assert.Throws<PostgresException>(() =>
        {
            using var upd = new NpgsqlCommand($"UPDATE finance.capital_events SET amount=1 WHERE capital_id={capitalId}", conn);
            upd.ExecuteNonQuery();
        });
        Assert.Throws<PostgresException>(() =>
        {
            using var del = new NpgsqlCommand($"DELETE FROM finance.capital_events WHERE capital_id={capitalId}", conn);
            del.ExecuteNonQuery();
        });
    }

    // ═══ 7) درج المحطة (V019 — الحقيقة الفيزيائية) ══════════════════════

    [Fact]
    public async Task T11_OneDrawerAccountPerStation()
    {
        var seed = await SeedG8ChainAsync();
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();

        // درج ثانٍ لنفس المحطة → الفهرس الفريد الجزئي يمنع
        var ex = Assert.Throws<PostgresException>(() =>
        {
            using var dup = new NpgsqlCommand($"INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, station_id) VALUES ('G8MS-DRW2', 'درج مكرر', 'CASH_DRAWER', {seed.BranchId}, {seed.StationId})", conn);
            dup.ExecuteNonQuery();
        });
        Assert.Equal("23505", ex.SqlState);

        // بذر V019: درج لكل محطة قائمة بلا درج (المحطة المزروعة في هذا الفحص حصلت على درجها يدوياً)
        var drawers = Scalar($"SELECT COUNT(*) FROM finance.treasury_accounts WHERE station_id={seed.StationId}", conn);
        Assert.Equal(1L, drawers);
    }
}
