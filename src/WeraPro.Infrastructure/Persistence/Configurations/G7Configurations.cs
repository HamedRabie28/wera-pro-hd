using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WeraPro.Infrastructure.Persistence.Entities;

namespace WeraPro.Infrastructure.Persistence.Configurations;

// ═══════ G7: sales.* (V005) + print.* (V007) — تهيئة مطابقة حرفياً snake_case ═════

public sealed class SalesInvoiceRowConfiguration : IEntityTypeConfiguration<SalesInvoiceRow>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceRow> builder)
    {
        builder.ToTable("invoices", "sales");
        builder.HasKey(x => x.InvoiceId);
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id").ValueGeneratedOnAdd();
        builder.Property(x => x.InvoiceNumber).HasColumnName("invoice_number").HasMaxLength(64);
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.ShiftId).HasColumnName("shift_id");
        builder.Property(x => x.CashierUserId).HasColumnName("cashier_user_id");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(8);
        builder.Property(x => x.TaxModeSnapshot).HasColumnName("tax_mode_snapshot").HasMaxLength(24);
        builder.Property(x => x.GrossTotal).HasColumnName("gross_total").HasPrecision(19, 4);
        builder.Property(x => x.LineDiscountTotal).HasColumnName("line_discount_total").HasPrecision(19, 4);
        builder.Property(x => x.InvoiceDiscount).HasColumnName("invoice_discount").HasPrecision(19, 4);
        builder.Property(x => x.TaxTotal).HasColumnName("tax_total").HasPrecision(19, 4);
        builder.Property(x => x.NetTotal).HasColumnName("net_total").HasPrecision(19, 4);
        builder.Property(x => x.PaidTotal).HasColumnName("paid_total").HasPrecision(19, 4);
        builder.Property(x => x.ReturnedTotal).HasColumnName("returned_total").HasPrecision(19, 4);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.VoidedAt).HasColumnName("voided_at");
        builder.Property(x => x.VoidReason).HasColumnName("void_reason");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.IdempotencyKey).HasDatabaseName("uq_invoices_idem").IsUnique().HasFilter("idempotency_key IS NOT NULL");
    }
}

public sealed class SalesInvoiceLineRowConfiguration : IEntityTypeConfiguration<SalesInvoiceLineRow>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLineRow> builder)
    {
        builder.ToTable("invoice_lines", "sales");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.LineNo).HasColumnName("line_no");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.SaleMode).HasColumnName("sale_mode").HasMaxLength(16);
        builder.Property(x => x.PackageId).HasColumnName("package_id");
        builder.Property(x => x.Qty).HasColumnName("qty").HasPrecision(18, 6);
        builder.Property(x => x.UomId).HasColumnName("uom_id");
        builder.Property(x => x.ConversionFactor).HasColumnName("conversion_factor").HasPrecision(18, 6);
        builder.Property(x => x.WeightKg).HasColumnName("weight_kg").HasPrecision(14, 3);
        builder.Property(x => x.WeightSource).HasColumnName("weight_source").HasMaxLength(24);
        builder.Property(x => x.AmountTarget).HasColumnName("amount_target").HasPrecision(19, 4);
        builder.Property(x => x.TareKg).HasColumnName("tare_kg").HasPrecision(14, 3);
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(19, 4);
        builder.Property(x => x.PriceListId).HasColumnName("price_list_id");
        builder.Property(x => x.TaxTreatment).HasColumnName("tax_treatment").HasMaxLength(16);
        builder.Property(x => x.TaxRateSnapshot).HasColumnName("tax_rate_snapshot").HasPrecision(7, 4);
        builder.Property(x => x.LineDiscount).HasColumnName("line_discount").HasPrecision(19, 4);
        builder.Property(x => x.InvoiceDiscountShare).HasColumnName("invoice_discount_share").HasPrecision(19, 4);
        builder.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasPrecision(19, 4);
        builder.Property(x => x.NetAmount).HasColumnName("net_amount").HasPrecision(19, 4);
        builder.Property(x => x.CostSnapshot).HasColumnName("cost_snapshot").HasPrecision(19, 4);
        builder.Property(x => x.CostMethodSnapshot).HasColumnName("cost_method_snapshot").HasMaxLength(8);
        builder.Property(x => x.SerialId).HasColumnName("serial_id");
        builder.Property(x => x.Notes).HasColumnName("notes");
    }
}

public sealed class SalesPaymentRowConfiguration : IEntityTypeConfiguration<SalesPaymentRow>
{
    public void Configure(EntityTypeBuilder<SalesPaymentRow> builder)
    {
        builder.ToTable("payments", "sales");
        builder.HasKey(x => x.PaymentId);
        builder.Property(x => x.PaymentId).HasColumnName("payment_id").ValueGeneratedOnAdd();
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.PaymentMethod).HasColumnName("payment_method").HasMaxLength(16);
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.ProviderName).HasColumnName("provider_name").HasMaxLength(64);
        builder.Property(x => x.ProviderReference).HasColumnName("provider_reference").HasMaxLength(128);
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at");
        builder.Property(x => x.SettledAt).HasColumnName("settled_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
    }
}

