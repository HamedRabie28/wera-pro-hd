namespace WeraPro.Application.Finance;

using WeraPro.Domain.SharedKernel;

/// <summary>خرق سياسة مالية موثق برمز ثابت ورسالة عربية — نمط SalesPolicyException (G7).</summary>
public sealed class FinancePolicyException(string code, string userMessageAr) : Exception(userMessageAr)
{
    public string Code { get; } = code;
    public string UserMessageAr { get; } = userMessageAr;
}

// ── بيانات قراءة مشتركة ──────────────────────────────────────────────────────

public sealed record TreasuryAccountData(
    long TreasuryAccountId, string Code, string NameAr, string AccountKind,
    long? BranchId, long? StationId, bool IsActive, decimal CurrentBalance);

public sealed record TreasuryTxnData(
    long TxnId, long TreasuryAccountId, string AccountCode, string AccountNameAr, string AccountKind,
    int Direction, decimal Amount, string SourceDocType, long SourceDocId,
    long? ShiftId, string? Reason, DateTimeOffset OccurredAtUtc, string CreatedByNameAr);

public sealed record ExpenseCategoryData(long CategoryId, string Code, string NameAr, bool IsActive);

public sealed record VoucherAllocationData(long InvoiceId, string InvoiceNumber, decimal Amount, decimal InvoiceRemainingAfter);

public sealed record VoucherData(
    long VoucherId, string VoucherNumber, string VoucherType, string Status,
    long TreasuryAccountId, string TreasuryCode, string TreasuryNameAr,
    long? CounterTreasuryAccountId, string? CounterTreasuryCode,
    decimal Amount, string? CounterpartyType, long? CounterpartyId, string? CounterpartyNameAr,
    string Reason, long? ShiftId, DateTimeOffset CreatedAtUtc, DateTimeOffset? PostedAtUtc,
    IReadOnlyList<VoucherAllocationData> Allocations);

public sealed record ExpenseData(
    long ExpenseId, string ExpenseNumber, string Status,
    long CategoryId, string CategoryNameAr, long BranchId, DateOnly ExpenseDate,
    decimal Amount, decimal TaxAmount, decimal CashOutflow, string PaymentMethod,
    long? TreasuryAccountId, string? TreasuryCode, string? TreasuryNameAr,
    string? Beneficiary, string? CostCenter, long? ShiftId, long? ApprovalId,
    string? Notes, long CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? PostedAtUtc);

public sealed record CapitalEventData(
    long CapitalId, string EventNumber, string EventType, string Status,
    decimal Amount, long? TreasuryAccountId, string? TreasuryCode,
    DateOnly EventDate, string Description, long? ApprovalId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? PostedAtUtc);

/// <summary>فاتورة آجلة قائمة لعميل — لاختيار التوزيع في سند القبض (§13).</summary>
public sealed record CustomerReceivableData(
    long InvoiceId, string InvoiceNumber, DateTimeOffset PostedAtUtc, string Status,
    decimal DueAmount, decimal AllocatedPosted, decimal Remaining);

// ── الورديات: الإغلاق والمطابقة (§14.1/§9.9 — استكمال G7) ────────────────────

public sealed record ShiftListData(
    long ShiftId, int ShiftNumber, long StationId, long BranchId, string StationCode, string StationNameAr,
    string OpenedByDisplayAr, DateTimeOffset OpenedAtUtc, decimal OpeningFloat, string Status,
    DateTimeOffset? ClosedAtUtc, string? ClosedByDisplayAr,
    decimal? ExpectedCash, decimal? CountedCash, decimal? Variance, string? VarianceReason,
    long? VarianceSettledByVoucherId);

/// <summary>مكونات المتوقع في الدرج من الدفتر — المرجع: ShiftReconciliation.ExpectedComponents.</summary>
public sealed record ShiftDrawerLedgerData(
    decimal OpeningFloat, decimal CashSales, decimal CashRefunds, decimal DrawerExpenses,
    decimal VoucherDeposits, decimal VoucherWithdrawals, decimal TransfersIn, decimal TransfersOut,
    decimal NetLedgerMovement, decimal ExpectedInDrawer,
    decimal CardCollected, decimal CreditIssued, int InvoiceCount,
    IReadOnlyList<TreasuryTxnData> Movements);

public sealed record DrawerEventListData(
    long EventId, string EventType, string? Reason, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? RespondedAtUtc, string ResultStatus, string TriggeredByNameAr);

