namespace WeraPro.Application.Catalog;

using WeraPro.Domain.Catalog;
using WeraPro.Domain.SharedKernel;

/// <summary>
/// مستودع الوحدات والتحويلات (catalog.units + catalog.unit_conversions) —
/// يعيد المخطط الجاهز المفحوص (أبعاد/دورات/تعارض مسارات).
/// </summary>
public interface IUnitCatalog
{
    Task<Result<UnitConversionGraph>> LoadGraphAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UnitDefinition>> ListUnitsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ConversionDefinition>> ListConversionsAsync(CancellationToken ct = default);
}

/// <summary>صنف الكتالوج للعرض (لا يحمل سلوك).</summary>
public sealed record CatalogCategory(long CategoryId, string Code, string NameAr, bool IsActive);

public sealed record CatalogBrand(long BrandId, string Code, string NameAr, bool IsActive);

/// <summary>قائمة سعرية (catalog.price_lists).</summary>
public sealed record PriceListInfo(long PriceListId, string Code, string NameAr, string Currency, bool IsDefault);

/// <summary>نتيجة إنشاء منتج — الهوية المولدة لكل قسم (تُعاد للعميل كاملة).</summary>
public sealed record CreatedProduct(
    long ProductId,
    IReadOnlyList<(long SkuId, string SkuCode)> Skus,
    IReadOnlyList<long> BarcodeIds,
    IReadOnlyList<long> PluIds,
    IReadOnlyList<long> PriceIds,
    IReadOnlyList<long> PackageIds);

/// <summary>
/// مستودع الكتالوج — كل الكتابات داخل معاملة واحدة (لا حفظ جزئي §4.4):
/// إنشاء المنتج بصيغه وباركوده وPLU وأسعاره وعبواته كلها أو لا شيء.
/// </summary>
public interface ICatalogRepository
{
    /// <summary>الوجود المسبق ضمن نطاق التفرد (§3.1): كود منتج/SKU/باركود نشط/PLU.</summary>
    Task<bool> ProductCodeExistsAsync(string code, CancellationToken ct = default);
    Task<bool> SkuCodeExistsAsync(string skuCode, CancellationToken ct = default);
    Task<bool> ActiveBarcodeExistsAsync(string barcodeNormalized, CancellationToken ct = default);
    Task<bool> PluCodeExistsAsync(string pluCode, CancellationToken ct = default);

