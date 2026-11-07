namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

/// <summary>
/// اختبارات بوابة G8 (§22): الورديات والنقد والأدراج والمصروفات ورأس المال —
/// سيناريوهات مطابقة بأرقام مرجعية مستقلة (بوابة القبول: النقد المتوقع يطابق قواعد
/// الحركات · فصل النقد عن البطاقة والآجل · لا ترحيل مكرر · الفارق يبقى ظاهراً حتى
/// التسوية) + قوالب السندات والرواتب وخصوصية الرواتب (لا طابعة عامة) + RBAC.
/// </summary>
public sealed class G8EndpointTests : IClassFixture<G8ApiFixture>
{
    private readonly G8ApiFixture _fx;

    public G8EndpointTests(G8ApiFixture fx) => _fx = fx;

    private async Task<HttpClient> ClientAsync(string username, string password, bool withStation = true, long? stationId = null, long? branchId = null)
    {
        var login = await _fx.LoginAsync(username, password, branchId ?? _fx.BranchId, stationId ?? (withStation ? _fx.StationId : null));
        var client = _fx.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<decimal> ScalarDecimalAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0m : Convert.ToDecimal(result);
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToString(result) ?? string.Empty;
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"STATUS={(int)response.StatusCode} BODY={body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string expectedCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"توقعت {(int)expected} وحصلت {(int)response.StatusCode} BODY={body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        return root;
    }

    private static object CashSaleLine(long skuId, decimal qty) => new { skuId, qty };

    private static object CashPayment(decimal amount, decimal? tendered = null) =>
        tendered is null ? new { method = "CASH", amount } : new { method = "CASH", amount, cashTendered = tendered };

    /// <summary>يفتح وردية جديدة للمحطة بعد إغلاق أي وردية مفتوحة (بمطابقتها بلا فارق) — عزل الاختبارات.</summary>
    private async Task<long> OpenFreshShiftAsync(decimal openingFloat)
    {
        using var supervisor = await ClientAsync("g8.supervisor", "Supervisor#G8-Pass");
        var current = await OkAsync(await supervisor.GetAsync("/api/v1/pos/shifts/current"));
        if (current.TryGetProperty("shiftId", out var openId))
        {
            var rec = await OkAsync(await supervisor.GetAsync($"/api/v1/pos/shifts/{openId.GetInt64()}/reconciliation"));
            await OkAsync(await supervisor.PostAsJsonAsync($"/api/v1/pos/shifts/{openId.GetInt64()}/close",
                new { countedCash = rec.GetProperty("expectedInDrawer").GetDecimal() }));
        }
        using var cashier = await ClientAsync("g8.cashier", "Cashier#G8-Pass");
        var opened = await OkAsync(await cashier.PostAsJsonAsync("/api/v1/pos/shifts", new { openingFloat }));
        return opened.GetProperty("shiftId").GetInt64();
    }

    // ═══ 1) السيناريو المرجعي الكامل: وردية بمطابقة تامة بأرقام مستقلة (بوابة القبول) ══

    [Fact]
    public async Task T01_FullShiftScenario_ExpectedMatchesLedger_IndependentNumbers()
    {
        var shiftId = await OpenFreshShiftAsync(200m);
        using var cashier = await ClientAsync("g8.cashier", "Cashier#G8-Pass");
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");
        var drawerBefore = await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.DrawerAccountId}");

        // (أ) بيع نقدي: 20 كيساً × 5.00 = 100.00 شامل 14% → ضريبة 12.28 · صافي 87.72 (مرجع مستقل من G7)
        var cashSale = await OkAsync(await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 20m) },
            payments = new[] { CashPayment(100.00m) },
            idempotencyKey = $"g8-t01-cash-{Guid.NewGuid():N}",
        }));
        Assert.Equal(100.00m, cashSale.GetProperty("totals").GetProperty("dueAmount").GetDecimal());
        Assert.Equal(12.28m, cashSale.GetProperty("totals").GetProperty("taxTotal").GetDecimal());

        // (ب) بيع بطاقة: 11 كيساً = 55.00 (ضريبة 6.75 · صافي 48.25) — لا يدخل الدرج
        var cardSale = await OkAsync(await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 11m) },
            payments = new[] { new { method = "CARD", amount = 55.00m } },
            idempotencyKey = $"g8-t01-card-{Guid.NewGuid():N}",
        }));
        Assert.Equal(55.00m, cardSale.GetProperty("totals").GetProperty("dueAmount").GetDecimal());

        // (ج) بيع آجل: 3 أكياس = 15.00 لعميل مرخص — ذمة لا درج
        var creditSale = await OkAsync(await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 3m) },
            payments = new[] { new { method = "CREDIT", amount = 15.00m } },
            customerId = _fx.CreditCustomerId,
            idempotencyKey = $"g8-t01-credit-{Guid.NewGuid():N}",
        }));
        Assert.Equal(15.00m, creditSale.GetProperty("totals").GetProperty("dueAmount").GetDecimal());

        // (د) مرتجع نقدي جزئي: 5 أكياس = 25.00 من البيع النقدي — الرد بوسيلة الدفع الأصلية (D-04)
        var cashInvoiceId = cashSale.GetProperty("invoiceId").GetInt64();
        var cashLineId = await ScalarAsync($"SELECT line_id FROM sales.invoice_lines WHERE invoice_id={cashInvoiceId} ORDER BY line_no LIMIT 1");
        var refundLines = new[] { new { originalLineId = cashLineId, qty = 5m } };
        var approval = await OkAsync(await owner.PostAsJsonAsync("/api/v1/approvals", new
        {
            entityType = "sales.refund", entityId = cashInvoiceId, reasonAr = "اعتماد مرتجع سيناريو G8",
        }));
        using var decider = await ClientAsync("g8.manager", "Manager#G8-Pass");
        await OkAsync(await decider.PostAsJsonAsync($"/api/v1/approvals/{approval.GetProperty("approvalId").GetInt64()}/decision",
            new { decision = "APPROVED", reasonAr = "اعتماد مرتجع سيناريو G8 — سليم" }));
        var refund = await OkAsync(await owner.PostAsJsonAsync("/api/v1/pos/refunds", new
        {
            originalInvoiceId = cashInvoiceId,
            lines = refundLines,
            refundMethod = "CASH",
            restock = true,
            reasonAr = "عبوات تالفة سيناريو G8",
            approvalId = approval.GetProperty("approvalId").GetInt64(),
            idempotencyKey = $"g8-t01-refund-{Guid.NewGuid():N}",
        }));
        Assert.Equal(25.00m, refund.GetProperty("total").GetDecimal());

        // (هـ) مصروف وردية من الدرج: 20.00 نظافة — القاعدة الموثقة G8 (ترحيل مباشر موثق بورديته)
        var expense = await OkAsync(await cashier.PostAsJsonAsync("/api/v1/finance/expenses", new
        {
            categoryId = _fx.CleaningCategoryId,
            amount = 20.00m,
            taxAmount = 0m,
            paymentMethod = "CASH",
            treasuryAccountId = _fx.DrawerAccountId,
            shiftId,
            beneficiary = "عامل نظافة",
            notesAr = "نظافة نهاية اليوم",
        }));
        Assert.Equal("POSTED", expense.GetProperty("status").GetString());

        // (و) المطابقة قبل الإغلاق: المتوقع من الدفتر — مرجع مستقل: 200 + 100 − 25 − 20 = 255.00
        var reconciliation = await OkAsync(await cashier.GetAsync($"/api/v1/pos/shifts/{shiftId}/reconciliation"));
        Assert.Equal(255.00m, reconciliation.GetProperty("expectedInDrawer").GetDecimal());
        Assert.Equal(200m, reconciliation.GetProperty("openingFloat").GetDecimal());
        Assert.Equal(100m, reconciliation.GetProperty("cashSales").GetDecimal());
        Assert.Equal(25m, reconciliation.GetProperty("cashRefunds").GetDecimal());
        Assert.Equal(20m, reconciliation.GetProperty("drawerExpenses").GetDecimal());
        // فصل الوسائل (بوابة القبول): البطاقة والآجل لا يدخلان متوقع الدرج
        Assert.Equal(55m, reconciliation.GetProperty("cardCollected").GetDecimal());
        Assert.Equal(15m, reconciliation.GetProperty("creditIssued").GetDecimal());

        // (ز) الإغلاق بمطابقة تامة: بلا فارق → لا سبب مطلوباً — والكاشير يجوز له الإغلاق
        var close = await OkAsync(await cashier.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close",
            new { countedCash = 255.00m }));
        Assert.Equal(0m, close.GetProperty("variance").GetDecimal());
        Assert.Equal(255.00m, close.GetProperty("reconciliation").GetProperty("shift").GetProperty("countedCash").GetDecimal());
        Assert.Equal("CLOSED", close.GetProperty("reconciliation").GetProperty("shift").GetProperty("status").GetString());

        // (ح) الدفتر: رصيد درج المحطة ازداد بمقدار 100 − 25 − 20 = 55 (الافتتاحي وردية لا حركة خزينة) —
        //     الفرق عن الرصيد قبل السيناريو (القاعدة مشتركة بين التشغيلات — لا تراكم يقارن مطلقاً)
        Assert.Equal(drawerBefore + 55m, await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.DrawerAccountId}"));
        // كل حركات الدرج تحمل وردية المحطة (المتوقع يطابق قواعد الحركات حرفياً)
        Assert.Equal(3L, await ScalarAsync(
            $"SELECT COUNT(*) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.DrawerAccountId} AND shift_id={shiftId}"));
    }

    // ═══ 2) الإغلاق بفارق: السبب والاعتماد بحسب الدور — والفارق يبقى ظاهراً حتى التسوية ══

    [Fact]
    public async Task T02_VarianceClose_ReasonAndRoleAndSettlement()
    {
        var shiftId = await OpenFreshShiftAsync(50m);
        using var cashier = await ClientAsync("g8.cashier", "Cashier#G8-Pass");
        using var supervisor = await ClientAsync("g8.supervisor", "Supervisor#G8-Pass");
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");

        // بيع نقدي 8 أكياس = 40.00 → المتوقع 50 + 40 = 90.00 (مرجع مستقل)
        await OkAsync(await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 8m) },
            payments = new[] { CashPayment(40.00m) },
            idempotencyKey = $"g8-t02-cash-{Guid.NewGuid():N}",
        }));

        // (أ) إغلاق بعدّ 85 بفارق −5 بلا سبب → رفض موثق
        await ProblemAsync(await cashier.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close",
            new { countedCash = 85m }), HttpStatusCode.Conflict, "SHIFT_VARIANCE_REASON_REQUIRED");

        // (ب) الكاشير لا يعتمد إغلاقاً بفارق (اعتماد بحسب الدور — §14.1)
        await ProblemAsync(await cashier.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close",
            new { countedCash = 85m, varianceReasonAr = "عجز عدّ موثق" }), HttpStatusCode.Forbidden, "SHIFT_VARIANCE_APPROVAL_REQUIRED");

        // (ج) المشرف يعتمد الإغلاق بفارق وسبب → يمر والفارق يبقى ظاهراً
        var close = await OkAsync(await supervisor.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close",
            new { countedCash = 85m, varianceReasonAr = "عجز عدّ موثق — تحت المراجعة" }));
        Assert.Equal(-5m, close.GetProperty("variance").GetDecimal());
        Assert.Equal(-5m, close.GetProperty("reconciliation").GetProperty("shift").GetProperty("variance").GetDecimal());
        Assert.Equal("عجز عدّ موثق — تحت المراجعة", close.GetProperty("reconciliation").GetProperty("shift").GetProperty("varianceReason").GetString());
        Assert.Equal(JsonValueKind.Null, close.GetProperty("reconciliation").GetProperty("shift").GetProperty("varianceSettledByVoucherId").ValueKind); // الفارق بلا تسوية بعد

        // (د) لا إغلاق مرتين (§14.1)
        await ProblemAsync(await supervisor.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close",
            new { countedCash = 85m }), HttpStatusCode.Conflict, "SHIFT_ALREADY_CLOSED");

        // (هـ) القائمة: الفارق يظل ظاهراً على الوردية المغلقة (بوابة القبول)
        var list = await OkAsync(await supervisor.GetAsync($"/api/v1/pos/shifts?stationId={_fx.StationId}&status=CLOSED"));
        var listed = list.GetProperty("items").EnumerateArray().First(s => s.GetProperty("shiftId").GetInt64() == shiftId);
        Assert.Equal(-5m, listed.GetProperty("variance").GetDecimal());
        Assert.NotNull(listed.GetProperty("varianceReason").GetString());

        // (و) التسوية بسند صرف مرحّل بمقدار الفارق: المالك يصرف 5 من الخزينة ويربطها — الفارق يُعالج بمستند
        var voucher = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "DISBURSEMENT",
            treasuryAccountId = _fx.SafeAccountId,
            amount = 5.00m,
            counterpartyType = "OWNER",
            reasonAr = "تسوية عجز عدّ وردية سيناريو G8",
        }));
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/vouchers/{voucher.GetProperty("voucherId").GetInt64()}/post", new { }));
        var settle = await OkAsync(await owner.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/settle-variance",
            new { voucherId = voucher.GetProperty("voucherId").GetInt64() }));
        Assert.True(settle.GetProperty("settled").GetBoolean());

        // مبلغ لا يطابق الفارق → رفض
        var badVoucher = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "DISBURSEMENT", treasuryAccountId = _fx.SafeAccountId, amount = 99m,
            counterpartyType = "OWNER", reasonAr = "سند تسوية بمبلغ خاطئ",
        }));
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/vouchers/{badVoucher.GetProperty("voucherId").GetInt64()}/post", new { }));
        await ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/settle-variance",
            new { voucherId = badVoucher.GetProperty("voucherId").GetInt64() }), HttpStatusCode.Conflict, "SHIFT_ALREADY_SETTLED");

        // بعد التسوية يظل الفارق ظاهراً (المتوقع/الفعلي/السبب) مع سند التسوية المرتبط
        var settled = await OkAsync(await supervisor.GetAsync($"/api/v1/pos/shifts/{shiftId}/reconciliation"));
        Assert.Equal(-5m, settled.GetProperty("variance").GetDecimal());
        Assert.Equal(voucher.GetProperty("voucherId").GetInt64(), settled.GetProperty("varianceSettledByVoucherId").GetInt64());
    }

    // ═══ 3) السندات: التحويل حركتان متقابلتان ولا إيراد ولا مصروف (§14.2) ═════

    [Fact]
    public async Task T03_VoucherTransfer_TwoLegs_NoDoublePosting()
    {
        var shiftId = await OpenFreshShiftAsync(100m);
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");

        var drawerBefore = await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.DrawerAccountId}");
        var safeBefore = await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.SafeAccountId}");

        // (أ) تحويل بلا وجهة → رفض (قيد V006)
        await ProblemAsync(await owner.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "TRANSFER", treasuryAccountId = _fx.DrawerAccountId, amount = 100m,
            reasonAr = "تحويل بلا وجهة مرفوض",
        }), HttpStatusCode.Conflict, "VOUCHER_TRANSFER_ACCOUNTS_INVALID");

        // (ب) تحويل درج → خزينة 100: حركتان متقابلتان (إيداع فائض الدرج)
        var voucher = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "TRANSFER",
            treasuryAccountId = _fx.DrawerAccountId,
            counterTreasuryAccountId = _fx.SafeAccountId,
            amount = 100m,
            reasonAr = "إيداع فائض الدرج في الخزينة",
        }));
        Assert.Equal("DRAFT", voucher.GetProperty("status").GetString());
        var voucherId = voucher.GetProperty("voucherId").GetInt64();

        // الترحيل: أثران — وسياق وردية المحطة محفوظ على رجل الدرج
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/vouchers/{voucherId}/post", new { }));
        Assert.Equal(2L, await ScalarAsync(
            $"SELECT COUNT(*) FROM finance.treasury_transactions WHERE source_doc_type='finance.voucher' AND source_doc_id={voucherId}"));
        Assert.Equal(0m, await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE source_doc_type='finance.voucher' AND source_doc_id={voucherId}")); // لا إيراد ولا مصروف

        // الأرصدة تحركت متقابلة: درج −100 وخزينة +100
        Assert.Equal(drawerBefore - 100m, await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.DrawerAccountId}"));
        Assert.Equal(safeBefore + 100m, await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.SafeAccountId}"));

        // (ج) لا ترحيل مكرر (بوابة القبول)
        await ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/finance/vouchers/{voucherId}/post", new { }),
            HttpStatusCode.Conflict, "VOUCHER_NOT_DRAFT");

        // (د) المتوقع في الدرج نقص 100 بالتحويل: 100 (افتتاحي) − 100 = 0.00 (مرجع مستقل)
        var reconciliation = await OkAsync(await owner.GetAsync($"/api/v1/pos/shifts/{shiftId}/reconciliation"));
        Assert.Equal(0.00m, reconciliation.GetProperty("expectedInDrawer").GetDecimal());
        Assert.Equal(100m, reconciliation.GetProperty("transfersOut").GetDecimal());

        // إغلاق مطابق بعد التحويل (عدّ صفر)
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close", new { countedCash = 0m }));

        // (هـ) المرحَّل نهائي: لا إلغاء — التصحيح بسند معاكس موثق (§14.2)
        await ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/finance/vouchers/{voucherId}/cancel",
            new { reasonAr = "محاولة إلغاء سند مرحّل" }), HttpStatusCode.Conflict, "VOUCHER_NOT_DRAFT");

        // (و) إلغاء مسودة سند آخر يعمل (سبب موثق)
        var draft = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "RECEIPT", treasuryAccountId = _fx.SafeAccountId, amount = 10m,
            counterpartyType = "OTHER", reasonAr = "مسودة ستلغى للاختبار",
        }));
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/vouchers/{draft.GetProperty("voucherId").GetInt64()}/cancel",
            new { reasonAr = "إلغاء للاختبار" }));
    }

    // ═══ 4) سند قبض العميل بتوزيع على الفواتير ≤ المتبقي (§13/PRT-003) ═════════

    [Fact]
    public async Task T04_CustomerReceiptVoucher_AllocationsWithinRemaining()
    {
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");
        using var accountant = await ClientAsync("g8.accountant", "Accountant#G8-Pass", withStation: false);

        // (أ) بيع آجل 40 كيساً = 200.00 لعميل G8 — الذمة 200 (ضريبة 24.56 · صافي 175.44 بمرجع مستقل)
        var creditSale = await OkAsync(await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 40m) },
            payments = new[] { new { method = "CREDIT", amount = 200.00m } },
            customerId = _fx.CreditCustomerId,
            idempotencyKey = $"g8-t04-credit-{Guid.NewGuid():N}",
        }));
        Assert.Equal(200.00m, creditSale.GetProperty("totals").GetProperty("dueAmount").GetDecimal());
        Assert.Equal(24.56m, creditSale.GetProperty("totals").GetProperty("taxTotal").GetDecimal());
        var invoiceId = creditSale.GetProperty("invoiceId").GetInt64();

        // (ب) مديونية العميل: فاتورة آجلة بمتبقي 200.00
        var receivables = await OkAsync(await accountant.GetAsync($"/api/v1/finance/customers/{_fx.CreditCustomerId}/receivables"));
        var open = receivables.EnumerateArray().First(r => r.GetProperty("invoiceId").GetInt64() == invoiceId);
        Assert.Equal(200m, open.GetProperty("remaining").GetDecimal());

        // (ج) سند قبض 150 بتوزيع 150 على الفاتورة (المحاسب — سند من الخزينة الرئيسية)
        var voucher = await OkAsync(await accountant.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "RECEIPT",
            treasuryAccountId = _fx.SafeAccountId,
            amount = 150m,
            counterpartyType = "CUSTOMER",
            counterpartyId = _fx.CreditCustomerId,
            reasonAr = "دفعة عميل سيناريو G8",
            allocations = new[] { new { invoiceId, amount = 150m } },
        }));
        var voucherId = voucher.GetProperty("voucherId").GetInt64();
        await OkAsync(await accountant.PostAsJsonAsync($"/api/v1/finance/vouchers/{voucherId}/post", new { }));

        // (د) المتبقي صار 50 (مرجع مستقل: 200 − 150)
        var after = await OkAsync(await accountant.GetAsync($"/api/v1/finance/customers/{_fx.CreditCustomerId}/receivables"));
        Assert.Equal(50m, after.EnumerateArray().First(r => r.GetProperty("invoiceId").GetInt64() == invoiceId).GetProperty("remaining").GetDecimal());

        // (هـ) توزيع فوق المتبقي → رفض موثق (منع تخصيص أكبر من المتبقي — §13): المتبقي 50 والتوزيع 60
        await ProblemAsync(await accountant.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "RECEIPT", treasuryAccountId = _fx.SafeAccountId, amount = 60m,
            counterpartyType = "CUSTOMER", counterpartyId = _fx.CreditCustomerId,
            reasonAr = "دفعة فوق المتبقي", allocations = new[] { new { invoiceId, amount = 60m } },
        }), HttpStatusCode.Conflict, "ALLOCATION_EXCEEDS_REMAINING");

        // (و) مجموع توزيعات السند فوق مبلغه → رفض (سند 40 وتوزيعان 30+20 — كلٌّ ضمن المتبقي ومجموعهما يتجاوز السند)
        await ProblemAsync(await accountant.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "RECEIPT", treasuryAccountId = _fx.SafeAccountId, amount = 40m,
            counterpartyType = "CUSTOMER", counterpartyId = _fx.CreditCustomerId,
            reasonAr = "توزيع فوق مبلغ السند",
            allocations = new[] { new { invoiceId, amount = 30m }, new { invoiceId, amount = 20m } },
        }), HttpStatusCode.Conflict, "VOUCHER_OVER_ALLOCATED");

        // (ز) التوزيع ليس لغير سند قبض عميل → رفض
        await ProblemAsync(await accountant.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "DISBURSEMENT", treasuryAccountId = _fx.SafeAccountId, amount = 10m,
            counterpartyType = "CUSTOMER", counterpartyId = _fx.CreditCustomerId,
            reasonAr = "توزيع على سند صرف", allocations = new[] { new { invoiceId, amount = 10m } },
        }), HttpStatusCode.Conflict, "VOUCHER_ALLOCATION_NOT_ALLOWED");
    }

    // ═══ 5) المصروف العام: اعتماد ثم ترحيل وأثر واحد وإلغاء بعكس موثق (§14.4) ═══

    [Fact]
    public async Task T05_GeneralExpense_ApproveThenPost_ReversalDocumented()
    {
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");
        using var accountant = await ClientAsync("g8.accountant", "Accountant#G8-Pass", withStation: false);

        // (أ) المحاسب يسجل مصروف بنك 100 + ضريبة 14 = مسدد 114 — مسودة
        var expense = await OkAsync(await accountant.PostAsJsonAsync("/api/v1/finance/expenses", new
        {
            categoryId = _fx.UtilitiesCategoryId,
            amount = 100m,
            taxAmount = 14m,
            paymentMethod = "BANK_TRANSFER",
            treasuryAccountId = _fx.BankAccountId,
            beneficiary = "شركة الكهرباء",
            notesAr = "فاتورة مرافق سيناريو G8",
        }));
        Assert.Equal("DRAFT", expense.GetProperty("status").GetString());
        Assert.Equal(114m, expense.GetProperty("cashOutflow").GetDecimal());
        var expenseId = expense.GetProperty("expenseId").GetInt64();

        // (ب) الترحيل قبل الاعتماد → رفض (اعتماد ثم سداد — §14.4)
        await ProblemAsync(await accountant.PostAsJsonAsync($"/api/v1/finance/expenses/{expenseId}/post", new { }),
            HttpStatusCode.Conflict, "EXPENSE_APPROVAL_REQUIRED");

        // (ج) المالك (≠ المنشئ) يعتمد → معتمد باعتماد موثق
        var approved = await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/expenses/{expenseId}/approve",
            new { reasonAr = "اعتماد فاتورة المرافق" }));
        Assert.Equal("APPROVED", approved.GetProperty("status").GetString());
        Assert.True(approved.GetProperty("approvalId").GetInt64() > 0);

        // (د) الترحيل: أثر مالي واحد 114 على البنك (بوابة القبول: لا ترحيل مكرر)
        var bankBefore = await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.BankAccountId}");
        var posted = await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/expenses/{expenseId}/post", new { }));
        Assert.Equal("POSTED", posted.GetProperty("expense").GetProperty("status").GetString());
        Assert.Equal(bankBefore - 114m, await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.BankAccountId}"));
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT COUNT(*) FROM finance.treasury_transactions WHERE source_doc_type='finance.expense' AND source_doc_id={expenseId}"));

        // (هـ) لا ترحيل مكرر
        await ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/finance/expenses/{expenseId}/post", new { }),
            HttpStatusCode.Conflict, "EXPENSE_ALREADY_POSTED");

        // (و) الطباعة التلقائية لسند المصروف (§29.10 AUTO_AFTER_POST على طابعة المالية) — محطة المالك فيها طابعة مالية
        Assert.True(posted.GetProperty("printJobCreated").GetBoolean());
        Assert.Equal("QUEUED", posted.GetProperty("printJobStatus").GetString());
        Assert.Equal("ROLL_80", posted.GetProperty("printMediaClass").GetString());

        // (ز) الإلغاء المرحَّل بعكس موثق: سبب إلزامي ثم أثر صافٍ صفر (لا حذف تاريخي)
        await ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/finance/expenses/{expenseId}/cancel",
            new { reasonAr = "ع" }), HttpStatusCode.Conflict, "EXPENSE_CANCEL_REASON_REQUIRED");
        var cancelled = await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/expenses/{expenseId}/cancel",
            new { reasonAr = "فاتورة مكررة — عكس موثق" }));
        Assert.Equal("CANCELLED", cancelled.GetProperty("status").GetString());
        Assert.Equal(0m, await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE source_doc_id={expenseId} AND source_doc_type IN ('finance.expense','finance.expense.reversal')"));
    }

    // ═══ 6) مصروف الوردية من درج المحطة: الترحيل المباشر الموثق (القاعدة G8 · §32) ══

    [Fact]
    public async Task T06_ShiftDrawerExpense_PostedDirectly_ButSafeNeedsApproval()
    {
        var shiftId = await OpenFreshShiftAsync(75m);
        using var cashier = await ClientAsync("g8.cashier", "Cashier#G8-Pass");

        // (أ) الكاشير يسجل مصروف وردية من درج محطته: 12.50 نقل — يُرحَّل مباشرة (المال خرج فعلاً — §14.1/§32)
        var expense = await OkAsync(await cashier.PostAsJsonAsync("/api/v1/finance/expenses", new
        {
            categoryId = _fx.TransportCategoryId,
            amount = 12.50m,
            taxAmount = 0m,
            paymentMethod = "CASH",
            treasuryAccountId = _fx.DrawerAccountId,
            shiftId,
            notesAr = "نقل بضاعة سيناريو G8",
        }));
        Assert.Equal("POSTED", expense.GetProperty("status").GetString());
        Assert.Equal(12.50m, expense.GetProperty("cashOutflow").GetDecimal());

        // الحركة تحمل وردية المحطة — المتوقع انخفض 12.50 (مرجع مستقل: 75 − 12.50 = 62.50)
        var reconciliation = await OkAsync(await cashier.GetAsync($"/api/v1/pos/shifts/{shiftId}/reconciliation"));
        Assert.Equal(62.50m, reconciliation.GetProperty("expectedInDrawer").GetDecimal());

        // (ب) الكاشير لا يرحّل مصروفاً على غير درج محطته (الخزينة): اعتمادٌ ثم ترحيل — §14.4)
        var safeExpense = await OkAsync(await cashier.PostAsJsonAsync("/api/v1/finance/expenses", new
        {
            categoryId = _fx.TransportCategoryId, amount = 30m, taxAmount = 0m,
            paymentMethod = "CASH", treasuryAccountId = _fx.SafeAccountId,
            notesAr = "محاولة صرف من الخزينة ككاشير",
        }));
        await ProblemAsync(await cashier.PostAsJsonAsync($"/api/v1/finance/expenses/{safeExpense.GetProperty("expenseId").GetInt64()}/post", new { }),
            HttpStatusCode.Conflict, "EXPENSE_APPROVAL_REQUIRED");

        // إغلاق مطابق
        await OkAsync(await cashier.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close", new { countedCash = 62.50m }));
    }

    // ═══ 7) رأس المال: أحداث موثقة والافتتاحي باعتماد فقط (§14.3/D-14/B-04) ══════

    [Fact]
    public async Task T07_CapitalEvents_ContributionWithdrawalOpeningBalance()
    {
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");
        using var manager = await ClientAsync("g8.manager", "Manager#G8-Pass", withStation: false);

        var safeBefore = await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.SafeAccountId}");

        // (أ) مساهمة مالك 10000 على الخزينة → ترحيل بأثر قبض واحد
        var contribution = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/capital/events", new
        {
            eventType = "OWNER_CONTRIBUTION", amount = 10000m, treasuryAccountId = _fx.SafeAccountId,
            descriptionAr = "مساهمة رأس مال إضافية سيناريو G8",
        }));
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/capital/events/{contribution.GetProperty("capitalId").GetInt64()}/post", new { }));
        Assert.Equal(safeBefore + 10000m, await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.SafeAccountId}"));

        // (ب) سحب مالك 1500 → أثر صرف واحد
        var withdrawal = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/capital/events", new
        {
            eventType = "OWNER_WITHDRAWAL", amount = 1500m, treasuryAccountId = _fx.SafeAccountId,
            descriptionAr = "سحب مالك موثق سيناريو G8",
        }));
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/capital/events/{withdrawal.GetProperty("capitalId").GetInt64()}/post", new { }));
        Assert.Equal(safeBefore + 8500m, await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.SafeAccountId}"));

        // (ج) الرصيد الافتتاحي بلا اعتماد → رفض (D-14/B-04: الأرصدة بمستندات معتمدة فقط)
        await ProblemAsync(await owner.PostAsJsonAsync("/api/v1/finance/capital/events", new
        {
            eventType = "OPENING_BALANCE", amount = 50000m, treasuryAccountId = _fx.SafeAccountId,
            descriptionAr = "افتتاحي بلا اعتماد",
        }), HttpStatusCode.Conflict, "CAPITAL_OPENING_APPROVAL_REQUIRED");

        // (د) الافتتاحي باعتماد ثنائي موثق (مدير يطلب، مالك يعتمد — no_self) ثم يُرحَّل
        var request = await OkAsync(await manager.PostAsJsonAsync("/api/v1/approvals", new
        {
            entityType = "finance.capital_event", entityId = 0, reasonAr = "طلب اعتماد الرصيد الافتتاحي سيناريو G8",
        }));
        var decision = await OkAsync(await owner.PostAsJsonAsync($"/api/v1/approvals/{request.GetProperty("approvalId").GetInt64()}/decision",
            new { decision = "APPROVED", reasonAr = "اعتماد الأرصدة الافتتاحية بعد المراجعة" }));
        Assert.Equal("APPROVED", decision.GetProperty("decision").GetString());

        var opening = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/capital/events", new
        {
            eventType = "OPENING_BALANCE", amount = 50000m, treasuryAccountId = _fx.BankAccountId,
            descriptionAr = CapitalPendingNoteForTest(), approvalId = request.GetProperty("approvalId").GetInt64(),
        }));
        await OkAsync(await owner.PostAsJsonAsync($"/api/v1/finance/capital/events/{opening.GetProperty("capitalId").GetInt64()}/post", new { }));
        Assert.Equal(50000m, await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.BankAccountId} AND source_doc_type='finance.capital_event' AND source_doc_id={opening.GetProperty("capitalId").GetInt64()}"));

        // (هـ) حدث رأس المال المرحّل نهائي: لا تعديل ولا حذف ولا ترحيل مكرر
        await ProblemAsync(await owner.PostAsJsonAsync($"/api/v1/finance/capital/events/{opening.GetProperty("capitalId").GetInt64()}/post", new { }),
            HttpStatusCode.Conflict, "CAPITAL_NOT_DRAFT");

        // (و) رأس المال لا يُساوى بالمبيعات أو الصندوق: قائمة الأحداث أنواعها موثقة منفصلة (§14.3)
        var events = await OkAsync(await owner.GetAsync("/api/v1/finance/capital/events?status=POSTED"));
        var postedEvents = events.GetProperty("items").EnumerateArray();
        Assert.Contains(postedEvents, e => e.GetProperty("eventType").GetString() == "OWNER_CONTRIBUTION");
        Assert.Contains(events.GetProperty("items").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "OWNER_WITHDRAWAL");
        Assert.Contains(events.GetProperty("items").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "OPENING_BALANCE");
    }

    private static string CapitalPendingNoteForTest() => "رصيد افتتاحي معتمد باختبار G8 — الأرقام الفعلية بانتظار تسليم المالك (D-14/B-04)";

    // ═══ 8) الخزائن: أرصدة الدفتر المنفصلة (درج ≠ خزينة ≠ بنك — §14.2) ═══════════

    [Fact]
    public async Task T08_TreasuryAccounts_SeparateLedgerBalances()
    {
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");

        var accounts = await OkAsync(await owner.GetAsync("/api/v1/finance/treasury/accounts"));
        var byCode = accounts.EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a);
        Assert.Contains("G8DRAWER-ST", byCode.Keys);
        Assert.Contains("MAIN_SAFE", byCode.Keys);
        Assert.Contains("MAIN_BANK", byCode.Keys);

        // الرصيد من الدفتر حرفياً: Σ(الحركات × اتجاهها) لكل حساب منفصلاً (تعريف ق12)
        var drawerLedger = await ScalarDecimalAsync($"SELECT COALESCE(SUM(amount*direction),0) FROM finance.treasury_transactions WHERE treasury_account_id={_fx.DrawerAccountId}");
        Assert.Equal(drawerLedger, byCode["G8DRAWER-ST"].GetProperty("currentBalance").GetDecimal());

        // حركات الخزينة مرقمة صفحة بصفحة وكل حركة لها مستند ومصدر وسبب (§14.2 ق4)
        var txns = await OkAsync(await owner.GetAsync($"/api/v1/finance/treasury/transactions?treasuryAccountId={_fx.DrawerAccountId}&page=1&pageSize=50"));
        Assert.True(txns.GetProperty("totalCount").GetInt32() > 0);
        foreach (var txn in txns.GetProperty("items").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(txn.GetProperty("sourceDocType").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(txn.GetProperty("reason").GetString()));
        }
    }

    // ═══ 9) خصوصية الرواتب: لا تطبع تفاصيل الرواتب على طابعة عامة (§31.1/§35.2 — اختبار 21) ══

    [Fact]
    public async Task T09_PayrollPrint_NeverOnPublicPrinter_ConfidentialOnly()
    {
        // (أ) محطة ثانية فيها طابعة مالية عامة فقط (بلا مؤمنة): كشف راتب يُرفض — لا مهمة إطلاقاً
        using var ownerOnPublicStation = await ClientAsync("g8.owner", "Owner#G8-Pass", stationId: _fx.PublicStationId);
        var denied = await ProblemAsync(await ownerOnPublicStation.PostAsJsonAsync("/api/v1/finance/print", new
        {
            documentType = "PAYROLL_SLIP", documentId = 1,
        }), HttpStatusCode.Conflict, "PAYROLL_PRINTER_CONFIDENTIAL_REQUIRED");
        Assert.Contains("طابعة مؤمنة", denied.GetProperty("detail").GetString());
        Assert.Equal(0L, await ScalarAsync("""
            SELECT COUNT(*) FROM print.print_jobs
             WHERE document_type IN ('PAYROLL_SLIP','PAYROLL_PAYMENT_VOUCHER')
               AND printer_profile_id IN (SELECT printer_profile_id FROM print.printer_profiles WHERE printer_id='G8-PRN-FIN2')
            """));

        // سند سداد راتب كذلك (§29.10: طابعة مؤمنة فقط)
        await ProblemAsync(await ownerOnPublicStation.PostAsJsonAsync("/api/v1/finance/print", new
        {
            documentType = "PAYROLL_PAYMENT_VOUCHER", documentId = 1,
        }), HttpStatusCode.Conflict, "PAYROLL_PRINTER_CONFIDENTIAL_REQUIRED");

        // (ب) المحطة الرئيسية فيها الطابعة المؤمنة (58mm): كشف الراتب يُوجَّه إليها حصراً
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");
        var slip = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/print", new
        {
            documentType = "PAYROLL_SLIP", documentId = 1,
        }));
        Assert.True(slip.GetProperty("jobCreated").GetBoolean());
        Assert.Equal("CONFIDENTIAL_PAYROLL", slip.GetProperty("targetPrinterRole").GetString());
        Assert.Equal("ROLL_58", slip.GetProperty("mediaClass").GetString());
        // المهمة على الطابعة المؤمنة تحديداً — لا على طابعة المالية ولا إيصال العميل
        var jobId = slip.GetProperty("jobId").GetInt64();
        Assert.Equal(_fx.ConfidentialPrinterId, await ScalarAsync($"SELECT printer_profile_id FROM print.print_jobs WHERE job_id={jobId}"));

        // (ج) السند المالي على طابعة المالية (EXPLICIT_USER §29.10) — قالب 80 المستقل
        var voucherPrint = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/print", new
        {
            documentType = "FINANCE_RECEIPT_VOUCHER", documentId = 1,
        }));
        Assert.True(voucherPrint.GetProperty("jobCreated").GetBoolean());
        Assert.Equal("FINANCE_VOUCHERS", voucherPrint.GetProperty("targetPrinterRole").GetString());
        Assert.Equal("ROLL_80", voucherPrint.GetProperty("mediaClass").GetString());

        // (د) إعادة الطباعة مهمة جديدة REPRINT مرتبطة بالأصل (§29.9) — لا نسخة مالية جديدة
        var reprint = await OkAsync(await owner.PostAsJsonAsync("/api/v1/finance/print", new
        {
            documentType = "FINANCE_RECEIPT_VOUCHER", documentId = 1, reprintOfJobId = voucherPrint.GetProperty("jobId").GetInt64(),
        }));
        var reprintJobId = reprint.GetProperty("jobId").GetInt64();
        var originalJobId = voucherPrint.GetProperty("jobId").GetInt64();
        Assert.Equal("REPRINT", await ScalarStringAsync($"SELECT copy_type FROM print.print_jobs WHERE job_id={reprintJobId}"));
        Assert.Equal(originalJobId, await ScalarAsync($"SELECT related_reprint_of FROM print.print_jobs WHERE job_id={reprintJobId}"));
    }

    // ═══ 10) أحداث الدرج والعدّات الموثقة (§9.9/§14.1 — HRD-006/SFT-002) ══════════

    [Fact]
    public async Task T10_DrawerEventsAndCashCounts_Documented()
    {
        var shiftId = await OpenFreshShiftAsync(30m);
        using var cashier = await ClientAsync("g8.cashier", "Cashier#G8-Pass");
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");

        // فتح درج بغير بيع بسبب مسجل (G7) ثم قراءة الأحداث من واجهة G8
        await OkAsync(await cashier.PostAsJsonAsync("/api/v1/pos/drawer/open", new { reasonAr = "تسليم فكة لعميل — سيناريو G8" }));
        await OkAsync(await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 2m) },
            payments = new[] { CashPayment(10.00m) },
            idempotencyKey = $"g8-t10-cash-{Guid.NewGuid():N}",
        }));
        var events = await OkAsync(await owner.GetAsync($"/api/v1/pos/shifts/{shiftId}/drawer-events"));
        Assert.True(events.GetArrayLength() >= 2); // MANUAL_OPEN + OPENED_BY_SALE — بأزمنة صادقة
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("eventType").GetString() == "MANUAL_OPEN");
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("eventType").GetString() == "OPENED_BY_SALE");

        // الإغلاق يوثق عدة العد (من عدّ ومتى وبكم — SFT-002)
        var close = await OkAsync(await owner.PostAsJsonAsync($"/api/v1/pos/shifts/{shiftId}/close",
            new { countedCash = 40.00m, notesAr = "عدة نهاية الوردية" }));
        Assert.Equal(40.00m, close.GetProperty("finalCountedCash").GetDecimal());

        var counts = await OkAsync(await owner.GetAsync($"/api/v1/pos/shifts/{shiftId}/cash-counts"));
        Assert.Equal(1, counts.GetArrayLength());
        Assert.Equal(40.00m, counts[0].GetProperty("countedCash").GetDecimal());
        Assert.Equal("المالك G8", counts[0].GetProperty("countedByNameAr").GetString());
    }

    // ═══ 11) RBAC (§18): أقل صلاحية تفصل بين الأدوار — ذرية خادمية ═══════════════

    [Fact]
    public async Task T11_Rbac_FinanceRolesAtomic()
    {
        // الكاشير لا ينشئ سندات (403)
        using var cashier = await ClientAsync("g8.cashier", "Cashier#G8-Pass");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "RECEIPT", treasuryAccountId = _fx.SafeAccountId, amount = 5m,
            reasonAr = "محاولة كاشير لإنشاء سند",
        })).StatusCode);

        // أمين المخزن لا يرى الخزائن (403)
        using var storekeeper = await ClientAsync("g8.storekeeper", "Storekeeper#G8-Pass");
        Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.GetAsync("/api/v1/finance/treasury/accounts")).StatusCode);

        // المراجع لا يملك finance.treasury.view في مصفوفة V009 (المحاسب والمالك فقط) — تقاريره المالية بواتها G9
        using var auditor = await ClientAsync("g8.auditor", "Auditor#G8-Pass", withStation: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/v1/finance/treasury/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/finance/vouchers", new
        {
            voucherType = "RECEIPT", treasuryAccountId = _fx.SafeAccountId, amount = 5m,
            reasonAr = "محاولة مراجع لإنشاء سند",
        })).StatusCode);

        // المحاسب يرى الخزائن وينشئ السندات ولا يدير حساباتها (403)
        using var accountant = await ClientAsync("g8.accountant", "Accountant#G8-Pass", withStation: false);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/v1/finance/treasury/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/v1/finance/treasury/accounts", new
        {
            code = "HACK", nameAr = "حساب غير مصرح", accountKind = "SAFE",
        })).StatusCode);

        // الكاشير لا يسجل أحداث رأس المال (403)
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/finance/capital/events", new
        {
            eventType = "OWNER_CONTRIBUTION", amount = 5m, descriptionAr = "محاولة كاشير",
        })).StatusCode);

        // المجهول: 401
        using var anonymous = _fx.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/finance/treasury/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/pos/shifts?status=OPEN")).StatusCode);
    }

    // ═══ 12) عقود الاستجابة: مطابقة قابلة للتفاوض (§21 عقد V1) ═════════════════

    [Fact]
    public async Task T12_Contracts_FinanceApiShape()
    {
        using var owner = await ClientAsync("g8.owner", "Owner#G8-Pass");

        // قالب سند القبض موجودان (80/58) بالبنية المعتمدة من G2 المستقلة (بوابة القبول G8)
        Assert.Equal(2L, await ScalarAsync(
            "SELECT COUNT(*) FROM print.document_templates WHERE document_type='FINANCE_RECEIPT_VOUCHER' AND media_class IN ('ROLL_80','ROLL_58') AND is_active"));
        Assert.Equal(2L, await ScalarAsync(
            "SELECT COUNT(*) FROM print.document_templates WHERE document_type='EXPENSE_VOUCHER' AND media_class IN ('ROLL_80','ROLL_58') AND is_active"));
        Assert.Equal(2L, await ScalarAsync(
            "SELECT COUNT(*) FROM print.document_templates WHERE document_type='PAYROLL_SLIP' AND media_class IN ('ROLL_80','ROLL_58') AND is_active"));

        // فئات المصروفات المرجعية الست قائمة (قابلة للتهيئة — §14.4)
        var categories = await OkAsync(await owner.GetAsync("/api/v1/finance/expense-categories"));
        Assert.True(categories.GetArrayLength() >= 6);

        // A4 لا يظهر في أي قرار طباعة G8 (§29.11 — EXPLICIT فقط عبر بوابة الطباعة التفصيلية)
        Assert.Equal(0L, await ScalarAsync(
            "SELECT COUNT(*) FROM print.auto_print_rules WHERE document_type IN ('FINANCE_RECEIPT_VOUCHER','FINANCE_DISBURSEMENT_VOUCHER','FINANCE_TRANSFER_VOUCHER','SHIFT_CLOSE_SUMMARY') AND policy NOT IN ('EXPLICIT_USER')"));
    }
}