public sealed record CashCountData(
    long CountId, decimal CountedCash, string CountedByNameAr, DateTimeOffset CountedAtUtc, string? Notes);

public sealed record ShiftCloseResultData(
    ShiftListData Shift, CashCountData FinalCount,
    ShiftDrawerLedgerData Reconciliation, decimal Variance);

// ── خطط الترحيل (يبنيها حال الاستخدام وينفذها المستودع ذرياً — نمط G6/G7) ──────

public sealed record VoucherAllocationPlan(long InvoiceId, decimal Amount);

public sealed record VoucherCreatePlan(
    string VoucherType, string VoucherNumber,
    long TreasuryAccountId, long? CounterTreasuryAccountId,
    decimal Amount, string? CounterpartyType, long? CounterpartyId,
    string Reason, long CreatedByUserId, long BranchId, long? ShiftId,
    IReadOnlyList<VoucherAllocationPlan> Allocations);

public sealed record VoucherPostPlan(long VoucherId, long UserId, long? ShiftId, DateTimeOffset PostedAt, long? FallbackBranchId = null);

public sealed record ExpenseCreatePlan(
    string ExpenseNumber, long CategoryId, long BranchId, DateOnly ExpenseDate,
    decimal Amount, decimal TaxAmount, string PaymentMethod,
    long? TreasuryAccountId, string? Beneficiary, string? CostCenter,
    long? ShiftId, string? Notes, long CreatedByUserId, bool PostDirectly);

public sealed record ExpensePostPlan(long ExpenseId, long UserId, long? ShiftId, decimal CashOutflow, DateTimeOffset PostedAt);

public sealed record CapitalCreatePlan(
    string EventNumber, string EventType, decimal Amount, long? TreasuryAccountId,
    DateOnly EventDate, string Description, long? ApprovalId, long CreatedByUserId, long BranchId);

// ── مستودع G8 المالي — كل الترحيلات ذرية وتعيد النتيجة أو ترمي FinancePolicyException ──

public interface IFinanceRepository
{
    // الخزائن
    Task<IReadOnlyList<TreasuryAccountData>> ListTreasuryAccountsAsync(bool activeOnly, CancellationToken ct = default);
    Task<TreasuryAccountData?> GetTreasuryAccountAsync(long treasuryAccountId, CancellationToken ct = default);
    /// <summary>درج محطة بعينها: درج المحطة أولاً ثم درج فرعها (توافق خلفي مع G7) — الحقيقة الفيزيائية: درج لكل محطة.</summary>
    Task<TreasuryAccountData?> FindDrawerAccountAsync(long stationId, CancellationToken ct = default);
    Task<Result<TreasuryAccountData>> CreateTreasuryAccountAsync(
        string code, string nameAr, string accountKind, long? branchId, long? stationId, CancellationToken ct = default);

    // حركات الخزينة
    Task<(IReadOnlyList<TreasuryTxnData> Items, int TotalCount)> ListTreasuryTransactionsAsync(
        long? treasuryAccountId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int page, int pageSize, CancellationToken ct = default);

