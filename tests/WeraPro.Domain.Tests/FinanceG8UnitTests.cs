namespace WeraPro.Domain.Tests;

using WeraPro.Domain.Finance;
using Xunit;

/// <summary>
/// اختبارات مجال G8 (§14/§13) بمراجع رقمية مستقلة — تُحسب النتائج المتوقعة يدوياً
/// لا باستدعاء الدالة نفسها (§16 ق10).
/// </summary>
public sealed class FinanceG8UnitTests
{
    // ═══ 1) مطابقة الوردية (§14.1/بوابة القبول) ═══════════════════════════

    [Fact]
    public void T01_ExpectedDrawer_FromLedgerComponents_IndependentNumbers()
    {
        // افتتاحي 200 · مبيعات نقدية 465.50 · ردود نقدية 50 · مصروف درج 30 ·
        // إيداع سند 100 · سحب إيداع خزينة 500 · تحويل وارد 0 · صادر 0
        var components = new ShiftReconciliation.ExpectedComponents(
            OpeningFloat: 200m, CashSales: 465.50m, CashRefunds: 50m, DrawerExpenses: 30m,
            VoucherDeposits: 100m, VoucherWithdrawals: 500m, TransfersIn: 0m, TransfersOut: 0m);
        // مرجع مستقل: 200 + 465.50 − 50 − 30 + 100 − 500 = 185.50
        Assert.Equal(185.50m, components.ExpectedInDrawer);
    }

    [Fact]
    public void T02_Variance_SignAndReasonRules()
    {
        // الفارق = الفعلي − المتوقع (§14.1)
        Assert.Equal(-4.75m, ShiftReconciliation.Variance(180.25m, 185.00m));
        Assert.Equal(15.00m, ShiftReconciliation.Variance(215.00m, 200.00m));
        Assert.Equal(0m, ShiftReconciliation.Variance(200m, 200m));

        // الفارق ≠ 0 يستلزم سبباً ≥ 3 محارف — والصفر لا يستلزم
        Assert.True(ShiftReconciliation.RequiresReason(-0.01m));
        Assert.False(ShiftReconciliation.RequiresReason(0m));
        Assert.False(ShiftReconciliation.ReasonIsValid("  "));
        Assert.False(ShiftReconciliation.ReasonIsValid("ع"));
        Assert.True(ShiftReconciliation.ReasonIsValid("فرق عدّ عُرض على المشرف"));
    }

    [Fact]
    public void T03_CloseAuthorization_ByRole()
    {
        // متطابق: كل حائز إغلاق وردية — حتى الكاشير
        Assert.True(ShiftReconciliation.MayClose(hasShiftClose: true, hasVarianceApproval: false, variance: 0m));
        // بفارق: يتطلب اعتماد فوارق (مشرف فأعلى) — الكاشير لا يعتمد فارقه بنفسه
        Assert.False(ShiftReconciliation.MayClose(hasShiftClose: true, hasVarianceApproval: false, variance: -5m));
        Assert.True(ShiftReconciliation.MayClose(hasShiftClose: true, hasVarianceApproval: true, variance: -5m));
        // بلا صلاحية إغلاق أصلاً: ممنوع
        Assert.False(ShiftReconciliation.MayClose(hasShiftClose: false, hasVarianceApproval: true, variance: 0m));
    }

    [Fact]
    public void T04_CardNeverInDrawer_TenderSeparation()
    {
        // فصل الوسائل (§14.1 ق4): البطاقة والآجل لا يدخلان متوقع الدرج
        var components = new ShiftReconciliation.ExpectedComponents(
            100m, CashSales: 250m, CashRefunds: 0m, DrawerExpenses: 0m,
            VoucherDeposits: 0m, VoucherWithdrawals: 0m, TransfersIn: 0m, TransfersOut: 0m);
        // بطاقة 700 وآجل 300 لا يغيّران المتوقع النقدي إطلاقاً
        Assert.Equal(350m, components.ExpectedInDrawer);
    }

    // ═══ 2) السندات (§14.2/§13) ═════════════════════════════════════════

    [Fact]
    public void T05_VoucherTransfer_TwoOppositeLegs_NoIncomeNoExpense()
    {
        // التحويل: حركتان متقابلتان بالمبلغ نفسه — لا إيراد ولا مصروف (§14.2 ق2)
        var legs = VoucherRules.PostingLegs(VoucherRules.Transfer, 500m, treasuryAccountId: 1, counterTreasuryAccountId: 7);
        Assert.Equal(2, legs.Count);
        Assert.Equal((1L, -1, 500m), (legs[0].TreasuryAccountId, legs[0].Direction, legs[0].Amount));
        Assert.Equal((7L, +1, 500m), (legs[1].TreasuryAccountId, legs[1].Direction, legs[1].Amount));
        // صافي أثر التحويل على مجموع الخزائن = صفر (لا يولد قيمة)
        Assert.Equal(0m, legs.Sum(l => l.Amount * l.Direction));
    }

