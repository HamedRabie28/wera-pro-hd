namespace WeraPro.Domain.Finance;

using WeraPro.Domain.SharedKernel;

// ═══════════════════════════════════════════════════════════════════════════
// G8 — الورديات والنقد والأدراج والمصروفات ورأس المال (GFM-CSharp 2.4.1 §14/§13)
// قواعد مجال نقية قابلة للاختبار بمراجع مستقلة — لا IO ولا تبعيات هنا.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// مطابقة نقدية الوردية (§14.1/بوابة قبول G8): النقد المتوقع يطابق قواعد الحركات —
/// المتوقع في الدرج = الرصيد الافتتاحي + Σ(حركات الدرج × اتجاهها) بوردية المحطة.
/// الفارق = الفعلي − المتوقع، ويظل ظاهراً على الوردية حتى التسوية بمستند موثق.
/// </summary>
public static class ShiftReconciliation
{
    /// <summary>المتوقع في الدرج من مكونات الدفتر — المجموع يجب أن يساوي مجموع حركات الدرج بوردية المحطة.</summary>
    public sealed record ExpectedComponents(
        decimal OpeningFloat,
        decimal CashSales,          // مدفوعات نقدية (CASH/SPLIT_CASH) لفواتير الوردية
        decimal CashRefunds,        // ردود نقدية وقعت في الوردية (خروج من الدرج)
        decimal DrawerExpenses,     // مصروفات مدفوعة من الدرج خلال الوردية
        decimal VoucherDeposits,    // سندات قبض أُودعت في الدرج خلال الوردية
        decimal VoucherWithdrawals, // سندات صرف من الدرج خلال الوردية (غير المصروفات)
        decimal TransfersIn,        // تحويلات إلى الدرج (من خزينة/بنك)
        decimal TransfersOut)       // تحويلات من الدرج (إيداع في الخزينة مثلاً)
    {
        public decimal ExpectedInDrawer =>
            OpeningFloat + CashSales - CashRefunds - DrawerExpenses
            + VoucherDeposits - VoucherWithdrawals + TransfersIn - TransfersOut;
    }

    /// <summary>حساب الفارق: الفعلي المعدود − المتوقع من الدفتر (§14.1).</summary>
    public static decimal Variance(decimal countedCash, decimal expectedCash) => countedCash - expectedCash;

    /// <summary>الفارق ≠ 0 يستلزم سبباً موثقاً لا يقل 3 محارف (بوابة القبول: الفارق يبقى ظاهراً حتى التسوية).</summary>
    public static bool RequiresReason(decimal variance) => variance != 0m;

    public static bool ReasonIsValid(string? reasonAr) =>
        !string.IsNullOrWhiteSpace(reasonAr) && reasonAr.Trim().Length >= 3;

    /// <summary>
    /// صلاحية الإغلاق بحسب الدور (§14.1 «واعتماد بحسب الدور»): الإغلاق المتطابق لكل حائز
    /// إغلاق وردية؛ الإغلاق بفارق يتطلب اعتماد صلاحية الفوارق (مشرف فأعلى — الكاشير لا يعتمد فارقه بنفسه).
    /// </summary>
    public static bool MayClose(bool hasShiftClose, bool hasVarianceApproval, decimal variance) =>
        hasShiftClose && (variance == 0m || hasVarianceApproval);
}

/// <summary>
/// فصل وسائل الدفع في الوردية (§14.1 ق4/بوابة القبول): النقد في الدرج ≠ البطاقة لدى المزود ≠
/// الآجل في الذمم — ثلاثة أرقام منفصلة لا رقماً واحداً، والبطاقة لا تدخل متوقع الدرج أبداً.
/// </summary>
public static class TenderSeparation
{
    public sealed record Separated(
        decimal CashCollected, decimal CardCollected, decimal CreditIssued,
        decimal CashRefunded, decimal ReturnsTotal);
}

/// <summary>
/// قواعد السندات (§14.2/§13): قبض/صرف/تحويل — التحويل الداخلي لا يولد إيراداً ولا مصروفاً،
/// وله حركتا خزينة متقابلتان، والتوزيع على فواتير العميل ≤ المتبقي.
/// </summary>
public static class VoucherRules
{
    public const string Receipt = "RECEIPT";
    public const string Disbursement = "DISBURSEMENT";
    public const string Transfer = "TRANSFER";

    public static readonly IReadOnlySet<string> Types =
        new HashSet<string>(StringComparer.Ordinal) { Receipt, Disbursement, Transfer };

