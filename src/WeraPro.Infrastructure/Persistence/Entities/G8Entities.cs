using System.ComponentModel.DataAnnotations.Schema;

namespace WeraPro.Infrastructure.Persistence.Entities;

// ═══════════════ G8: finance.* (V006 قاعدة + V019 استكمال) — السندات والمصروفات
// ورأس المال وعدّات النقدية وتوزيعات السندات (§14/§13) ═══════════════════════

[Table("vouchers", Schema = "finance")]
public sealed class FinanceVoucherRow
{
    public long VoucherId { get; set; }
    public required string VoucherNumber { get; set; }
    public string VoucherType { get; set; } = "RECEIPT";
    public long TreasuryAccountId { get; set; }
    public long? CounterTreasuryAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? CounterpartyType { get; set; }
    public long? CounterpartyId { get; set; }
    public required string Reason { get; set; }
    public string Status { get; set; } = "DRAFT";
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? ShiftId { get; set; }        // V019 (G8): وردية سياق السند عند تضمينه درج محطة
}

[Table("voucher_allocations", Schema = "finance")]
public sealed class VoucherAllocationRow
{
    public long AllocationId { get; set; }
    public long VoucherId { get; set; }
    public long InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("expense_categories", Schema = "finance")]
public sealed class ExpenseCategoryRow
{
    public long CategoryId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public long? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("expenses", Schema = "finance")]
public sealed class ExpenseRow
{
    public long ExpenseId { get; set; }
    public required string ExpenseNumber { get; set; }
    public long CategoryId { get; set; }
    public long BranchId { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public long? TreasuryAccountId { get; set; }
    public string? Beneficiary { get; set; }
    public string? CostCenter { get; set; }
    public string Status { get; set; } = "DRAFT";
    public long? ApprovalId { get; set; }
    public long? AttachmentMediaId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? ShiftId { get; set; }        // V019 (G8): مصروف الوردية (§32)
}

[Table("capital_events", Schema = "finance")]
public sealed class CapitalEventRow
{
    public long CapitalId { get; set; }
    public required string EventNumber { get; set; }
    public string EventType { get; set; } = "OWNER_CONTRIBUTION";
    public decimal Amount { get; set; }
    public long? TreasuryAccountId { get; set; }
    public DateOnly EventDate { get; set; }
    public required string Description { get; set; }
    public string Status { get; set; } = "DRAFT";
    public long? ApprovalId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("shift_cash_counts", Schema = "finance")]
public sealed class ShiftCashCountRow
{
    public long CountId { get; set; }
    public long ShiftId { get; set; }
    public decimal CountedCash { get; set; }
    public long CountedBy { get; set; }
    public DateTimeOffset CountedAt { get; set; }
    public string? Notes { get; set; }
}
