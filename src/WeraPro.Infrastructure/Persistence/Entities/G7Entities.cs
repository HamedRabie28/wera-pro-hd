using System.ComponentModel.DataAnnotations.Schema;

namespace WeraPro.Infrastructure.Persistence.Entities;

// ═══════════════ G7: sales.* (V005) — فواتير وسطور ومدفوعات ومرتجعات وسلال وورديات ═

[Table("invoices", Schema = "sales")]
public sealed class SalesInvoiceRow
{
    public long InvoiceId { get; set; }
    public required string InvoiceNumber { get; set; }
    public long BranchId { get; set; }
    public long StationId { get; set; }
    public long? ShiftId { get; set; }
    public long CashierUserId { get; set; }
    public long? CustomerId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string Currency { get; set; } = "EGP";
    public string TaxModeSnapshot { get; set; } = "VAT_INCLUSIVE";
    public decimal GrossTotal { get; set; }
    public decimal LineDiscountTotal { get; set; }
    public decimal InvoiceDiscount { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal NetTotal { get; set; }
    public decimal PaidTotal { get; set; }
    public decimal ReturnedTotal { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("invoice_lines", Schema = "sales")]
public sealed class SalesInvoiceLineRow
{
    public long LineId { get; set; }
    public long InvoiceId { get; set; }
    public int LineNo { get; set; }
    public long SkuId { get; set; }
    public string SaleMode { get; set; } = "COUNT";
    public long? PackageId { get; set; }
    public decimal Qty { get; set; }
    public long UomId { get; set; }
    public decimal ConversionFactor { get; set; }
    public decimal? WeightKg { get; set; }
    public string? WeightSource { get; set; }
    public decimal? AmountTarget { get; set; }
    public decimal? TareKg { get; set; }
    public decimal UnitPrice { get; set; }
    public long? PriceListId { get; set; }
    public string TaxTreatment { get; set; } = "TAXABLE";
    public decimal TaxRateSnapshot { get; set; }
    public decimal LineDiscount { get; set; }
    public decimal InvoiceDiscountShare { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal NetAmount { get; set; }
    public decimal CostSnapshot { get; set; }
    public string CostMethodSnapshot { get; set; } = "WAC";
    public long? SerialId { get; set; }
    public string? Notes { get; set; }
}

[Table("payments", Schema = "sales")]
public sealed class SalesPaymentRow
{
    public long PaymentId { get; set; }
    public long InvoiceId { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public decimal Amount { get; set; }
    public string Status { get; set; } = "CAPTURED";
    public string? ProviderName { get; set; }
    public string? ProviderReference { get; set; }
    public long? TreasuryAccountId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public long CreatedBy { get; set; }
}

[Table("refunds", Schema = "sales")]
public sealed class SalesRefundRow
{
    public long RefundId { get; set; }
    public required string RefundNumber { get; set; }
    public long OriginalInvoiceId { get; set; }
    public long BranchId { get; set; }
    public long? ApprovalId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string RefundMethod { get; set; } = "CASH";
    public bool Restock { get; set; } = true;
    public decimal Total { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
}

[Table("refund_lines", Schema = "sales")]
public sealed class SalesRefundLineRow
{
    public long LineId { get; set; }
    public long RefundId { get; set; }
    public long OriginalLineId { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitRefund { get; set; }
    public decimal LineTotal { get; set; }
}

[Table("held_carts", Schema = "sales")]
public sealed class HeldCartRow
{
    public long CartId { get; set; }
    public long StationId { get; set; }
    public long HeldBy { get; set; }
    public long? CustomerId { get; set; }
    public required string CartSnapshot { get; set; }   // JSONB عبر تحويل النص
    public string? NoteAr { get; set; }                 // V018: ملاحظة التعليق (§9.5 قائمة المعلقات)
    public DateTimeOffset HeldAt { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public long? RestoredBy { get; set; }
}

[Table("shifts", Schema = "sales")]
public sealed class SalesShiftRow
{
    public long ShiftId { get; set; }
    public long StationId { get; set; }
    public long OpenedBy { get; set; }
    public long? ClosedBy { get; set; }
    public string Status { get; set; } = "OPEN";
    public decimal OpeningFloat { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    // variance عمود مولّد في V005 (GENERATED ALWAYS) — لا يُدرج ولا يُحدَّث من EF
    public string? VarianceReason { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

[Table("drawer_events", Schema = "sales")]
public sealed class DrawerEventRow
{
    public long EventId { get; set; }
    public long StationId { get; set; }
    public long? ShiftId { get; set; }
    public string EventType { get; set; } = "MANUAL_OPEN";
    public string? Reason { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string ResultStatus { get; set; } = "UNKNOWN";
    public long TriggeredBy { get; set; }
}

// ═══════════════ G7: print.* (V007) — التوجيه والقوالب والمهام (§29.4/§29.9) ════

[Table("printer_profiles", Schema = "print")]
public sealed class PrinterProfileRow
{
    public long PrinterProfileId { get; set; }
    public required string PrinterId { get; set; }
    public required string WindowsQueueName { get; set; }
    public required string DeviceModel { get; set; }
    public required string DriverName { get; set; }
    public long? StationId { get; set; }
    public long? BranchId { get; set; }
    public string MediaClass { get; set; } = "ROLL_80";
    public decimal MediaWidthMm { get; set; }
    public decimal PrintableWidthMm { get; set; }
    public decimal? VerifiedMediaWidthMm { get; set; }
    public int DpiX { get; set; } = 203;
    public int DpiY { get; set; } = 203;
    public decimal LeftMarginMm { get; set; }
    public decimal RightMarginMm { get; set; }
    public string? RollMode { get; set; }
    public decimal? MaxContinuousLengthMm { get; set; }
    public string AdapterKind { get; set; } = "ESC_POS";
    public bool IsCalibrated { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public string? LastTestResult { get; set; }
    public long? LastTestedBy { get; set; }
    public int ProfileVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("printer_document_roles", Schema = "print")]
public sealed class PrinterDocumentRoleRow
{
    public long RoleAssignmentId { get; set; }
    public long PrinterProfileId { get; set; }
    public long StationId { get; set; }
    public string DocumentRole { get; set; } = "CUSTOMER_RECEIPT";
    public bool IsAutoRoutingEnabled { get; set; }
}

[Table("document_templates", Schema = "print")]
public sealed class DocumentTemplateRow
{
    public long TemplateId { get; set; }
    public required string TemplateCode { get; set; }
    public required string DocumentType { get; set; }
    public string MediaClass { get; set; } = "ROLL_80";
    public int Version { get; set; } = 1;
    public required string BodyDefinition { get; set; }   // JSONB عبر تحويل النص
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("print_jobs", Schema = "print")]
public sealed class PrintJobRow
{
    public long JobId { get; set; }
    public long DocumentId { get; set; }
    public string DocumentType { get; set; } = "CUSTOMER_RECEIPT";
    public long TemplateId { get; set; }
    public int TemplateVersion { get; set; }
    public long PrinterProfileId { get; set; }
    public int PrinterProfileVersion { get; set; }
    public string MediaClass { get; set; } = "ROLL_80";
    public decimal PrintableWidthMm { get; set; }
    public int Dpi { get; set; }
    public string CopyType { get; set; } = "CUSTOMER";
    public string Status { get; set; } = "QUEUED";
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? FailureReason { get; set; }
    public required string IdempotencyKey { get; set; }
    public long RequestedBy { get; set; }
    public string? CorrelationId { get; set; }
    public long? RelatedReprintOf { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