    [Fact]
    public void T06_VoucherReceiptAndDisbursement_SingleLeg()
    {
        var receipt = VoucherRules.PostingLegs(VoucherRules.Receipt, 250m, 3, null);
        Assert.Single(receipt);
        Assert.Equal(+1, receipt[0].Direction);

        var disbursement = VoucherRules.PostingLegs(VoucherRules.Disbursement, 90m, 3, null);
        Assert.Single(disbursement);
        Assert.Equal(-1, disbursement[0].Direction);
    }

    [Fact]
    public void T07_VoucherCreateValidation_Rules()
    {
        // تحويل لنفس الخزينة → رفض
        Assert.NotNull(VoucherRules.ValidateCreate("TRANSFER", 10m, 1, 1, null, null, "تحويل لنفس الحساب"));
        // تحويل بلا وجهة → رفض
        Assert.NotNull(VoucherRules.ValidateCreate("TRANSFER", 10m, 1, null, null, null, "بلا وجهة"));
        // تحويل بطرف تجاري → رفض (قيد V006)
        Assert.NotNull(VoucherRules.ValidateCreate("TRANSFER", 10m, 1, 2, "CUSTOMER", 5, "تحويل بطرف"));
        // سند سليم
        Assert.Null(VoucherRules.ValidateCreate("RECEIPT", 10m, 1, null, "CUSTOMER", 5, "تحصيل دفعة عميل"));
        // سبب قصير → رفض
        Assert.NotNull(VoucherRules.ValidateCreate("RECEIPT", 10m, 1, null, "CUSTOMER", 5, "ل"));
        // مبلغ غير موجب → رفض
        Assert.NotNull(VoucherRules.ValidateCreate("RECEIPT", 0m, 1, null, "CUSTOMER", 5, "مبلغ صفر"));
        // طرف بلا معرف → رفض
        Assert.NotNull(VoucherRules.ValidateCreate("DISBURSEMENT", 10m, 1, null, "SUPPLIER", null, "صرف مورد"));
    }

    [Fact]
    public void T08_AllocationRules_WithinRemainingOnly()
    {
        // التوزيع لسند قبض من عميل فقط (§13/PRT-003)
        Assert.True(VoucherRules.MayAllocate("RECEIPT", "CUSTOMER"));
        Assert.False(VoucherRules.MayAllocate("DISBURSEMENT", "CUSTOMER"));
        Assert.False(VoucherRules.MayAllocate("RECEIPT", "SUPPLIER"));

        // ≤ المتبقي يمر · فوقه يُرفض (منع تخصيص أكبر من المتبقي — §13 ق3)
        Assert.Null(VoucherRules.ValidateAllocation(100m, 100m));
        Assert.Null(VoucherRules.ValidateAllocation(99.99m, 100m));
        var error = VoucherRules.ValidateAllocation(100.01m, 100m);
        Assert.NotNull(error);
        Assert.Equal("ALLOCATION_EXCEEDS_REMAINING", error!.Code);
        Assert.NotNull(VoucherRules.ValidateAllocation(0m, 100m));
    }

    [Fact]
    public void T09_VoucherNumberPrefixes()
    {
        Assert.Equal("RV", VoucherRules.NumberPrefix("RECEIPT"));
        Assert.Equal("DV", VoucherRules.NumberPrefix("DISBURSEMENT"));
        Assert.Equal("TV", VoucherRules.NumberPrefix("TRANSFER"));
    }

    // ═══ 3) المصروفات (§14.4) ═══════════════════════════════════════════

    [Fact]
    public void T10_ExpenseCashOutflow_AmountPlusTax()
    {
        // المسدد = المبلغ + الضريبة (ضريبة المصروف منفصلة — تعريف ق8)
        Assert.Equal(114m, ExpenseRules.CashOutflow(100m, 14m));
        Assert.Equal(30m, ExpenseRules.CashOutflow(30m, 0m));
    }

    [Fact]
    public void T11_ShiftDrawerPaidOut_DirectPostRule()
    {
        // مصروف وردية من درج المحطة: خزينته هي درج الوردية ومرتبط بورديتها
        Assert.True(ExpenseRules.MayPostDirectly(shiftId: 11, treasuryAccountId: 4, drawerAccountId: 4));
        // من غير درج المحطة (خزينة/بنك) → لا ترحيل مباشر: اعتماد ثم ترحيل (§14.4)
        Assert.False(ExpenseRules.MayPostDirectly(shiftId: 11, treasuryAccountId: 7, drawerAccountId: 4));
        Assert.False(ExpenseRules.MayPostDirectly(shiftId: null, treasuryAccountId: 4, drawerAccountId: 4));
    }

    [Fact]
    public void T12_ExpenseReversalReason_Required()
    {
        Assert.False(ExpenseRules.ReversalReasonIsValid(null));
        Assert.False(ExpenseRules.ReversalReasonIsValid("ع"));
        Assert.True(ExpenseRules.ReversalReasonIsValid("خطأ في المبلغ — أعيد بالمستند الصحيح"));
    }

    // ═══ 4) رأس المال (§14.3/D-14) ═════════════════════════════════════