    // السندات (§14.2/§13)
    Task<string> NextVoucherNumberAsync(string voucherType, CancellationToken ct = default);
    Task<Result<VoucherData>> CreateVoucherAsync(VoucherCreatePlan plan, CancellationToken ct = default);
    Task<Result<VoucherData>> PostVoucherAsync(VoucherPostPlan plan, CancellationToken ct = default);
    Task<Result<VoucherData>> CancelVoucherDraftAsync(long voucherId, long userId, string reasonAr, CancellationToken ct = default);
    Task<VoucherData?> GetVoucherAsync(long voucherId, CancellationToken ct = default);
    Task<(IReadOnlyList<VoucherData> Items, int TotalCount)> ListVouchersAsync(
        string? voucherType, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerReceivableData>> GetCustomerReceivablesAsync(long customerId, CancellationToken ct = default);
    Task<bool> CustomerExistsAsync(long customerId, CancellationToken ct = default);
    Task<bool> SupplierExistsAsync(long supplierId, CancellationToken ct = default);
    Task<bool> UserExistsAsync(long userId, CancellationToken ct = default);

    // المصروفات (§14.4)
    Task<string> NextExpenseNumberAsync(CancellationToken ct = default);
    Task<Result<ExpenseData>> CreateExpenseAsync(ExpenseCreatePlan plan, CancellationToken ct = default);
    Task<Result<ExpenseData>> ApproveExpenseAsync(long expenseId, long approverUserId, string? reasonAr, CancellationToken ct = default);
    Task<Result<ExpenseData>> PostExpenseAsync(ExpensePostPlan plan, CancellationToken ct = default);
    Task<Result<ExpenseData>> CancelExpenseAsync(long expenseId, long userId, string reasonAr, CancellationToken ct = default);
    Task<ExpenseData?> GetExpenseAsync(long expenseId, CancellationToken ct = default);
    Task<(IReadOnlyList<ExpenseData> Items, int TotalCount)> ListExpensesAsync(
        string? status, long? shiftId, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseCategoryData>> ListExpenseCategoriesAsync(bool activeOnly, CancellationToken ct = default);
    Task<Result<ExpenseCategoryData>> CreateExpenseCategoryAsync(string code, string nameAr, CancellationToken ct = default);

    // رأس المال (§14.3/D-14)
    Task<string> NextCapitalEventNumberAsync(CancellationToken ct = default);
    Task<Result<CapitalEventData>> CreateCapitalEventAsync(CapitalCreatePlan plan, CancellationToken ct = default);
    Task<Result<CapitalEventData>> PostCapitalEventAsync(long capitalId, long userId, long? fallbackBranchId, CancellationToken ct = default);
    Task<Result<CapitalEventData>> CancelCapitalEventDraftAsync(long capitalId, long userId, string reasonAr, CancellationToken ct = default);
    Task<CapitalEventData?> GetCapitalEventAsync(long capitalId, CancellationToken ct = default);
    Task<(IReadOnlyList<CapitalEventData> Items, int TotalCount)> ListCapitalEventsAsync(
        string? eventType, string? status, int page, int pageSize, CancellationToken ct = default);
    /// <summary>اعتماد موثق في core.approvals (المعتمد ≠ الطالب — قيد no_self) — يُنشأ داخلياً عند اعتماد المصروف.</summary>
    Task<bool> IsCapitalApprovalApprovedAsync(long approvalId, CancellationToken ct = default);

    // الورديات: الإغلاق والمطابقة (§14.1 — استكمال G7)
    Task<ShiftListData?> GetShiftAsync(long shiftId, CancellationToken ct = default);
    Task<(IReadOnlyList<ShiftListData> Items, int TotalCount)> ListShiftsAsync(
        long? stationId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<ShiftDrawerLedgerData?> GetShiftDrawerLedgerAsync(long shiftId, long drawerAccountId, CancellationToken ct = default);
    Task<IReadOnlyList<DrawerEventListData>> ListDrawerEventsAsync(long shiftId, CancellationToken ct = default);
    Task<IReadOnlyList<CashCountData>> ListCashCountsAsync(long shiftId, CancellationToken ct = default);
    Task<Result<ShiftCloseResultData>> CloseShiftAsync(
        long shiftId, long closedByUserId, decimal expectedCash, decimal countedCash,
        string? varianceReasonAr, string? notesAr, CancellationToken ct = default);
    /// <summary>ربط سند تسوية الفارق بالوردية المغلقة — الربط وحده يُسمح به على المغلقة (§14.1).</summary>
    Task<Result<long>> LinkVarianceSettlementAsync(long shiftId, long voucherId, CancellationToken ct = default);

    // طباعة مستندات G8 (§29.10/§31/§35.2 — خصوصية الرواتب مفروضة في التوجيه)
    Task<StationRolePrinterData?> GetStationRolePrinterAsync(long stationId, string documentRole, CancellationToken ct = default);
    Task<PrintOutcome> CreateFinancePrintJobAsync(
        long stationId, long userId, string documentType, long documentId,
        string copyType, long? reprintOfJobId, CancellationToken ct = default);
}

/// <summary>تعيين طابعة محطة لدور مستند (print.printer_document_roles + printer_profiles).</summary>
public sealed record StationRolePrinterData(
    long PrinterProfileId, int PrinterProfileVersion, string MediaClass,
    decimal PrintableWidthMm, int Dpi, bool IsCalibrated, bool IsAutoRoutingEnabled);

public sealed record PrintOutcome(
    bool JobCreated, long? JobId, string? JobStatus, string? MediaClass,
    string? HoldReasonCode, string NoticeAr);
