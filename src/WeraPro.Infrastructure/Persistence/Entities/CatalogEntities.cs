namespace WeraPro.Infrastructure.Persistence.Entities;

using System.ComponentModel.DataAnnotations.Schema;

/// <summary>كيانات الكتالوج (G4) — صور مباشرة لجداول V002 (أسماء snake_case مطابقة حرفياً).</summary>

[Table("categories", Schema = "catalog")]
public sealed class CategoryRow
{
    public long CategoryId { get; set; }
    public long? ParentId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? NameEn { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("brands", Schema = "catalog")]
public sealed class BrandRow
{
    public long BrandId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? NameEn { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("units", Schema = "catalog")]
public sealed class UnitRow
{
    public long UomId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    /// <summary>COUNT/MASS/VOLUME/LENGTH.</summary>
    public required string Dimension { get; set; }
    public bool IsBase { get; set; }
    public short Decimals { get; set; }
}

[Table("unit_conversions", Schema = "catalog")]
public sealed class UnitConversionRow
{
    public long ConversionId { get; set; }
    public long FromUomId { get; set; }
    public long ToUomId { get; set; }
    public decimal Factor { get; set; }
    public bool IsFixed { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("products", Schema = "catalog")]
public sealed class ProductRow
{
    public long ProductId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? NameEn { get; set; }
    public long CategoryId { get; set; }
    public long? BrandId { get; set; }
    public string? Description { get; set; }
    /// <summary>ACTIVE/INACTIVE/DISCONTINUED.</summary>
    public string Status { get; set; } = "ACTIVE";
    public bool TrackLots { get; set; }
    public bool TrackSerials { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>تنقّل قراءة فقط — للتركيب المركّب (Include).</summary>
    public CategoryRow? Category { get; set; }
    public BrandRow? Brand { get; set; }
}

[Table("skus", Schema = "catalog")]
public sealed class SkuRow
{
    public long SkuId { get; set; }
    public long ProductId { get; set; }
    public required string SkuCode { get; set; }
    /// <summary>COUNT/WEIGHT/PACK/SERIALIZED — صريح دائماً (§4.2).</summary>
    public required string SaleMode { get; set; }
    public long BaseUomId { get; set; }
    public long SaleUomId { get; set; }
    /// <summary>اسم صيغة البيع للعرض (كيس/علبة 12 كيساً) — V015؛ الهوية في sku_code.</summary>
    public string? SkuNameAr { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public int? WeightPrecisionG { get; set; }
    public int? WeightStepG { get; set; }
    public bool AllowManualWeight { get; set; } = true;
    public bool AllowAmountTarget { get; set; } = true;
    public decimal? MinWeightKg { get; set; }
    public decimal? MaxWeightKg { get; set; }
    public decimal DefaultTareKg { get; set; }
    public decimal ReorderLevel { get; set; }
    public bool? AllowNegative { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("packages", Schema = "catalog")]
public sealed class PackageRow
{
    public long PackageId { get; set; }
    public long PackageSkuId { get; set; }
    public long ContentSkuId { get; set; }
    public decimal UnitsPerPackage { get; set; }
    public bool OpenPackageAllowed { get; set; } = true;
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
}

[Table("barcodes", Schema = "catalog")]
public sealed class BarcodeRow
{
    public long BarcodeId { get; set; }
    public long SkuId { get; set; }
    public required string BarcodeValue { get; set; }
    public required string BarcodeNormalized { get; set; }
    public required string BarcodeType { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public bool IsPrintedLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("plus", Schema = "catalog")]
public sealed class PluRow
{
    public long PluId { get; set; }
    public required string PluCode { get; set; }
    public long SkuId { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("price_lists", Schema = "catalog")]
public sealed class PriceListRow
{
    public long PriceListId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string Currency { get; set; } = "EGP";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

[Table("product_prices", Schema = "catalog")]
public sealed class ProductPriceRow
{
    public long PriceId { get; set; }
    public long SkuId { get; set; }
    public long PriceListId { get; set; }
    public long UnitId { get; set; }
    public decimal UnitPrice { get; set; }
    public string TaxTreatment { get; set; } = "TAXABLE";
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ── المخزون: الدفتر Append-only والمخازن (V003) — ترحيل حركات G4 ────────────

[Table("warehouses", Schema = "inv")]
public sealed class WarehouseRow
{
    public long WarehouseId { get; set; }
    public long BranchId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string WhType { get; set; } = "BRANCH";
    public bool IsActive { get; set; } = true;
}

[Table("stock_movements", Schema = "inv")]
public sealed class StockMovementRow
{
    public long MovementId { get; set; }
    public long SkuId { get; set; }
    public long WarehouseId { get; set; }
    public required string MovementType { get; set; }
    public short Direction { get; set; }
    public decimal BaseQty { get; set; }
    public long SourceUomId { get; set; }
    public decimal ConversionFactor { get; set; }
    public required string SourceDocType { get; set; }
    public long SourceDocId { get; set; }
    public string? SourceLineRef { get; set; }
    public long? LotId { get; set; }
    public long? ApprovalId { get; set; }
    public string? Reason { get; set; }
    public long BranchId { get; set; }
    public long UserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