    public static readonly IReadOnlySet<string> CounterpartyTypes =
        new HashSet<string>(StringComparer.Ordinal) { "CUSTOMER", "SUPPLIER", "EMPLOYEE", "OWNER", "OTHER" };

    /// <summary>البادئة المرجعية لرقم السند حسب نوعه (تسلسل V019).</summary>
    public static string NumberPrefix(string voucherType) => voucherType switch
    {
        Receipt => "RV",
        Disbursement => "DV",
        Transfer => "TV",
        _ => throw new ArgumentOutOfRangeException(nameof(voucherType)),
    };

    /// <summary>تحقق إنشاء السند: النوع والوجهة والطرف والسبب والمبلغ (القيود الدلالية فوق قيود V006).</summary>
    public static DomainError? ValidateCreate(
        string voucherType, decimal amount, long treasuryAccountId,
        long? counterTreasuryAccountId, string? counterpartyType, long? counterpartyId, string reasonAr)
    {
        if (!Types.Contains(voucherType))
        {
            return new("VOUCHER_TYPE_INVALID", "نوع السند يجب أن يكون قبضاً أو صرفاً أو تحويلاً");
        }
        if (amount <= 0m)
        {
            return new("VOUCHER_AMOUNT_INVALID", "مبلغ السند يجب أن يكون أكبر من صفر");
        }
        if (reasonAr is null || reasonAr.Trim().Length < 3)
        {
            return new("VOUCHER_REASON_REQUIRED", "سبب السند موثق لا يقل عن 3 محارف (§14.2 ق4)");
        }
        if (voucherType == Transfer)
        {
            if (counterTreasuryAccountId is not { } counter || counter == treasuryAccountId)
            {
                return new("VOUCHER_TRANSFER_ACCOUNTS_INVALID", "التحويل يستلزم وجهة مختلفة عن المصدر (قيد V006)");
            }
            if (counterpartyType is not null)
            {
                return new("VOUCHER_TRANSFER_NO_COUNTERPARTY", "التحويل الداخلي بلا طرف تجاري (قيد V006)");
            }
        }
        else
        {
            if (counterpartyType is not null && !CounterpartyTypes.Contains(counterpartyType))
            {
                return new("VOUCHER_COUNTERPARTY_TYPE_INVALID", "نوع الطرف غير معروف");
            }
            // الطرف الموثق بمعرف لكل من له سجل (عميل/مورد/موظف) — المالك وطرف آخر بلا معرف سجل
            if (counterpartyType is ("CUSTOMER" or "SUPPLIER" or "EMPLOYEE") && counterpartyId is null)
            {
                return new("VOUCHER_COUNTERPARTY_REQUIRED", "تحديد الطرف يستلزم معرفه");
            }
            if (counterpartyType is null && counterpartyId is not null)
            {
                return new("VOUCHER_COUNTERPARTY_TYPE_REQUIRED", "معرّف الطرف بلا نوع طرف");
            }
        }
        return null;
    }

    /// <summary>
    /// أثر الترحيل: القبض/الصرف حركة واحدة؛ التحويل حركتان متقابلتان بالمبلغ نفسه —
    /// التحويل الداخلي لا يولد إيراداً ولا مصروفاً (§14.2 ق2).
    /// </summary>
    public sealed record PostingLeg(long TreasuryAccountId, int Direction, decimal Amount);

    public static IReadOnlyList<PostingLeg> PostingLegs(
        string voucherType, decimal amount, long treasuryAccountId, long? counterTreasuryAccountId) =>
        voucherType switch
        {
            Receipt => [new PostingLeg(treasuryAccountId, +1, amount)],
            Disbursement => [new PostingLeg(treasuryAccountId, -1, amount)],
            Transfer =>
            [
                new PostingLeg(treasuryAccountId, -1, amount),
                new PostingLeg(counterTreasuryAccountId!.Value, +1, amount),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(voucherType)),
        };

    /// <summary>التوزيع على فواتير العميل لسند قبض من عميل فقط (§13/PRT-003).</summary>
    public static bool MayAllocate(string voucherType, string? counterpartyType) =>
        voucherType == Receipt && counterpartyType == "CUSTOMER";

