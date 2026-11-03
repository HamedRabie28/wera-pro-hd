namespace WeraPro.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WeraPro.Infrastructure.Persistence.Entities;

// ═══════════════════════════════════════════════════════════════════════════
// تهيئات كيانات G6 — أسماء أعمدة snake_case مطابقة حرفياً لـV003/V004/V005/
// V006/V017 (§2.6: لا تسمية ضمنية).
// ═══════════════════════════════════════════════════════════════════════════

public sealed class ApprovalRowConfiguration : IEntityTypeConfiguration<ApprovalRow>
{
    public void Configure(EntityTypeBuilder<ApprovalRow> builder)
    {
        builder.ToTable("approvals", "core");
        builder.HasKey(x => x.ApprovalId);
        builder.Property(x => x.ApprovalId).HasColumnName("approval_id").ValueGeneratedOnAdd();
        builder.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(64);
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by");
        builder.Property(x => x.DecidedBy).HasColumnName("decided_by");
        builder.Property(x => x.Decision).HasColumnName("decision").HasMaxLength(16);
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.RequestedAt).HasColumnName("requested_at");
        builder.Property(x => x.DecidedAt).HasColumnName("decided_at");
    }
}

public sealed class StockBalanceRowConfiguration : IEntityTypeConfiguration<StockBalanceRow>
{
    public void Configure(EntityTypeBuilder<StockBalanceRow> builder)
    {
        builder.ToTable("stock_balances", "inv");
        builder.HasKey(x => x.BalanceId);
        builder.Property(x => x.BalanceId).HasColumnName("balance_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.BaseQty).HasColumnName("base_qty").HasPrecision(18, 6);
        builder.Property(x => x.ReservedQty).HasColumnName("reserved_qty").HasPrecision(18, 6);
        builder.Property(x => x.LastMovementAt).HasColumnName("last_movement_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class LotRowConfiguration : IEntityTypeConfiguration<LotRow>
{
    public void Configure(EntityTypeBuilder<LotRow> builder)
    {
        builder.ToTable("lots", "inv");
        builder.HasKey(x => x.LotId);
        builder.Property(x => x.LotId).HasColumnName("lot_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.LotCode).HasColumnName("lot_code").HasMaxLength(64);
        builder.Property(x => x.ExpiryDate).HasColumnName("expiry_date");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at");
    }
}

public sealed class StockReservationRowConfiguration : IEntityTypeConfiguration<StockReservationRow>
{
    public void Configure(EntityTypeBuilder<StockReservationRow> builder)
    {
        builder.ToTable("stock_reservations", "inv");
        builder.HasKey(x => x.ReservationId);
        builder.Property(x => x.ReservationId).HasColumnName("reservation_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.QtyBase).HasColumnName("qty_base").HasPrecision(18, 6);
        builder.Property(x => x.SourceDocType).HasColumnName("source_doc_type").HasMaxLength(64);
        builder.Property(x => x.SourceDocId).HasColumnName("source_doc_id");
        builder.Property(x => x.ReservedAt).HasColumnName("reserved_at");
        builder.Property(x => x.ReleasedAt).HasColumnName("released_at");
    }
}

public sealed class CountSessionRowConfiguration : IEntityTypeConfiguration<CountSessionRow>
{
    public void Configure(EntityTypeBuilder<CountSessionRow> builder)
    {
        builder.ToTable("count_sessions", "inv");
        builder.HasKey(x => x.CountId);
        builder.Property(x => x.CountId).HasColumnName("count_id").ValueGeneratedOnAdd();
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.CountedBy).HasColumnName("counted_by");
        builder.Property(x => x.ApprovedBy).HasColumnName("approved_by");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at");
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at");
        builder.Property(x => x.Notes).HasColumnName("notes");
    }
}

public sealed class CountLineRowConfiguration : IEntityTypeConfiguration<CountLineRow>
{
    public void Configure(EntityTypeBuilder<CountLineRow> builder)
    {
        builder.ToTable("count_lines", "inv");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.CountId).HasColumnName("count_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.SystemQty).HasColumnName("system_qty").HasPrecision(18, 6);
        builder.Property(x => x.CountedQty).HasColumnName("counted_qty").HasPrecision(18, 6);
    }
}

public sealed class StockAdjustmentRowConfiguration : IEntityTypeConfiguration<StockAdjustmentRow>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentRow> builder)
    {
        builder.ToTable("stock_adjustments", "inv");
        builder.HasKey(x => x.AdjustmentId);
        builder.Property(x => x.AdjustmentId).HasColumnName("adjustment_id").ValueGeneratedOnAdd();
        builder.Property(x => x.CountId).HasColumnName("count_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.NewQty).HasColumnName("new_qty").HasPrecision(18, 6);
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.ApprovalId).HasColumnName("approval_id");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.AdjustmentKind).HasColumnName("adjustment_kind").HasMaxLength(16);
    }
}

public sealed class TransferOrderRowConfiguration : IEntityTypeConfiguration<TransferOrderRow>
{
    public void Configure(EntityTypeBuilder<TransferOrderRow> builder)
    {
        builder.ToTable("transfer_orders", "inv");
        builder.HasKey(x => x.TransferId);
        builder.Property(x => x.TransferId).HasColumnName("transfer_id").ValueGeneratedOnAdd();
        builder.Property(x => x.FromWarehouseId).HasColumnName("from_warehouse_id");
        builder.Property(x => x.ToWarehouseId).HasColumnName("to_warehouse_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.SentAt).HasColumnName("sent_at");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

public sealed class TransferLineRowConfiguration : IEntityTypeConfiguration<TransferLineRow>
{
    public void Configure(EntityTypeBuilder<TransferLineRow> builder)
    {
        builder.ToTable("transfer_lines", "inv");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.TransferId).HasColumnName("transfer_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.QtyBase).HasColumnName("qty_base").HasPrecision(18, 6);
    }
}

public sealed class SupplierRowConfiguration : IEntityTypeConfiguration<SupplierRow>
{
    public void Configure(EntityTypeBuilder<SupplierRow> builder)
    {
        builder.ToTable("suppliers", "purch");
        builder.HasKey(x => x.SupplierId);
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.NameEn).HasColumnName("name_en");
        builder.Property(x => x.Phone).HasColumnName("phone");
        builder.Property(x => x.Email).HasColumnName("email");
        builder.Property(x => x.TaxId).HasColumnName("tax_id");
        builder.Property(x => x.PaymentTermsDays).HasColumnName("payment_terms_days");
        builder.Property(x => x.CreditLimit).HasColumnName("credit_limit").HasPrecision(19, 4);
        builder.Property(x => x.AccountStatus).HasColumnName("account_status").HasMaxLength(16);
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class PurchaseOrderRowConfiguration : IEntityTypeConfiguration<PurchaseOrderRow>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderRow> builder)
    {
        builder.ToTable("purchase_orders", "purch");
        builder.HasKey(x => x.PoId);
        builder.Property(x => x.PoId).HasColumnName("po_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PoNumber).HasColumnName("po_number").HasMaxLength(32);
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.ExpectedDate).HasColumnName("expected_date");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at");
    }
}

public sealed class PurchaseOrderLineRowConfiguration : IEntityTypeConfiguration<PurchaseOrderLineRow>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLineRow> builder)
    {
        builder.ToTable("purchase_order_lines", "purch");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PoId).HasColumnName("po_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.Qty).HasColumnName("qty").HasPrecision(18, 6);
        builder.Property(x => x.UomId).HasColumnName("uom_id");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(19, 4);
    }
}

public sealed class PurchaseReceiptRowConfiguration : IEntityTypeConfiguration<PurchaseReceiptRow>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptRow> builder)
    {
        builder.ToTable("purchase_receipts", "purch");
        builder.HasKey(x => x.ReceiptId);
        builder.Property(x => x.ReceiptId).HasColumnName("receipt_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ReceiptNumber).HasColumnName("receipt_number").HasMaxLength(32);
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id");
        builder.Property(x => x.PoId).HasColumnName("po_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.ReceivedBy).HasColumnName("received_by");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
    }
}

public sealed class PurchaseReceiptLineRowConfiguration : IEntityTypeConfiguration<PurchaseReceiptLineRow>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptLineRow> builder)
    {
        builder.ToTable("purchase_receipt_lines", "purch");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ReceiptId).HasColumnName("receipt_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.Qty).HasColumnName("qty").HasPrecision(18, 6);
        builder.Property(x => x.UomId).HasColumnName("uom_id");
        builder.Property(x => x.ConversionFactor).HasColumnName("conversion_factor").HasPrecision(18, 6);
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(19, 4);
        builder.Property(x => x.LotId).HasColumnName("lot_id");
        builder.Property(x => x.LotCode).HasColumnName("lot_code").HasMaxLength(64);
        builder.Property(x => x.LotExpiry).HasColumnName("lot_expiry");
        builder.Property(x => x.BaseQty).HasColumnName("base_qty").HasPrecision(18, 6);
        builder.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(19, 4);
    }
}