public sealed class SalesRefundRowConfiguration : IEntityTypeConfiguration<SalesRefundRow>
{
    public void Configure(EntityTypeBuilder<SalesRefundRow> builder)
    {
        builder.ToTable("refunds", "sales");
        builder.HasKey(x => x.RefundId);
        builder.Property(x => x.RefundId).HasColumnName("refund_id").ValueGeneratedOnAdd();
        builder.Property(x => x.RefundNumber).HasColumnName("refund_number").HasMaxLength(64);
        builder.Property(x => x.OriginalInvoiceId).HasColumnName("original_invoice_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.ApprovalId).HasColumnName("approval_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.RefundMethod).HasColumnName("refund_method").HasMaxLength(24);
        builder.Property(x => x.Restock).HasColumnName("restock");
        builder.Property(x => x.Total).HasColumnName("total").HasPrecision(19, 4);
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.HasIndex(x => x.IdempotencyKey).HasDatabaseName("uq_refunds_idempotency").IsUnique().HasFilter("idempotency_key IS NOT NULL");
    }
}

public sealed class SalesRefundLineRowConfiguration : IEntityTypeConfiguration<SalesRefundLineRow>
{
    public void Configure(EntityTypeBuilder<SalesRefundLineRow> builder)
    {
        builder.ToTable("refund_lines", "sales");
        builder.HasKey(x => x.LineId);
        builder.Property(x => x.LineId).HasColumnName("line_id").ValueGeneratedOnAdd();
        builder.Property(x => x.RefundId).HasColumnName("refund_id");
        builder.Property(x => x.OriginalLineId).HasColumnName("original_line_id");
        builder.Property(x => x.Qty).HasColumnName("qty").HasPrecision(18, 6);
        builder.Property(x => x.UnitRefund).HasColumnName("unit_refund").HasPrecision(19, 4);
        builder.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(19, 4);
    }
}

public sealed class HeldCartRowConfiguration : IEntityTypeConfiguration<HeldCartRow>
{
    public void Configure(EntityTypeBuilder<HeldCartRow> builder)
    {
        builder.ToTable("held_carts", "sales");
        builder.HasKey(x => x.CartId);
        builder.Property(x => x.CartId).HasColumnName("cart_id").ValueGeneratedOnAdd();
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.HeldBy).HasColumnName("held_by");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");
        builder.Property(x => x.CartSnapshot).HasColumnName("cart_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.NoteAr).HasColumnName("note_ar");
        builder.Property(x => x.HeldAt).HasColumnName("held_at");
        builder.Property(x => x.RestoredAt).HasColumnName("restored_at");
        builder.Property(x => x.RestoredBy).HasColumnName("restored_by");
    }
}

public sealed class SalesShiftRowConfiguration : IEntityTypeConfiguration<SalesShiftRow>
{
    public void Configure(EntityTypeBuilder<SalesShiftRow> builder)
    {
        builder.ToTable("shifts", "sales");
        builder.HasKey(x => x.ShiftId);
        builder.Property(x => x.ShiftId).HasColumnName("shift_id").ValueGeneratedOnAdd();
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.OpenedBy).HasColumnName("opened_by");
        builder.Property(x => x.ClosedBy).HasColumnName("closed_by");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(8);
        builder.Property(x => x.OpeningFloat).HasColumnName("opening_float").HasPrecision(19, 4);
        builder.Property(x => x.ExpectedCash).HasColumnName("expected_cash").HasPrecision(19, 4);
        builder.Property(x => x.CountedCash).HasColumnName("counted_cash").HasPrecision(19, 4);
        builder.Property(x => x.VarianceReason).HasColumnName("variance_reason");
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at");
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at");
        builder.Property(x => x.VarianceSettledByVoucherId).HasColumnName("variance_settled_by_voucher_id");
    }
}

public sealed class DrawerEventRowConfiguration : IEntityTypeConfiguration<DrawerEventRow>
{
    public void Configure(EntityTypeBuilder<DrawerEventRow> builder)
    {
        builder.ToTable("drawer_events", "sales");
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedOnAdd();
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.ShiftId).HasColumnName("shift_id");
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(24);
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.RequestedAt).HasColumnName("requested_at");
        builder.Property(x => x.RespondedAt).HasColumnName("responded_at");
        builder.Property(x => x.ResultStatus).HasColumnName("result_status").HasMaxLength(16);
        builder.Property(x => x.TriggeredBy).HasColumnName("triggered_by");
    }
}