    [Fact]
    public void T13_CapitalDirections_ByEventType()
    {
        Assert.Equal(+1, CapitalRules.TreasuryDirection("OWNER_CONTRIBUTION"));
        Assert.Equal(+1, CapitalRules.TreasuryDirection("LOAN_RECEIVED"));
        Assert.Equal(+1, CapitalRules.TreasuryDirection("OPENING_BALANCE"));
        Assert.Equal(-1, CapitalRules.TreasuryDirection("OWNER_WITHDRAWAL"));
        Assert.Equal(-1, CapitalRules.TreasuryDirection("LOAN_REPAYMENT"));
    }

    [Fact]
    public void T14_OpeningBalance_RequiresApproval()
    {
        // D-14: الرصيد الافتتاحي باعتماد موثق فقط — ما سواه من أحداث رأس المال مباشر
        Assert.True(CapitalRules.RequiresApproval("OPENING_BALANCE"));
        Assert.False(CapitalRules.RequiresApproval("OWNER_CONTRIBUTION"));
        Assert.False(CapitalRules.RequiresApproval("OWNER_WITHDRAWAL"));
        Assert.False(CapitalRules.RequiresApproval("LOAN_RECEIVED"));
        Assert.False(CapitalRules.RequiresApproval("LOAN_REPAYMENT"));
    }

    // ═══ 5) سياسة طباعة G8 وخصوصية الرواتب (§29.10/§31/§35.2 — اختبار 21) ═

    [Fact]
    public void T15_PayrollDocuments_NeverRouteToPublicPrinter()
    {
        // محطة فيها طابعة مالية عامة فقط (لا مؤمنة): مستند رواتب يُرفض — لا توجيه للعام إطلاقاً
        var denied = FinancePrintPolicy.Route("PAYROLL_SLIP",
            stationHasConfidentialPrinter: false, stationHasFinancePrinter: true, stationHasReceiptPrinter: true);
        Assert.False(denied.Allowed);
        Assert.Equal("PAYROLL_PRINTER_CONFIDENTIAL_REQUIRED", denied.DenyCode);
        Assert.Equal(FinancePrintPolicy.ConfidentialRole, denied.TargetRole);

        // محطة فيها الطابعة المؤمنة: يُوجَّه إليها حصراً
        var allowed = FinancePrintPolicy.Route("PAYROLL_SLIP",
            stationHasConfidentialPrinter: true, stationHasFinancePrinter: true, stationHasReceiptPrinter: true);
        Assert.True(allowed.Allowed);
        Assert.Equal(FinancePrintPolicy.ConfidentialRole, allowed.TargetRole);

        // سند سداد الراتب كذلك (PAYROLL_PAYMENT_VOUCHER — §29.10)
        var voucherDenied = FinancePrintPolicy.Route("PAYROLL_PAYMENT_VOUCHER", false, true, true);
        Assert.False(voucherDenied.Allowed);
    }

    [Fact]
    public void T16_FinanceDocuments_RouteByRole()
    {
        // السندات المالية والمصروف وملخص الإقفال → دور طابعة المالية
        Assert.Equal(FinancePrintPolicy.FinanceVouchersRole, FinancePrintPolicy.TargetRole("FINANCE_RECEIPT_VOUCHER"));
        Assert.Equal(FinancePrintPolicy.FinanceVouchersRole, FinancePrintPolicy.TargetRole("FINANCE_DISBURSEMENT_VOUCHER"));
        Assert.Equal(FinancePrintPolicy.FinanceVouchersRole, FinancePrintPolicy.TargetRole("FINANCE_TRANSFER_VOUCHER"));
        Assert.Equal(FinancePrintPolicy.FinanceVouchersRole, FinancePrintPolicy.TargetRole("EXPENSE_VOUCHER"));
        Assert.Equal(FinancePrintPolicy.FinanceVouchersRole, FinancePrintPolicy.TargetRole("SHIFT_CLOSE_SUMMARY"));
        // ملحق مبيعات الكاشير → رول المحطة (§31.2)
        Assert.Equal(FinancePrintPolicy.CustomerReceiptRole, FinancePrintPolicy.TargetRole("CASHIER_SALES_APPENDIX"));

        // لا طابعة مالية معينة: رفض موثق بلا مهمة (لا فشل صامت)
        var noFinance = FinancePrintPolicy.Route("FINANCE_RECEIPT_VOUCHER", false, false, true);
        Assert.False(noFinance.Allowed);
        Assert.Equal("FINANCE_PRINTER_NOT_ASSIGNED", noFinance.DenyCode);
    }

    [Fact]
    public void T17_SummaryBadge_RequiredOnSummaries()
    {
        // وسم «ملخص» إلزامي على ما لا يسرد كل التفاصيل (PMR-016)
        Assert.True(FinancePrintPolicy.RequiresSummaryBadge("CASHIER_SALES_APPENDIX"));
        Assert.True(FinancePrintPolicy.RequiresSummaryBadge("SHIFT_CLOSE_SUMMARY"));
        Assert.False(FinancePrintPolicy.RequiresSummaryBadge("FINANCE_RECEIPT_VOUCHER"));
        Assert.False(FinancePrintPolicy.RequiresSummaryBadge("PAYROLL_SLIP"));
    }
}