public sealed class SupplierInvoiceRowConfiguration : IEntityTypeConfiguration<SupplierInvoiceRow>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceRow> builder)
    {
        builder.ToTable("supplier_invoices", "purch");
        builder.HasKey(x => x.InvoiceId);
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id").ValueGeneratedOnAdd();
        builder.Property(x => x.InvoiceNumber).HasColumnName("invoice_number").HasMaxLength(64);
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id");
        builder.Property(x => x.ReceiptId).HasColumnName("receipt_id");
        builder.Property(x => x.Subtotal).HasColumnName("subtotal").HasPrecision(19, 4);
        builder.Property(x => x.TaxTotal).HasColumnName("tax_total").HasPrecision(19, 4);
        builder.Property(x => x.GrandTotal).HasColumnName("grand_total").HasPrecision(19, 4);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.InvoiceDate).HasColumnName("invoice_date");
        builder.Property(x => x.DueDate).HasColumnName("due_date");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
    }
}

public sealed class SupplierInvoiceLineRowConfiguration : IEntityTypeConfiguration<SupplierInvoiceLineRow>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceLineRow> builder)
    {
        builder.ToTable("supplier_invoice_lines", "purch");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.Qty).HasColumnName("qty").HasPrecision(18, 6);
        builder.Property(x => x.UomId).HasColumnName("uom_id");
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(19, 4);
        builder.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(19, 4);
    }
}

