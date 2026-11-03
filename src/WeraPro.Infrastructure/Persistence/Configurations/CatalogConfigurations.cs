namespace WeraPro.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>تهيئة كيانات الكتالوج (G4) — أسماء snake_case مطابقة لـV002/V003/V015 حرفياً.</summary>

public sealed class CategoryRowConfiguration : IEntityTypeConfiguration<CategoryRow>
{
    public void Configure(EntityTypeBuilder<CategoryRow> builder)
    {
        builder.ToTable("categories", "catalog");
        builder.HasKey(x => x.CategoryId);
        builder.Property(x => x.CategoryId).HasColumnName("category_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ParentId).HasColumnName("parent_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.NameEn).HasColumnName("name_en");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class BrandRowConfiguration : IEntityTypeConfiguration<BrandRow>
{
    public void Configure(EntityTypeBuilder<BrandRow> builder)
    {
        builder.ToTable("brands", "catalog");
        builder.HasKey(x => x.BrandId);
        builder.Property(x => x.BrandId).HasColumnName("brand_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.NameEn).HasColumnName("name_en");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class UnitRowConfiguration : IEntityTypeConfiguration<UnitRow>
{
    public void Configure(EntityTypeBuilder<UnitRow> builder)
    {
        builder.ToTable("units", "catalog");
        builder.HasKey(x => x.UomId);
        builder.Property(x => x.UomId).HasColumnName("uom_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(16);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.Dimension).HasColumnName("dimension").HasMaxLength(10);
        builder.Property(x => x.IsBase).HasColumnName("is_base");
        builder.Property(x => x.Decimals).HasColumnName("decimals");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class UnitConversionRowConfiguration : IEntityTypeConfiguration<UnitConversionRow>
{
    public void Configure(EntityTypeBuilder<UnitConversionRow> builder)
    {
        builder.ToTable("unit_conversions", "catalog");
        builder.HasKey(x => x.ConversionId);
        builder.Property(x => x.ConversionId).HasColumnName("conversion_id").ValueGeneratedOnAdd();
        builder.Property(x => x.FromUomId).HasColumnName("from_uom_id");
        builder.Property(x => x.ToUomId).HasColumnName("to_uom_id");
        builder.Property(x => x.Factor).HasColumnName("factor").HasColumnType("numeric(18,6)");
        builder.Property(x => x.IsFixed).HasColumnName("is_fixed");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasOne<UnitRow>().WithMany().HasForeignKey(x => x.FromUomId);
        builder.HasOne<UnitRow>().WithMany().HasForeignKey(x => x.ToUomId);
    }
}

public sealed class ProductRowConfiguration : IEntityTypeConfiguration<ProductRow>
{
    public void Configure(EntityTypeBuilder<ProductRow> builder)
    {
        builder.ToTable("products", "catalog");
        builder.HasKey(x => x.ProductId);
        builder.Property(x => x.ProductId).HasColumnName("product_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.NameEn).HasColumnName("name_en");
        builder.Property(x => x.CategoryId).HasColumnName("category_id");
        builder.Property(x => x.BrandId).HasColumnName("brand_id");
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.TrackLots).HasColumnName("track_lots");
        builder.Property(x => x.TrackSerials).HasColumnName("track_serials");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);
        builder.HasOne(x => x.Brand).WithMany().HasForeignKey(x => x.BrandId);
    }
}

public sealed class SkuRowConfiguration : IEntityTypeConfiguration<SkuRow>
{
    public void Configure(EntityTypeBuilder<SkuRow> builder)
    {
        builder.ToTable("skus", "catalog");
        builder.HasKey(x => x.SkuId);
        builder.Property(x => x.SkuId).HasColumnName("sku_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ProductId).HasColumnName("product_id");
        builder.Property(x => x.SkuCode).HasColumnName("sku_code").HasMaxLength(48);
        builder.Property(x => x.SaleMode).HasColumnName("sale_mode").HasMaxLength(12);
        builder.Property(x => x.BaseUomId).HasColumnName("base_uom_id");
        builder.Property(x => x.SaleUomId).HasColumnName("sale_uom_id");
        builder.Property(x => x.SkuNameAr).HasColumnName("sku_name_ar");   // V015
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(12);
        builder.Property(x => x.WeightPrecisionG).HasColumnName("weight_precision_g");
        builder.Property(x => x.WeightStepG).HasColumnName("weight_step_g");
        builder.Property(x => x.AllowManualWeight).HasColumnName("allow_manual_weight");
        builder.Property(x => x.AllowAmountTarget).HasColumnName("allow_amount_target");
        builder.Property(x => x.MinWeightKg).HasColumnName("min_weight_kg").HasColumnType("numeric(14,3)");
        builder.Property(x => x.MaxWeightKg).HasColumnName("max_weight_kg").HasColumnType("numeric(14,3)");
        builder.Property(x => x.DefaultTareKg).HasColumnName("default_tare_kg").HasColumnType("numeric(14,3)");
        builder.Property(x => x.ReorderLevel).HasColumnName("reorder_level").HasColumnType("numeric(18,6)");
        builder.Property(x => x.AllowNegative).HasColumnName("allow_negative");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.SkuCode).IsUnique();
        builder.HasIndex(x => x.ProductId);
        builder.HasOne<ProductRow>().WithMany().HasForeignKey(x => x.ProductId);
        builder.HasOne<UnitRow>().WithMany().HasForeignKey(x => x.BaseUomId);
        builder.HasOne<UnitRow>().WithMany().HasForeignKey(x => x.SaleUomId);
    }
}

public sealed class PackageRowConfiguration : IEntityTypeConfiguration<PackageRow>
{
    public void Configure(EntityTypeBuilder<PackageRow> builder)
    {
        builder.ToTable("packages", "catalog");
        builder.HasKey(x => x.PackageId);
        builder.Property(x => x.PackageId).HasColumnName("package_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PackageSkuId).HasColumnName("package_sku_id");
        builder.Property(x => x.ContentSkuId).HasColumnName("content_sku_id");
        builder.Property(x => x.UnitsPerPackage).HasColumnName("units_per_package").HasColumnType("numeric(18,6)");
        builder.Property(x => x.OpenPackageAllowed).HasColumnName("open_package_allowed");
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from");
        builder.Property(x => x.ValidTo).HasColumnName("valid_to");
        builder.HasIndex(x => x.PackageSkuId).IsUnique();
        builder.HasIndex(x => x.ContentSkuId);
        builder.HasOne<SkuRow>().WithMany().HasForeignKey(x => x.PackageSkuId);
        builder.HasOne<SkuRow>().WithMany().HasForeignKey(x => x.ContentSkuId);
    }
}

public sealed class BarcodeRowConfiguration : IEntityTypeConfiguration<BarcodeRow>
{
    public void Configure(EntityTypeBuilder<BarcodeRow> builder)
    {
        builder.ToTable("barcodes", "catalog");
        builder.HasKey(x => x.BarcodeId);
        builder.Property(x => x.BarcodeId).HasColumnName("barcode_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.BarcodeValue).HasColumnName("barcode_value").HasMaxLength(64);
        builder.Property(x => x.BarcodeNormalized).HasColumnName("barcode_normalized").HasMaxLength(64);
        builder.Property(x => x.BarcodeType).HasColumnName("barcode_type").HasMaxLength(12);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(12);
        builder.Property(x => x.IsPrintedLabel).HasColumnName("is_printed_label");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(x => x.SkuId);
        builder.HasOne<SkuRow>().WithMany().HasForeignKey(x => x.SkuId);
        // الفهرس الفريد الجزئي (النشط فقط) يفرضه SQL — التفرد الكامل على المطبع الحرفي سيمنع
        // إعادة استخدام قيمة بعد إحالتها للتقاعد، لذا لا يُعلن فهرس EF فريد هنا.
    }
}

public sealed class PluRowConfiguration : IEntityTypeConfiguration<PluRow>
{
    public void Configure(EntityTypeBuilder<PluRow> builder)
    {
        builder.ToTable("plus", "catalog");
        builder.HasKey(x => x.PluId);
        builder.Property(x => x.PluId).HasColumnName("plu_id").ValueGeneratedOnAdd();
        builder.Property(x => x.PluCode).HasColumnName("plu_code").HasMaxLength(8);
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.HasIndex(x => x.PluCode).IsUnique();
        builder.HasIndex(x => x.SkuId);
        builder.HasOne<SkuRow>().WithMany().HasForeignKey(x => x.SkuId);
    }
}

public sealed class PriceListRowConfiguration : IEntityTypeConfiguration<PriceListRow>
{
    public void Configure(EntityTypeBuilder<PriceListRow> builder)
    {
        builder.ToTable("price_lists", "catalog");
        builder.HasKey(x => x.PriceListId);
        builder.Property(x => x.PriceListId).HasColumnName("price_list_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(8);
        builder.Property(x => x.IsDefault).HasColumnName("is_default");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class ProductPriceRowConfiguration : IEntityTypeConfiguration<ProductPriceRow>
{
    public void Configure(EntityTypeBuilder<ProductPriceRow> builder)
    {
        builder.ToTable("product_prices", "catalog");
        builder.HasKey(x => x.PriceId);
        builder.Property(x => x.PriceId).HasColumnName("price_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.PriceListId).HasColumnName("price_list_id");
        builder.Property(x => x.UnitId).HasColumnName("unit_id");
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(19,4)");
        builder.Property(x => x.TaxTreatment).HasColumnName("tax_treatment").HasMaxLength(16);
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from");
        builder.Property(x => x.ValidTo).HasColumnName("valid_to");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(x => new { x.SkuId, x.PriceListId, x.ValidFrom }).IsUnique();
        builder.HasOne<SkuRow>().WithMany().HasForeignKey(x => x.SkuId);
        builder.HasOne<PriceListRow>().WithMany().HasForeignKey(x => x.PriceListId);
        builder.HasOne<UnitRow>().WithMany().HasForeignKey(x => x.UnitId);
    }
}

public sealed class WarehouseRowConfiguration : IEntityTypeConfiguration<WarehouseRow>
{
    public void Configure(EntityTypeBuilder<WarehouseRow> builder)
    {
        builder.ToTable("warehouses", "inv");
        builder.HasKey(x => x.WarehouseId);
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id").ValueGeneratedOnAdd();
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.WhType).HasColumnName("wh_type").HasMaxLength(12);
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class StockMovementRowConfiguration : IEntityTypeConfiguration<StockMovementRow>
{
    public void Configure(EntityTypeBuilder<StockMovementRow> builder)
    {
        builder.ToTable("stock_movements", "inv");
        builder.HasKey(x => x.MovementId);
        builder.Property(x => x.MovementId).HasColumnName("movement_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(x => x.MovementType).HasColumnName("movement_type").HasMaxLength(20);
        builder.Property(x => x.Direction).HasColumnName("direction");
        builder.Property(x => x.BaseQty).HasColumnName("base_qty").HasColumnType("numeric(18,6)");
        builder.Property(x => x.SourceUomId).HasColumnName("source_uom_id");
        builder.Property(x => x.ConversionFactor).HasColumnName("conversion_factor").HasColumnType("numeric(18,6)");
        builder.Property(x => x.SourceDocType).HasColumnName("source_doc_type").HasMaxLength(40);
        builder.Property(x => x.SourceDocId).HasColumnName("source_doc_id");
        builder.Property(x => x.SourceLineRef).HasColumnName("source_line_ref").HasMaxLength(80);
        builder.Property(x => x.LotId).HasColumnName("lot_id");
        builder.Property(x => x.ApprovalId).HasColumnName("approval_id");
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(400);
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        builder.HasIndex(x => new { x.SkuId, x.WarehouseId, x.OccurredAt });
        builder.HasIndex(x => new { x.SourceDocType, x.SourceDocId });
        builder.HasOne<SkuRow>().WithMany().HasForeignKey(x => x.SkuId);
        builder.HasOne<WarehouseRow>().WithMany().HasForeignKey(x => x.WarehouseId);
        // Append-only: لا تعديل ولا حذف عبر EF — يفرضه مشغل القاعدة (trg_stock_movement_immutable)
        builder.ToTable(t => t.HasTrigger("trg_stock_movement_immutable"));
    }
}