public sealed class PrinterProfileRowConfiguration : IEntityTypeConfiguration<PrinterProfileRow>
{
    public void Configure(EntityTypeBuilder<PrinterProfileRow> builder)
    {
        builder.ToTable("printer_profiles", "print");
        builder.HasKey(x => x.PrinterProfileId);
        builder.Property(x => x.PrinterProfileId).HasColumnName("printer_profile_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PrinterId).HasColumnName("printer_id").HasMaxLength(128);
        builder.Property(x => x.WindowsQueueName).HasColumnName("windows_queue_name").HasMaxLength(256);
        builder.Property(x => x.DeviceModel).HasColumnName("device_model").HasMaxLength(128);
        builder.Property(x => x.DriverName).HasColumnName("driver_name").HasMaxLength(128);
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.MediaClass).HasColumnName("media_class").HasMaxLength(16);
        builder.Property(x => x.MediaWidthMm).HasColumnName("media_width_mm").HasPrecision(6, 2);
        builder.Property(x => x.PrintableWidthMm).HasColumnName("printable_width_mm").HasPrecision(6, 2);
        builder.Property(x => x.VerifiedMediaWidthMm).HasColumnName("verified_media_width_mm").HasPrecision(6, 2);
        builder.Property(x => x.DpiX).HasColumnName("dpi_x");
        builder.Property(x => x.DpiY).HasColumnName("dpi_y");
        builder.Property(x => x.LeftMarginMm).HasColumnName("left_margin_mm").HasPrecision(6, 2);
        builder.Property(x => x.RightMarginMm).HasColumnName("right_margin_mm").HasPrecision(6, 2);
        builder.Property(x => x.RollMode).HasColumnName("roll_mode").HasMaxLength(16);
        builder.Property(x => x.MaxContinuousLengthMm).HasColumnName("max_continuous_length_mm").HasPrecision(10, 2);
        builder.Property(x => x.AdapterKind).HasColumnName("adapter_kind").HasMaxLength(24);
        builder.Property(x => x.IsCalibrated).HasColumnName("is_calibrated");
        builder.Property(x => x.LastTestAt).HasColumnName("last_test_at");
        builder.Property(x => x.LastTestResult).HasColumnName("last_test_result").HasMaxLength(8);
        builder.Property(x => x.LastTestedBy).HasColumnName("last_tested_by");
        builder.Property(x => x.ProfileVersion).HasColumnName("profile_version");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class PrinterDocumentRoleRowConfiguration : IEntityTypeConfiguration<PrinterDocumentRoleRow>
{
    public void Configure(EntityTypeBuilder<PrinterDocumentRoleRow> builder)
    {
        builder.ToTable("printer_document_roles", "print");
        builder.HasKey(x => x.RoleAssignmentId);
        builder.Property(x => x.RoleAssignmentId).HasColumnName("role_assignment_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PrinterProfileId).HasColumnName("printer_profile_id");
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.DocumentRole).HasColumnName("document_role").HasMaxLength(32);
        builder.Property(x => x.IsAutoRoutingEnabled).HasColumnName("is_auto_routing_enabled");
    }
}

public sealed class DocumentTemplateRowConfiguration : IEntityTypeConfiguration<DocumentTemplateRow>
{
    public void Configure(EntityTypeBuilder<DocumentTemplateRow> builder)
    {
        builder.ToTable("document_templates", "print");
        builder.HasKey(x => x.TemplateId);
        builder.Property(x => x.TemplateId).HasColumnName("template_id").ValueGeneratedOnAdd();
        builder.Property(x => x.TemplateCode).HasColumnName("template_code").HasMaxLength(64);
        builder.Property(x => x.DocumentType).HasColumnName("document_type").HasMaxLength(48);
        builder.Property(x => x.MediaClass).HasColumnName("media_class").HasMaxLength(16);
        builder.Property(x => x.Version).HasColumnName("version");
        builder.Property(x => x.BodyDefinition).HasColumnName("body_definition").HasColumnType("jsonb");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

public sealed class PrintJobRowConfiguration : IEntityTypeConfiguration<PrintJobRow>
{
    public void Configure(EntityTypeBuilder<PrintJobRow> builder)
    {
        builder.ToTable("print_jobs", "print");
        builder.HasKey(x => x.JobId);
        builder.Property(x => x.JobId).HasColumnName("job_id").ValueGeneratedOnAdd();
        builder.Property(x => x.DocumentId).HasColumnName("document_id");
        builder.Property(x => x.DocumentType).HasColumnName("document_type").HasMaxLength(48);
        builder.Property(x => x.TemplateId).HasColumnName("template_id");
        builder.Property(x => x.TemplateVersion).HasColumnName("template_version");
        builder.Property(x => x.PrinterProfileId).HasColumnName("printer_profile_id");
        builder.Property(x => x.PrinterProfileVersion).HasColumnName("printer_profile_version");
        builder.Property(x => x.MediaClass).HasColumnName("media_class").HasMaxLength(16);
        builder.Property(x => x.PrintableWidthMm).HasColumnName("printable_width_mm").HasPrecision(6, 2);
        builder.Property(x => x.Dpi).HasColumnName("dpi");
        builder.Property(x => x.CopyType).HasColumnName("copy_type").HasMaxLength(24);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(32);
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        builder.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by");
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
        builder.Property(x => x.RelatedReprintOf).HasColumnName("related_reprint_of");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("print_jobs_idempotency_key_key");
    }
}