public sealed class SupplierPaymentRowConfiguration : IEntityTypeConfiguration<SupplierPaymentRow>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentRow> builder)
    {
        builder.ToTable("supplier_payments", "purch");
        builder.HasKey(x => x.PaymentId);
        builder.Property(x => x.PaymentId).HasColumnName("payment_id").ValueGeneratedOnAdd();
        builder.Property(x => x.VoucherNumber).HasColumnName("voucher_number").HasMaxLength(32);
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id");
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.PaymentMethod).HasColumnName("payment_method").HasMaxLength(16);
        builder.Property(x => x.PaymentDate).HasColumnName("payment_date");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
    }
}

public sealed class SupplierPaymentAllocationRowConfiguration : IEntityTypeConfiguration<SupplierPaymentAllocationRow>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentAllocationRow> builder)
    {
        builder.ToTable("supplier_payment_allocations", "purch");
        builder.HasKey(x => x.AllocationId);
        builder.Property(x => x.AllocationId).HasColumnName("allocation_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PaymentId).HasColumnName("payment_id");
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
    }
}

public sealed class SupplierReturnRowConfiguration : IEntityTypeConfiguration<SupplierReturnRow>
{
    public void Configure(EntityTypeBuilder<SupplierReturnRow> builder)
    {
        builder.ToTable("supplier_returns", "purch");
        builder.HasKey(x => x.ReturnId);
        builder.Property(x => x.ReturnId).HasColumnName("return_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ReturnNumber).HasColumnName("return_number").HasMaxLength(32);
        builder.Property(x => x.SupplierId).HasColumnName("supplier_id");
        builder.Property(x => x.ReceiptId).HasColumnName("receipt_id");
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.RefundMethod).HasColumnName("refund_method").HasMaxLength(16);
        builder.Property(x => x.Total).HasColumnName("total").HasPrecision(19, 4);
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
    }
}