// ═══════════════ بذرة G8 ═════════════════════════════════════════════════════

public sealed class G8ApiFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seededOnce;

    public WeraApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public long BranchId { get; private set; }
    public long StationId { get; private set; }
    public long PublicStationId { get; private set; }
    public long WarehouseId { get; private set; }
    public long BagSkuId { get; private set; }
    public long DrawerAccountId { get; private set; }
    public long SafeAccountId { get; private set; }
    public long BankAccountId { get; private set; }
    public long CleaningCategoryId { get; private set; }
    public long UtilitiesCategoryId { get; private set; }
    public long TransportCategoryId { get; private set; }
    public long CreditCustomerId { get; private set; }
    public long ConfidentialPrinterId { get; private set; }

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient(); // يقلع ويرحّل حتى رأس V019
        await SeedOnceAsync();
    }

    private async Task SeedOnceAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seededOnce)
            {
                await RefreshIdsAsync();
                return;
            }
            await SeedCoreAsync();
            _seededOnce = true;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    /// <summary>معرفات البذرة ثابتة على قاعدة الاختبار المشتركة — تُقرأ مرة أخرى لكل تشغيل.</summary>
    private async Task RefreshIdsAsync()
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        BranchId = Scalar("SELECT branch_id FROM core.branches WHERE code='G8B1'");
        StationId = Scalar("SELECT station_id FROM core.stations WHERE code='G8ST1'");
        PublicStationId = Scalar("SELECT station_id FROM core.stations WHERE code='G8ST2'");
        WarehouseId = Scalar("SELECT warehouse_id FROM inv.warehouses WHERE code='G8W1'");
        BagSkuId = Scalar("SELECT sku_id FROM catalog.skus WHERE sku_code='G8-BAG'");
        DrawerAccountId = Scalar("SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='G8DRAWER-ST'");
        SafeAccountId = Scalar("SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='MAIN_SAFE'");
        BankAccountId = Scalar("SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='MAIN_BANK'");
        CleaningCategoryId = Scalar("SELECT category_id FROM finance.expense_categories WHERE code='CLEANING'");
        UtilitiesCategoryId = Scalar("SELECT category_id FROM finance.expense_categories WHERE code='UTILITIES'");
        TransportCategoryId = Scalar("SELECT category_id FROM finance.expense_categories WHERE code='TRANSPORT'");
        CreditCustomerId = Scalar("SELECT customer_id FROM sales.customers WHERE code='G8CRED'");
        ConfidentialPrinterId = Scalar("SELECT printer_profile_id FROM print.printer_profiles WHERE printer_id='G8-PRN-CONF'");
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task<Contracts.V1.LoginResponse> LoginAsync(string username, string password, long? branchId = null, long? stationId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password, branchId, stationId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contracts.V1.LoginResponse>())!;
    }

    private async Task SeedCoreAsync()
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        void UpsertUser(string username, string display, string password, string roleCode)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
            Scalar($"INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('{username}', '{display}', '{hash}', true) ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash");
            var userId = Scalar($"SELECT user_id FROM core.users WHERE username = '{username}'");
            Scalar($"DELETE FROM core.user_roles WHERE user_id = {userId}");
            Scalar($"INSERT INTO core.user_roles (user_id, role_id, branch_id) VALUES ({userId}, (SELECT role_id FROM core.roles WHERE code='{roleCode}'), NULL)");
        }

        UpsertUser("g8.owner", "المالك G8", "Owner#G8-Pass", "OWNER");
        UpsertUser("g8.manager", "مدير G8", "Manager#G8-Pass", "MANAGER");
        UpsertUser("g8.supervisor", "مشرف G8", "Supervisor#G8-Pass", "SUPERVISOR");
        UpsertUser("g8.cashier", "كاشير G8", "Cashier#G8-Pass", "CASHIER");
        UpsertUser("g8.accountant", "محاسب G8", "Accountant#G8-Pass", "ACCOUNTANT");
        UpsertUser("g8.storekeeper", "أمين مخزن G8", "Storekeeper#G8-Pass", "STOREKEEPER");
        UpsertUser("g8.auditor", "مراجع G8", "Auditor#G8-Pass", "AUDITOR");

        BranchId = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G8B1', 'فرع G8 الرئيسي', 'Africa/Cairo') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING branch_id");
        StationId = Scalar($"INSERT INTO core.stations (branch_id, code, name_ar) VALUES ({BranchId}, 'G8ST1', 'محطة كاشير G8') ON CONFLICT (branch_id, code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING station_id");
        PublicStationId = Scalar($"INSERT INTO core.stations (branch_id, code, name_ar) VALUES ({BranchId}, 'G8ST2', 'محطة خلفية G8 (بلا طابعة مؤمنة)') ON CONFLICT (branch_id, code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING station_id");
        WarehouseId = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({BranchId}, 'G8W1', 'مخزن البيع G8', 'MAIN') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");

        // الخزائن: درج المحطة (الحقيقة الفيزيائية G8) + الخزينة/البنك المرجعيان من بذر V019
        Scalar($@"INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, station_id)
                   VALUES ('G8DRAWER-ST', 'درج محطة G8 الرئيسية', 'CASH_DRAWER', {BranchId}, {StationId})
                   ON CONFLICT DO NOTHING");
        SafeAccountId = Scalar("SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='MAIN_SAFE'");
        BankAccountId = Scalar("SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='MAIN_BANK'");

        CleaningCategoryId = Scalar("SELECT category_id FROM finance.expense_categories WHERE code='CLEANING'");
        UtilitiesCategoryId = Scalar("SELECT category_id FROM finance.expense_categories WHERE code='UTILITIES'");
        TransportCategoryId = Scalar("SELECT category_id FROM finance.expense_categories WHERE code='TRANSPORT'");

        CreditCustomerId = Scalar("INSERT INTO sales.customers (code, name_ar, credit_enabled, credit_limit) VALUES ('G8CRED', 'عميل آجل G8', true, 1000000) ON CONFLICT (code) DO UPDATE SET credit_enabled = EXCLUDED.credit_enabled, credit_limit = EXCLUDED.credit_limit RETURNING customer_id");

        // طابعات G8: مالية 80mm (سندات ومصروفات وإقفال) + مؤمنة 58mm (رواتب فقط) على المحطة الرئيسية،
        // ومحطة خلفية بطابعة مالية فقط — لإثبات أن الرواتب لا تسقط على طابعة عامة أبداً
        var financePrinter = Scalar($@"INSERT INTO print.printer_profiles
                    (printer_id, windows_queue_name, device_model, driver_name, station_id, media_class, media_width_mm, printable_width_mm, dpi_x, dpi_y, adapter_kind, is_calibrated)
                  VALUES ('G8-PRN-FIN', 'G8FinanceQueue', 'Bixolon SRP-330', 'ESC_POS', {StationId}, 'ROLL_80', 80, 72, 180, 180, 'ESC_POS', true)
                  ON CONFLICT (printer_id) DO UPDATE SET station_id = EXCLUDED.station_id RETURNING printer_profile_id");
        ConfidentialPrinterId = Scalar($@"INSERT INTO print.printer_profiles
                    (printer_id, windows_queue_name, device_model, driver_name, station_id, media_class, media_width_mm, printable_width_mm, dpi_x, dpi_y, adapter_kind, is_calibrated)
                  VALUES ('G8-PRN-CONF', 'G8ConfidentialQueue', 'Epson TM-T20', 'ESC_POS', {StationId}, 'ROLL_58', 58, 48, 203, 203, 'ESC_POS', true)
                  ON CONFLICT (printer_id) DO UPDATE SET station_id = EXCLUDED.station_id RETURNING printer_profile_id");
        var backOfficePrinter = Scalar($@"INSERT INTO print.printer_profiles
                    (printer_id, windows_queue_name, device_model, driver_name, station_id, media_class, media_width_mm, printable_width_mm, dpi_x, dpi_y, adapter_kind, is_calibrated)
                  VALUES ('G8-PRN-FIN2', 'G8BackQueue', 'Bixolon SRP-330', 'ESC_POS', {PublicStationId}, 'ROLL_80', 80, 72, 180, 180, 'ESC_POS', true)
                  ON CONFLICT (printer_id) DO UPDATE SET station_id = EXCLUDED.station_id RETURNING printer_profile_id");
        Scalar($@"INSERT INTO print.printer_document_roles (printer_profile_id, station_id, document_role, is_auto_routing_enabled)
                  VALUES ({financePrinter}, {StationId}, 'FINANCE_VOUCHERS', true)
                  ON CONFLICT (station_id, document_role) DO UPDATE SET printer_profile_id = EXCLUDED.printer_profile_id, is_auto_routing_enabled = true");
        Scalar($@"INSERT INTO print.printer_document_roles (printer_profile_id, station_id, document_role, is_auto_routing_enabled)
                  VALUES ({ConfidentialPrinterId}, {StationId}, 'CONFIDENTIAL_PAYROLL', true)
                  ON CONFLICT (station_id, document_role) DO UPDATE SET printer_profile_id = EXCLUDED.printer_profile_id, is_auto_routing_enabled = true");
        Scalar($@"INSERT INTO print.printer_document_roles (printer_profile_id, station_id, document_role, is_auto_routing_enabled)
                  VALUES ({backOfficePrinter}, {PublicStationId}, 'FINANCE_VOUCHERS', true)
                  ON CONFLICT (station_id, document_role) DO UPDATE SET printer_profile_id = EXCLUDED.printer_profile_id, is_auto_routing_enabled = true");

        // الإعداد: البطاقة عبر المحاكاة الموسومة (B-02 الصادق نفسه من G7)
        Scalar("UPDATE core.business_settings SET value_text='SANDBOX_SIMULATOR' WHERE setting_key='payments.card_provider'");

        // منتج الأكياس (سعر 5.00 شامل — نفس بنية G7) إن لم يكن مزروعاً
        var seeded = Scalar("SELECT COUNT(*) FROM catalog.skus WHERE sku_code='G8-BAG'") > 0;
        if (!seeded)
        {
            var login = await LoginAsync("g8.owner", "Owner#G8-Pass", BranchId, StationId);
            using var authed = CreateClient();
            authed.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

            var categoryId = Scalar("INSERT INTO catalog.categories (code, name_ar) VALUES ('G8CAT', 'فئة G8') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING category_id");
            var priceListId = Scalar("SELECT price_list_id FROM catalog.price_lists WHERE code='DEFAULT_EGP'");
            var payload = new
            {
                code = "G8-SCN",
                nameAr = "منتج سيناريو G8",
                nameEn = "G8 Scenario Product",
                categoryId,
                description = "منتج سيناريوهات G8",
                trackLots = false,
                trackSerials = false,
                skus = new object[]
                {
                    new { skuCode = "G8-BAG", saleMode = "COUNT", baseUomId = 1, saleUomId = 1, nameAr = "كيس", reorderLevel = 10, weight = (object?)null },
                },
                barcodes = new object[]
                {
                    new { barcodeValue = "6221031500084", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 },
                },
                plus = Array.Empty<object>(),
                prices = new object[]
                {
                    new { skuOrdinal = 0, priceListId, unitId = 1, unitPrice = 5.00m, taxTreatment = "TAXABLE" },
                },
                packages = Array.Empty<object>(),
            };
            var created = await authed.PostAsJsonAsync("/api/v1/catalog/products", payload);
            Assert.True(created.IsSuccessStatusCode, "بذر منتج G8 فشل: " + await created.Content.ReadAsStringAsync());
        }
        await RefreshIdsAsync();

        // بذر المخزون مرة واحدة (نمط G7: حركة OPENING موثقة + طبقة WAC — لا أرقام حرة بلا دفتر)
        var ownerIdForStock = Scalar("SELECT user_id FROM core.users WHERE username='g8.owner'");
        var stockSeeded = Scalar($"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='g8.seed' AND warehouse_id={WarehouseId}") > 0;
        if (!stockSeeded)
        {
            Scalar($@"INSERT INTO finance.cost_layers (sku_id, warehouse_id, costing_method, avg_unit_cost, qty_on_layer)
                      VALUES ({BagSkuId}, {WarehouseId}, 'WAC', 2.0000, 10000)
                      ON CONFLICT (sku_id, warehouse_id) DO UPDATE SET avg_unit_cost = 2.0000, qty_on_layer = 10000");
            Scalar($@"INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor, source_doc_type, source_doc_id, reason, branch_id, user_id)
                      VALUES ({BagSkuId}, {WarehouseId}, 'OPENING', 1, 10000, 1, 1, 'g8.seed', 1, 'بذر مخزون G8', {BranchId}, {ownerIdForStock})");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