    /// <summary>لا تخصيص أكبر من المتبقي دون سياسة معتمدة (§13 ق3).</summary>
    public static DomainError? ValidateAllocation(decimal allocation, decimal invoiceRemaining) =>
        allocation <= 0m
            ? new("ALLOCATION_AMOUNT_INVALID", "مبلغ التوزيع يجب أن يكون أكبر من صفر")
            : allocation > invoiceRemaining + 0.00005m
                ? new("ALLOCATION_EXCEEDS_REMAINING",
                    $"التوزيع ({allocation:0.00}) يتجاوز متبقي الفاتورة ({invoiceRemaining:0.00}) — منع تخصيص أكبر من المتبقي (§13)")
                : null;
}

/// <summary>
/// قواعد المصروفات (§14.4): اعتماد ثم سداد، وأثر مالي واحد، والإلغاء المرحَّل بعكس موثق لا حذف.
/// القاعدة الموثقة لمصروف الوردية المدفوع من درج المحطة (§14.1 «عمليات صرف/إيداع مسموح بها»
/// و§32 «افصل مصروف الوردية المدفوع من الدرج»): المال خرج فعلاً فيسجَّل مرحّلاً مباشرة من حائز
/// تسجيل المصروف داخل جلسة محطة بوردية مفتوحة — بلا صلاحية الترحيل العامة، والتدقيق والفارق يحيطانه.
/// </summary>
public static class ExpenseRules
{
    public const string Draft = "DRAFT";
    public const string Approved = "APPROVED";
    public const string Posted = "POSTED";
    public const string Cancelled = "CANCELLED";

    /// <summary>المسدد من الخزينة = المبلغ + الضريبة (ضريبة المصروف تظهر منفصلة — FINANCIAL_DEFINITIONS ق8).</summary>
    public static decimal CashOutflow(decimal amount, decimal taxAmount) => amount + taxAmount;

    /// <summary>مصروف وردية من درج المحطة: الخزينة درج والسجل داخل وردية مفتوحة — القاعدة الموثقة أعلاه.</summary>
    public static bool IsShiftDrawerPaidOut(long? shiftId, long? treasuryAccountId, long drawerAccountId) =>
        shiftId is not null && treasuryAccountId == drawerAccountId;

    /// <summary>هل يجوز الترحيل المباشر (DRAFT→POSTED) لهذا المستخدم؟ — مصروف الوردية من الدرج فقط.</summary>
    public static bool MayPostDirectly(long? shiftId, long? treasuryAccountId, long drawerAccountId) =>
        IsShiftDrawerPaidOut(shiftId, treasuryAccountId, drawerAccountId);

    /// <summary>إلغاء المرحَّل يستلزم سبباً موثقاً ≥ 3 محارف (عكس موثق لا حذف تاريخي — §14.4).</summary>
    public static bool ReversalReasonIsValid(string? reasonAr) =>
        !string.IsNullOrWhiteSpace(reasonAr) && reasonAr.Trim().Length >= 3;
}

/// <summary>
/// قواعد رأس المال (§14.3/D-14): حدث موثق بكل مرة — مساهمة/سحب/تمويل/افتتاحي —
/// ورأس المال لا يُساوى بالمبيعات أو الصندوق أو قيمة المخزون، والافتتاحي باعتماد فقط.
/// </summary>
public static class CapitalRules
{
    public const string OwnerContribution = "OWNER_CONTRIBUTION";
    public const string OwnerWithdrawal = "OWNER_WITHDRAWAL";
    public const string LoanReceived = "LOAN_RECEIVED";
    public const string LoanRepayment = "LOAN_REPAYMENT";
    public const string OpeningBalance = "OPENING_BALANCE";

    public static readonly IReadOnlySet<string> EventTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            OwnerContribution, OwnerWithdrawal, LoanReceived, LoanRepayment, OpeningBalance,
        };

    /// <summary>اتجاه الأثر النقدي: مساهمة/تمويل/افتتاحي = قبض (+1)؛ سحب/سداد تمويل = صرف (−1).</summary>
    public static int TreasuryDirection(string eventType) => eventType switch
    {
        OwnerContribution or LoanReceived or OpeningBalance => +1,
        OwnerWithdrawal or LoanRepayment => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    /// <summary>الرصيد الافتتاحي يتطلب اعتماداً موثقاً قبل الترحيل (قيد V006 عند الإنشاء + تحقق الخدمة) — D-14/B-04.</summary>
    public static bool RequiresApproval(string eventType) => eventType == OpeningBalance;

    /// <summary>المستندات الافتتاحية الفارغة تظل موسومة «بانتظار اعتماد» حتى تسليم الأرصدة الفعلية (D-14 — B-04 معلن).</summary>
    public const string PendingOwnerApprovalNoteAr = "بانتظار اعتماد — وثيقة افتتاحية لا تُرحَّل حتى تسليم الأرصدة المعتمدة (D-14/B-04)";
}