public sealed class SupplierReturnLineRowConfiguration : IEntityTypeConfiguration<SupplierReturnLineRow>
{
    public void Configure(EntityTypeBuilder<SupplierReturnLineRow> builder)
    {
        builder.ToTable("supplier_return_lines", "purch");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ReturnId).HasColumnName("return_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.Qty).HasColumnName("qty").HasPrecision(18, 6);
        builder.Property(x => x.UomId).HasColumnName("uom_id");
        builder.Property(x => x.ConversionFactor).HasColumnName("conversion_factor").HasPrecision(18, 6);
        builder.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(19, 4);
        builder.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(19, 4);
        builder.Property(x => x.TargetSkuId).HasColumnName("target_sku_id");
        builder.Property(x => x.BaseQty).HasColumnName("base_qty").HasPrecision(18, 6);
    }
}

public sealed class TreasuryAccountRowConfiguration : IEntityTypeConfiguration<TreasuryAccountRow>
{
    public void Configure(EntityTypeBuilder<TreasuryAccountRow> builder)
    {
        builder.ToTable("treasury_accounts", "finance");
        builder.HasKey(x => x.TreasuryAccountId);
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.AccountKind).HasColumnName("account_kind").HasMaxLength(20);
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(8);
        builder.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public sealed class TreasuryTxnRowConfiguration : IEntityTypeConfiguration<TreasuryTxnRow>
{
    public void Configure(EntityTypeBuilder<TreasuryTxnRow> builder)
    {
        builder.ToTable("treasury_transactions", "finance");
        builder.HasKey(x => x.TxnId);
        builder.Property(x => x.TxnId).HasColumnName("txn_id").ValueGeneratedOnAdd();
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id");
        builder.Property(x => x.Direction).HasColumnName("direction");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.SourceDocType).HasColumnName("source_doc_type").HasMaxLength(64);
        builder.Property(x => x.SourceDocId).HasColumnName("source_doc_id");
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
    }
}

public sealed class CostLayerRowConfiguration : IEntityTypeConfiguration<CostLayerRow>
{
    public void Configure(EntityTypeBuilder<CostLayerRow> builder)
    {
        builder.ToTable("cost_layers", "finance");
        builder.HasKey(x => x.LayerId);
        builder.Property(x => x.LayerId).HasColumnName("layer_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.CostingMethod).HasColumnName("costing_method").HasMaxLength(8);
        builder.Property(x => x.AvgUnitCost).HasColumnName("avg_unit_cost").HasPrecision(19, 4);
        builder.Property(x => x.QtyOnLayer).HasColumnName("qty_on_layer").HasPrecision(18, 6);
        builder.Property(x => x.LastReceiptAt).HasColumnName("last_receipt_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class CustomerRowConfiguration : IEntityTypeConfiguration<CustomerRow>
{
    public void Configure(EntityTypeBuilder<CustomerRow> builder)
    {
        builder.ToTable("customers", "sales");
        builder.HasKey(x => x.CustomerId);
        builder.Property(x => x.CustomerId).HasColumnName("customer_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.Phone).HasColumnName("phone");
        builder.Property(x => x.Email).HasColumnName("email");
        builder.Property(x => x.TaxId).HasColumnName("tax_id");
        builder.Property(x => x.CreditLimit).HasColumnName("credit_limit").HasPrecision(19, 4);
        builder.Property(x => x.CreditEnabled).HasColumnName("credit_enabled");
        builder.Property(x => x.PaymentTermsDays).HasColumnName("payment_terms_days");
        builder.Property(x => x.AccountStatus).HasColumnName("account_status").HasMaxLength(16);
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
