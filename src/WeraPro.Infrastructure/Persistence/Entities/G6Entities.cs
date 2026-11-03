namespace WeraPro.Infrastructure.Persistence.Entities;

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

// ═══════════════════════════════════════════════════════════════════════════
// كيانات G6 (V003/V004/V005/V006/V017): المخزون والمشتريات والأطراف
// والاعتمادات والخزينة والتكلفة — أسماء snake_case مطابقة حرفياً للقاعدة (§12).
// ═══════════════════════════════════════════════════════════════════════════

// ── core: الاعتمادات (D-03) ──────────────────────────────────────────────────

[Table("approvals", Schema = "core")]
public sealed class ApprovalRow
{
    public long ApprovalId { get; set; }
    public required string EntityType { get; set; }
    public long EntityId { get; set; }
    public long RequestedBy { get; set; }
    public long? DecidedBy { get; set; }
    public string Decision { get; set; } = "PENDING";
    public string? Reason { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

// ── inv: الجرد والتسويات والتحويلات والحجز والدفعات والأرصدة ────────────────

[Table("stock_balances", Schema = "inv")]
public sealed class StockBalanceRow
{
    public long BalanceId { get; set; }
    public long SkuId { get; set; }
    public long WarehouseId { get; set; }
    public decimal BaseQty { get; set; }
    public decimal ReservedQty { get; set; }
    public DateTimeOffset? LastMovementAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("lots", Schema = "inv")]
public sealed class LotRow
{
    public long LotId { get; set; }
    public long SkuId { get; set; }
    public required string LotCode { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

[Table("stock_reservations", Schema = "inv")]
public sealed class StockReservationRow
{
    public long ReservationId { get; set; }
    public long SkuId { get; set; }
    public long WarehouseId { get; set; }
    public decimal QtyBase { get; set; }
    public required string SourceDocType { get; set; }
    public long SourceDocId { get; set; }
    public DateTimeOffset ReservedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

[Table("count_sessions", Schema = "inv")]
public sealed class CountSessionRow
{
    public long CountId { get; set; }
    public long WarehouseId { get; set; }
    public string Status { get; set; } = "OPEN";
    public long CountedBy { get; set; }
    public long? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? Notes { get; set; }
}

[Table("count_lines", Schema = "inv")]
public sealed class CountLineRow
{
    public long LineId { get; set; }
    public long CountId { get; set; }
    public long SkuId { get; set; }
    public decimal SystemQty { get; set; }
    public decimal CountedQty { get; set; }
}

[Table("stock_adjustments", Schema = "inv")]
public sealed class StockAdjustmentRow
{
    public long AdjustmentId { get; set; }
    public long? CountId { get; set; }
    public long WarehouseId { get; set; }
    public long SkuId { get; set; }
    public decimal NewQty { get; set; }
    public required string Reason { get; set; }
    public long ApprovalId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string AdjustmentKind { get; set; } = "QUICK";
}

[Table("transfer_orders", Schema = "inv")]
public sealed class TransferOrderRow
{
    public long TransferId { get; set; }
    public long FromWarehouseId { get; set; }
    public long ToWarehouseId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("transfer_lines", Schema = "inv")]
public sealed class TransferLineRow
{
    public long LineId { get; set; }
    public long TransferId { get; set; }
    public long SkuId { get; set; }
    public decimal QtyBase { get; set; }
}

// ── purch: الموردون وأوامر الشراء والاستلامات والفواتير والسداد والمرتجعات ──

[Table("suppliers", Schema = "purch")]
public sealed class SupplierRow
{
    public long SupplierId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? NameEn { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public int PaymentTermsDays { get; set; }
    public decimal? CreditLimit { get; set; }
    public string AccountStatus { get; set; } = "ACTIVE";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("purchase_orders", Schema = "purch")]
public sealed class PurchaseOrderRow
{
    public long PoId { get; set; }
    public required string PoNumber { get; set; }
    public long SupplierId { get; set; }
    public long WarehouseId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public DateOnly? ExpectedDate { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

[Table("purchase_order_lines", Schema = "purch")]
public sealed class PurchaseOrderLineRow
{
    public long LineId { get; set; }
    public long PoId { get; set; }
    public long SkuId { get; set; }
    public decimal Qty { get; set; }
    public long UomId { get; set; }
    public decimal UnitCost { get; set; }
}

[Table("purchase_receipts", Schema = "purch")]
public sealed class PurchaseReceiptRow
{
    public long ReceiptId { get; set; }
    public required string ReceiptNumber { get; set; }
    public long SupplierId { get; set; }
    public long? PoId { get; set; }
    public long WarehouseId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public long ReceivedBy { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public string? Notes { get; set; }
    public string? IdempotencyKey { get; set; }
}

[Table("purchase_receipt_lines", Schema = "purch")]
public sealed class PurchaseReceiptLineRow
{
    public long LineId { get; set; }
    public long ReceiptId { get; set; }
    public long SkuId { get; set; }
    public decimal Qty { get; set; }
    public long UomId { get; set; }
    public decimal ConversionFactor { get; set; }
    public decimal UnitCost { get; set; }
    public long? LotId { get; set; }
    public string? LotCode { get; set; }          // محسوبة عند الترحيل في G6 (مخزنة على السطر للواجهة)
    public DateOnly? LotExpiry { get; set; }
    public decimal BaseQty { get; set; }          // لقطة محسوبة عند الإنشاء (qty × factor)
    public decimal LineTotal { get; set; }
}

[Table("supplier_invoices", Schema = "purch")]
public sealed class SupplierInvoiceRow
{
    public long InvoiceId { get; set; }
    public required string InvoiceNumber { get; set; }
    public long SupplierId { get; set; }
    public long? ReceiptId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public string Status { get; set; } = "DRAFT";
    public DateOnly InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
}

[Table("supplier_invoice_lines", Schema = "purch")]
public sealed class SupplierInvoiceLineRow
{
    public long LineId { get; set; }
    public long InvoiceId { get; set; }
    public long SkuId { get; set; }
    public decimal Qty { get; set; }
    public long UomId { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}

[Table("supplier_payments", Schema = "purch")]
public sealed class SupplierPaymentRow
{
    public long PaymentId { get; set; }
    public required string VoucherNumber { get; set; }
    public long SupplierId { get; set; }
    public long TreasuryAccountId { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public DateOnly PaymentDate { get; set; }
    public string Status { get; set; } = "DRAFT";
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }
    public string? IdempotencyKey { get; set; }
}

[Table("supplier_payment_allocations", Schema = "purch")]
public sealed class SupplierPaymentAllocationRow
{
    public long AllocationId { get; set; }
    public long PaymentId { get; set; }
    public long InvoiceId { get; set; }
    public decimal Amount { get; set; }
}

[Table("supplier_returns", Schema = "purch")]
public sealed class SupplierReturnRow
{
    public long ReturnId { get; set; }
    public required string ReturnNumber { get; set; }
    public long SupplierId { get; set; }
    public long? ReceiptId { get; set; }
    public long? InvoiceId { get; set; }
    public long WarehouseId { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string? RefundMethod { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
}

[Table("supplier_return_lines", Schema = "purch")]
public sealed class SupplierReturnLineRow
{
    public long LineId { get; set; }
    public long ReturnId { get; set; }
    public long SkuId { get; set; }
    public decimal Qty { get; set; }
    public long UomId { get; set; }
    public decimal ConversionFactor { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
    public long TargetSkuId { get; set; }          // صنف المحتوى عند PACK — الأثر في الدفتر
    public decimal BaseQty { get; set; }
}

// ── finance: الخزينة والتكلفة (V006) ────────────────────────────────────────

[Table("treasury_accounts", Schema = "finance")]
public sealed class TreasuryAccountRow
{
    public long TreasuryAccountId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string AccountKind { get; set; } = "SAFE";
    public long? BranchId { get; set; }
    public string Currency { get; set; } = "EGP";
    public bool IsActive { get; set; } = true;
}

[Table("treasury_transactions", Schema = "finance")]
public sealed class TreasuryTxnRow
{
    public long TxnId { get; set; }
    public long TreasuryAccountId { get; set; }
    public short Direction { get; set; }
    public decimal Amount { get; set; }
    public required string SourceDocType { get; set; }
    public long SourceDocId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long CreatedBy { get; set; }
    public long BranchId { get; set; }
}

[Table("cost_layers", Schema = "finance")]
public sealed class CostLayerRow
{
    public long LayerId { get; set; }
    public long SkuId { get; set; }
    public long WarehouseId { get; set; }
    public string CostingMethod { get; set; } = "WAC";
    public decimal AvgUnitCost { get; set; }
    public decimal QtyOnLayer { get; set; }
    public DateTimeOffset? LastReceiptAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ── sales: العملاء (V005) ───────────────────────────────────────────────────

[Table("customers", Schema = "sales")]
public sealed class CustomerRow
{
    public long CustomerId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public decimal CreditLimit { get; set; }
    public bool CreditEnabled { get; set; }
    public int PaymentTermsDays { get; set; }
    public string AccountStatus { get; set; } = "ACTIVE";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