/// <summary>
/// سياسة طباعة مستندات G8 (§29.10/§31/§32/§35.2): السندات والمصروفات على طابعة المالية،
/// وكشوف الرواتب وسنداتها على الطابعة المؤمنة ONLY — لا تطبع تفاصيل الرواتب على طابعة عامة
/// أبداً ولا يُحوَّل الوسيط عند الفشل، ووسم «ملخص» على الملخصات (PMR-016).
/// </summary>
public static class FinancePrintPolicy
{
    /// <summary>مستندات الرواتب السرية — توجيهها حصراً لدور CONFIDENTIAL_PAYROLL (اختبار 21 · §31.1/§35.2).</summary>
    public static readonly IReadOnlySet<string> ConfidentialDocumentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "PAYROLL_SLIP", "PAYROLL_PAYMENT_VOUCHER" };

    /// <summary>مستندات طابعة المالية (سندات القبض/الصرف/التحويل والمصروف وملخص الإقفال).</summary>
    public static readonly IReadOnlySet<string> FinanceVoucherDocumentTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "FINANCE_RECEIPT_VOUCHER", "FINANCE_DISBURSEMENT_VOUCHER", "FINANCE_TRANSFER_VOUCHER",
            "EXPENSE_VOUCHER", "SHIFT_CLOSE_SUMMARY",
        };

    /// <summary>ملخصات رول للمحطة — تطبع على طابعة إيصال المحطة (§31.2 «ملخص رول عند الطلب»).</summary>
    public static readonly IReadOnlySet<string> StationRollSummaryDocumentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "CASHIER_SALES_APPENDIX" };

    /// <summary>دور الطابعة المستهدف لنوع المستند — مستند سري بلا دور مؤمن لا يوجَّه لغيره أبداً.</summary>
    public const string ConfidentialRole = "CONFIDENTIAL_PAYROLL";
    public const string FinanceVouchersRole = "FINANCE_VOUCHERS";
    public const string CustomerReceiptRole = "CUSTOMER_RECEIPT";

    public static string TargetRole(string documentType) =>
        ConfidentialDocumentTypes.Contains(documentType) ? ConfidentialRole
        : FinanceVoucherDocumentTypes.Contains(documentType) ? FinanceVouchersRole
        : StationRollSummaryDocumentTypes.Contains(documentType) ? CustomerReceiptRole
        : throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "نوع مستند غير معروف لسياسة طباعة G8");

    /// <summary>
    /// قرار التوجيه: مستند سري لا يُرسل إلا لدور الطابعة المؤمنة — لا سقوط احتياطي إلى طابعة مالية
    /// أو عامة مهما كانت الحالة (بوابة G8: «لا تطبع تفاصيل الرواتب على طابعة عامة»).
    /// </summary>
    public sealed record RoutingDecision(bool Allowed, string TargetRole, string? DenyCode, string? DenyMessageAr);

    public static RoutingDecision Route(string documentType, bool stationHasConfidentialPrinter, bool stationHasFinancePrinter, bool stationHasReceiptPrinter)
    {
        var role = TargetRole(documentType);
        var hasIt = role switch
        {
            ConfidentialRole => stationHasConfidentialPrinter,
            FinanceVouchersRole => stationHasFinancePrinter,
            _ => stationHasReceiptPrinter,
        };
        if (hasIt)
        {
            return new RoutingDecision(true, role, null, null);
        }
        return role == ConfidentialRole
            ? new RoutingDecision(false, role, "PAYROLL_PRINTER_CONFIDENTIAL_REQUIRED",
                "مستند رواتب سري — لا يُطبع إلا على طابعة مؤمنة بدور Confidential (اختبار 21 · §31.1/§35.2)؛ لم تُعيَّن طابعة مؤمنة لهذه المحطة — لا توجيه لطابعة عامة إطلاقاً")
            : new RoutingDecision(false, role, "FINANCE_PRINTER_NOT_ASSIGNED",
                "لم تُعيَّن طابعة بدور المالية لهذه المحطة — المستند محفوظ إلكترونياً والطباعة عند الطلب بعد التعيين (§29.10)");
    }

    /// <summary>وسم «ملخص» إلزامي على الإيصالات التي لا تسرد كل التفاصيل (PMR-016).</summary>
    public static bool RequiresSummaryBadge(string documentType) =>
        StationRollSummaryDocumentTypes.Contains(documentType) || documentType == "SHIFT_CLOSE_SUMMARY";
}
