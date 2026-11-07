namespace WeraPro.Contracts.V1;

/// <summary>عقود G8 — الورديات والنقد والأدراج والمصروفات ورأس المال (§14/§13/§9.9/§29.10/§31).</summary>
public static class FinanceApi
{
    public const string Prefix = ApiRoutes.Prefix + "/finance";
}

// ── الخزائن والأدراج (§14.2 — درج المحطة ≠ الخزينة ≠ البنك ≠ مزود الدفع) ──────

public sealed record TreasuryAccountBalanceDto(
    long TreasuryAccountId, string Code, string NameAr, string AccountKind,
    long? BranchId, long? StationId, bool IsActive, decimal CurrentBalance);

public sealed record CreateTreasuryAccountRequest(
    string? Code, string? NameAr, string? AccountKind, long? BranchId, long? StationId);

public sealed record TreasuryTxnDto(
    long TxnId, long TreasuryAccountId, string AccountCode, string AccountNameAr, string AccountKind,
    int Direction, decimal Amount, string SourceDocType, long SourceDocId,
    long? ShiftId, string? Reason, DateTimeOffset OccurredAtUtc, string CreatedByNameAr);

public sealed record TreasuryTxnPagedDto(
    IReadOnlyList<TreasuryTxnDto> Items, int TotalCount, int Page, int PageSize);

// ── السندات: قبض/صرف/تحويل (§14.2) والتوزيع على الفواتير (§13/PRT-003) ────────

public sealed record VoucherAllocationRequest(long InvoiceId, decimal Amount);

public sealed record CreateVoucherRequest(
    string VoucherType,               // RECEIPT | DISBURSEMENT | TRANSFER
    long TreasuryAccountId,
    long? CounterTreasuryAccountId,   // إلزامي للتحويل
    decimal Amount,
    string? CounterpartyType,         // CUSTOMER | SUPPLIER | EMPLOYEE | OWNER | OTHER
    long? CounterpartyId,
    string? ReasonAr,
    IReadOnlyList<VoucherAllocationRequest>? Allocations);

public sealed record VoucherAllocationDto(long InvoiceId, string InvoiceNumber, decimal Amount, decimal InvoiceRemainingAfter);

public sealed record VoucherDto(
    long VoucherId, string VoucherNumber, string VoucherType, string Status,
    long TreasuryAccountId, string TreasuryCode, string TreasuryNameAr,
    long? CounterTreasuryAccountId, string? CounterTreasuryCode,
    decimal Amount, string? CounterpartyType, long? CounterpartyId, string? CounterpartyNameAr,
    string Reason, long? ShiftId, DateTimeOffset CreatedAtUtc, DateTimeOffset? PostedAtUtc,
    IReadOnlyList<VoucherAllocationDto> Allocations);

public sealed record VoucherPagedDto(IReadOnlyList<VoucherDto> Items, int TotalCount, int Page, int PageSize);

public sealed record CustomerReceivableDto(
    long InvoiceId, string InvoiceNumber, DateTimeOffset PostedAtUtc, string Status,
    decimal DueAmount, decimal AllocatedPosted, decimal Remaining);

public sealed record CancelRequest(string? ReasonAr);

// ── المصروفات (§14.4) ────────────────────────────────────────────────────────

public sealed record CreateExpenseRequest(
    long CategoryId,
    decimal Amount,
    decimal TaxAmount,
    string PaymentMethod,              // CASH | BANK_TRANSFER | CHEQUE | CARD
    long? TreasuryAccountId,           // إلزامي عند السداد/الترحيل
    string? Beneficiary,
    string? CostCenter,
    DateOnly? ExpenseDate,
    long? ShiftId,                     // مصروف وردية من درج المحطة (§32)
    string? NotesAr);

public sealed record ExpenseDto(
    long ExpenseId, string ExpenseNumber, string Status,
    long CategoryId, string CategoryNameAr, long BranchId, DateOnly ExpenseDate,
    decimal Amount, decimal TaxAmount, decimal CashOutflow, string PaymentMethod,
    long? TreasuryAccountId, string? TreasuryCode, string? TreasuryNameAr,
    string? Beneficiary, string? CostCenter, long? ShiftId, long? ApprovalId,
    string? Notes, long CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? PostedAtUtc);

