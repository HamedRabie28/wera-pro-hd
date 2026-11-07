using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WeraPro.Infrastructure.Persistence.Entities;

namespace WeraPro.Infrastructure.Persistence.Configurations;

// ═══════════════ G8: تهيئات finance.* (V006/V019) — snake_case مطابقة حرفياً ═══

public sealed class FinanceVoucherRowConfiguration : IEntityTypeConfiguration<FinanceVoucherRow>
{
    public void Configure(EntityTypeBuilder<FinanceVoucherRow> builder)
    {
        builder.ToTable("vouchers", "finance");
        builder.HasKey(x => x.VoucherId);
        builder.Property(x => x.VoucherId).HasColumnName("voucher_id").ValueGeneratedOnAdd();
        builder.Property(x => x.VoucherNumber).HasColumnName("voucher_number").HasMaxLength(32);
        builder.Property(x => x.VoucherType).HasColumnName("voucher_type").HasMaxLength(16);
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id");
        builder.Property(x => x.CounterTreasuryAccountId).HasColumnName("counter_treasury_account_id");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.CounterpartyType).HasColumnName("counterparty_type").HasMaxLength(16);
        builder.Property(x => x.CounterpartyId).HasColumnName("counterparty_id");
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ShiftId).HasColumnName("shift_id");
    }
}

public sealed class VoucherAllocationRowConfiguration : IEntityTypeConfiguration<VoucherAllocationRow>
{
    public void Configure(EntityTypeBuilder<VoucherAllocationRow> builder)
    {
        builder.ToTable("voucher_allocations", "finance");
        builder.HasKey(x => x.AllocationId);
        builder.Property(x => x.AllocationId).HasColumnName("allocation_id").ValueGeneratedOnAdd();
        builder.Property(x => x.VoucherId).HasColumnName("voucher_id");
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(x => new { x.VoucherId, x.InvoiceId }).IsUnique();
        builder.HasIndex(x => x.InvoiceId);
    }
}

public sealed class ExpenseCategoryRowConfiguration : IEntityTypeConfiguration<ExpenseCategoryRow>
{
    public void Configure(EntityTypeBuilder<ExpenseCategoryRow> builder)
    {
        builder.ToTable("expense_categories", "finance");
        builder.HasKey(x => x.CategoryId);
        builder.Property(x => x.CategoryId).HasColumnName("category_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.ParentId).HasColumnName("parent_id");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public sealed class ExpenseRowConfiguration : IEntityTypeConfiguration<ExpenseRow>
{
    public void Configure(EntityTypeBuilder<ExpenseRow> builder)
    {
        builder.ToTable("expenses", "finance");
        builder.HasKey(x => x.ExpenseId);
        builder.Property(x => x.ExpenseId).HasColumnName("expense_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ExpenseNumber).HasColumnName("expense_number").HasMaxLength(32);
        builder.Property(x => x.CategoryId).HasColumnName("category_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.ExpenseDate).HasColumnName("expense_date");
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasPrecision(19, 4);
        builder.Property(x => x.PaymentMethod).HasColumnName("payment_method").HasMaxLength(16);
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id");
        builder.Property(x => x.Beneficiary).HasColumnName("beneficiary");
        builder.Property(x => x.CostCenter).HasColumnName("cost_center");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.ApprovalId).HasColumnName("approval_id");
        builder.Property(x => x.AttachmentMediaId).HasColumnName("attachment_media_id");
        builder.Property(x => x.Notes).HasColumnName("notes");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ShiftId).HasColumnName("shift_id");
    }
}

public sealed class CapitalEventRowConfiguration : IEntityTypeConfiguration<CapitalEventRow>
{
    public void Configure(EntityTypeBuilder<CapitalEventRow> builder)
    {
        builder.ToTable("capital_events", "finance");
        builder.HasKey(x => x.CapitalId);
        builder.Property(x => x.CapitalId).HasColumnName("capital_id").ValueGeneratedOnAdd();
        builder.Property(x => x.EventNumber).HasColumnName("event_number").HasMaxLength(32);
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(24);
        builder.Property(x => x.Amount).HasColumnName("amount").HasPrecision(19, 4);
        builder.Property(x => x.TreasuryAccountId).HasColumnName("treasury_account_id");
        builder.Property(x => x.EventDate).HasColumnName("event_date");
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.ApprovalId).HasColumnName("approval_id");
        builder.Property(x => x.PostedAt).HasColumnName("posted_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

public sealed class ShiftCashCountRowConfiguration : IEntityTypeConfiguration<ShiftCashCountRow>
{
    public void Configure(EntityTypeBuilder<ShiftCashCountRow> builder)
    {
        builder.ToTable("shift_cash_counts", "finance");
        builder.HasKey(x => x.CountId);
        builder.Property(x => x.CountId).HasColumnName("count_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ShiftId).HasColumnName("shift_id");
        builder.Property(x => x.CountedCash).HasColumnName("counted_cash").HasPrecision(19, 4);
        builder.Property(x => x.CountedBy).HasColumnName("counted_by");
        builder.Property(x => x.CountedAt).HasColumnName("counted_at");
        builder.Property(x => x.Notes).HasColumnName("notes");
    }
}