    Task<IReadOnlyList<CatalogCategory>> ListCategoriesAsync(bool activeOnly, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogBrand>> ListBrandsAsync(bool activeOnly, CancellationToken ct = default);
    Task<IReadOnlyList<PriceListInfo>> ListPriceListsAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>إنشاء منتج كامل بمعاملة واحدة — يرمي على أي فشل فتُلغى المعاملة كلها.</summary>
    Task<CreatedProduct> CreateProductAsync(CreateProductPlan plan, CancellationToken ct = default);

    /// <summary>قراءة منتج كامل بصيغه وباركوده وأسعاره (null إن لم يوجد).</summary>
    Task<Contracts.V1.ProductDto?> FindProductAsync(long productId, CancellationToken ct = default);

    /// <summary>البحث النصي المحدود بترقيم خادمي (server-side دائماً).</summary>
    Task<(IReadOnlyList<Contracts.V1.ProductDto> Items, long TotalCount)> SearchProductsAsync(
        string? query, string? saleMode, string? status, int page, int pageSize, CancellationToken ct = default);

    /// <summary>مطابقة دقيقة: باركود منمّط نشط → SKU (§3.1 Exact Match).</summary>
    Task<Contracts.V1.CatalogLookupResponse?> FindByBarcodeAsync(string barcodeNormalized, CancellationToken ct = default);

    /// <summary>مطابقة دقيقة: كود SKU.</summary>
    Task<Contracts.V1.CatalogLookupResponse?> FindBySkuCodeAsync(string skuCode, CancellationToken ct = default);

    /// <summary>مطابقة دقيقة: كود PLU نشط.</summary>
    Task<Contracts.V1.CatalogLookupResponse?> FindByPluAsync(string pluCode, CancellationToken ct = default);

    /// <summary>تغيير حالة منتج (ACTIVE/INACTIVE/DISCONTINUED — لا حذف تاريخي).</summary>
    Task<Result<long>> SetProductStatusAsync(long productId, string status, CancellationToken ct = default);

    /// <summary>بيانات صيغة البيع كما يحتاجها حاسب الكميات.</summary>
    Task<SkuSaleInfo?> FindSkuSaleInfoAsync(long skuId, CancellationToken ct = default);

    /// <summary>تعريف العبوة الساري لصيغة PACK (valid_to IS NULL أو مستقبلي).</summary>
    Task<PackageInfo?> FindActivePackageAsync(long packageSkuId, CancellationToken ct = default);

    /// <summary>وجود مخزن (الحركة توثق بمخزن حقيقي).</summary>
    Task<bool> WarehouseExistsAsync(long warehouseId, CancellationToken ct = default);

    /// <summary>فرع المخزن — null إذا المخزن غير موجود (الحركة تُوثق بفرع مخزنها).</summary>
    Task<long?> FindWarehouseBranchAsync(long warehouseId, CancellationToken ct = default);

    /// <summary>ترحيل صف دفتر واحد (Append-only) بلقطة التحويل المحسوبة.</summary>
    Task<long> PostStockMovementAsync(StockMovementDraft draft, CancellationToken ct = default);
}

/// <summary>خطة إنشاء منتج مكتملة التحقق — تُنفذ ذرياً في المستودع.</summary>
public sealed record CreateProductPlan(
    string Code,
    string NameAr,
    string? NameEn,
    long CategoryId,
    long? BrandId,
    string? Description,
    bool TrackLots,
    bool TrackSerials,
    IReadOnlyList<SkuPlan> Skus,
    IReadOnlyList<BarcodePlan> Barcodes,
    IReadOnlyList<PluPlan> Plus,
    IReadOnlyList<PricePlan> Prices,
    IReadOnlyList<PackagePlan> Packages);

public sealed record SkuPlan(
    string SkuCode,
    string SaleMode,
    long BaseUomId,
    long SaleUomId,
    string? NameAr,
    int? WeightPrecisionG,
    int? WeightStepG,
    bool AllowManualWeight,
    bool AllowAmountTarget,
    decimal? MinWeightKg,
    decimal? MaxWeightKg,
    decimal DefaultTareKg,
    decimal ReorderLevel,
    bool? AllowNegative);

public sealed record BarcodePlan(string BarcodeValue, string BarcodeNormalized, string BarcodeType, bool IsPrintedLabel, int SkuOrdinal);

public sealed record PluPlan(string PluCode, int SkuOrdinal);

public sealed record PricePlan(long SkuOrdinal, long PriceListId, long UnitId, decimal UnitPrice, string TaxTreatment);

public sealed record PackagePlan(int PackageSkuOrdinal, int ContentSkuOrdinal, decimal UnitsPerPackage, bool OpenPackageAllowed);

/// <summary>مسودة صف دفتر محسوبة — التحويل انتهى؛ هذا تنفيذ موثق بمصدره.</summary>
public sealed record StockMovementDraft(
    long SkuId,
    long WarehouseId,
    string MovementType,
    int Direction,
    decimal BaseQty,
    long SourceUomId,
    decimal ConversionFactor,
    string SourceDocType,
    long SourceDocId,
    string? SourceLineRef,
    string? Reason,
    long BranchId,
    long UserId);

/// <summary>
/// خرق سياسة قاعدة حاسم عند ترحيل حركة (مثل D-03 رصيد سالب بلا اعتماد مشرف) —
/// رسالة عربية جاهزة للعرض برمز 409 لا خطأ خادم 500.
/// </summary>
public sealed class StockPolicyViolationException(string userMessageAr) : Exception(userMessageAr)
{
    public string UserMessageAr { get; } = userMessageAr;
}