public sealed record ExpensePagedDto(IReadOnlyList<ExpenseDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>استجابة ترحيل المصروف — تتضمن نتيجة الطباعة التلقائية §29.10 (سند المصروف AUTO_AFTER_POST).</summary>
public sealed record ExpensePostResponse(
    ExpenseDto Expense, bool PrintJobCreated, long? PrintJobId, string? PrintJobStatus,
    string? PrintMediaClass, string? PrintHoldReasonCode, string PrintNoticeAr);

public sealed record ExpenseCategoryDto(long CategoryId, string Code, string NameAr, bool IsActive);

public sealed record CreateExpenseCategoryRequest(string? Code, string? NameAr);

// ── رأس المال والأرصدة الافتتاحية (§14.3/D-14) ────────────────────────────────

public sealed record CreateCapitalEventRequest(
    string EventType,                  // OWNER_CONTRIBUTION | OWNER_WITHDRAWAL | LOAN_RECEIVED | LOAN_REPAYMENT | OPENING_BALANCE
    decimal Amount,
    long? TreasuryAccountId,
    DateOnly? EventDate,
    string? DescriptionAr,
    long? ApprovalId);                 // إلزامي لـ OPENING_BALANCE (قيد V006 — D-14)

public sealed record CapitalEventDto(
    long CapitalId, string EventNumber, string EventType, string Status,
    decimal Amount, long? TreasuryAccountId, string? TreasuryCode,
    DateOnly EventDate, string Description, long? ApprovalId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? PostedAtUtc);

public sealed record CapitalPagedDto(IReadOnlyList<CapitalEventDto> Items, int TotalCount, int Page, int PageSize);

// ── الورديات: الإغلاق والمطابقة (§14.1 — استكمال G7) ──────────────────────────

public sealed record CloseShiftRequest(
    decimal CountedCash,
    string? VarianceReasonAr,          // إلزامي عند فارق ≠ 0
    string? NotesAr);

public sealed record ShiftListItemDto(
    long ShiftId, int ShiftNumber, long StationId, string StationCode, string StationNameAr,
    string OpenedByDisplayAr, DateTimeOffset OpenedAtUtc, decimal OpeningFloat, string Status,
    DateTimeOffset? ClosedAtUtc, string? ClosedByDisplayAr,
    decimal? ExpectedCash, decimal? CountedCash, decimal? Variance, string? VarianceReason,
    long? VarianceSettledByVoucherId);

public sealed record ShiftPagedDto(IReadOnlyList<ShiftListItemDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>مطابقة الوردية: مكونات المتوقع من الدفتر + الفعلي والفارق (يبقى ظاهراً حتى التسوية).</summary>
public sealed record ShiftReconciliationDto(
    ShiftListItemDto Shift,
    decimal OpeningFloat, decimal CashSales, decimal CashRefunds, decimal DrawerExpenses,
    decimal VoucherDeposits, decimal VoucherWithdrawals, decimal TransfersIn, decimal TransfersOut,
    decimal ExpectedInDrawer,
    decimal? CountedCash, decimal? Variance, string? VarianceReason, long? VarianceSettledByVoucherId,
    decimal CardCollected, decimal CreditIssued, int InvoiceCount);

public sealed record ShiftCloseResponse(
    ShiftReconciliationDto Reconciliation,
    decimal FinalCountedCash, decimal Variance,
    string CountedByNameAr, DateTimeOffset CountedAtUtc);

public sealed record ShiftCashMovementsDto(
    long DrawerTreasuryAccountId, string DrawerCode, string DrawerNameAr,
    IReadOnlyList<TreasuryTxnDto> Movements, decimal NetLedgerMovement, decimal ExpectedInDrawer);

public sealed record DrawerEventDto(
    long EventId, string EventType, string? Reason, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? RespondedAtUtc, string ResultStatus, string TriggeredByNameAr);

public sealed record CashCountDto(
    long CountId, decimal CountedCash, string CountedByNameAr, DateTimeOffset CountedAtUtc, string? Notes);

// ── طباعة مستندات G8 (§29.10/§31/§35.2 — خصوصية الرواتب) ─────────────────────

public sealed record PrintFinanceDocumentRequest(
    string DocumentType,               // FINANCE_*_VOUCHER | EXPENSE_VOUCHER | PAYROLL_SLIP | PAYROLL_PAYMENT_VOUCHER | CASHIER_SALES_APPENDIX | SHIFT_CLOSE_SUMMARY
    long DocumentId,
    long? ReprintOfJobId);

public sealed record FinancePrintResponse(
    bool JobCreated, long? JobId, string? JobStatus, string? MediaClass,
    string? HoldReasonCode, string NoticeAr, string TargetPrinterRole, bool SummaryBadgeRequired);
