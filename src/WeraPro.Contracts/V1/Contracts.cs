namespace WeraPro.Contracts.V1;

/// <summary>عقد v1 — مسار URL الثابت /api/v1 (تغييره كسر للعقد ويستلزم v2).</summary>
public static class ApiRoutes
{
    public const string Prefix = "api/v1";

    public const string Login = Prefix + "/auth/login";
    public const string Refresh = Prefix + "/auth/refresh";
    public const string Logout = Prefix + "/auth/logout";
    public const string Me = Prefix + "/auth/me";
    public const string Branches = Prefix + "/branches";
    public const string Stations = Prefix + "/branches/{branchId:long}/stations";
    public const string Settings = Prefix + "/settings";
    public const string CatalogUnits = Prefix + "/catalog/units";
    public const string CatalogReference = Prefix + "/catalog/reference";
    public const string CatalogProducts = Prefix + "/catalog/products";
    public const string CatalogProductById = Prefix + "/catalog/products/{productId:long}";
    public const string CatalogProductStatus = Prefix + "/catalog/products/{productId:long}/status";
    public const string CatalogLookup = Prefix + "/catalog/lookup";
    public const string CatalogStockMovements = Prefix + "/catalog/skus/{skuId:long}/stock-movements";
    public const string SkuDefaultImage = Prefix + "/catalog/skus/{skuId:long}/default-image";
    public const string Media = Prefix + "/media";
    public const string MediaImportJobs = Prefix + "/media/import-jobs";
    // ── G6: المخزون والمشتريات والأطراف والاعتمادات ──
    public const string InventoryWarehouses = Prefix + "/inventory/warehouses";
    public const string InventoryBalances = Prefix + "/inventory/balances";
    public const string InventoryValuation = Prefix + "/inventory/valuation";
    public const string InventoryReservations = Prefix + "/inventory/reservations";
    public const string InventoryStocktakes = Prefix + "/inventory/stocktakes";
    public const string InventoryAdjustments = Prefix + "/inventory/adjustments";
    public const string InventoryTransfers = Prefix + "/inventory/transfers";
    public const string InventoryReorder = Prefix + "/inventory/reorder";
    public const string InventoryLots = Prefix + "/inventory/lots";
    public const string PurchasingSuppliers = Prefix + "/purchasing/suppliers";
    public const string PurchasingOrders = Prefix + "/purchasing/purchase-orders";
    public const string PurchasingReceipts = Prefix + "/purchasing/purchase-receipts";
    public const string PurchasingInvoices = Prefix + "/purchasing/supplier-invoices";
    public const string PurchasingPayments = Prefix + "/purchasing/supplier-payments";
    public const string PurchasingReturns = Prefix + "/purchasing/supplier-returns";
    public const string PurchasingTreasuryAccounts = Prefix + "/purchasing/treasury-accounts";
    public const string PartiesCustomers = Prefix + "/parties/customers";
    public const string Approvals = Prefix + "/approvals";
    public const string HealthLive = "health/live";
    public const string HealthReady = "health/ready";
}

// ── المصادقة ────────────────────────────────────────────────────────────────

public sealed record LoginRequest(string Username, string Password, long? BranchId, long? StationId);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, string RefreshToken, UserInfo User);

public sealed record RefreshRequest(string RefreshToken);

public sealed record RefreshResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, string RefreshToken);

public sealed record UserInfo(long UserId, string Username, string DisplayNameAr, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public sealed record UserScope(long? BranchId, string? BranchNameAr, long? StationId, string? StationNameAr);

// ── الفروع والمحطات ─────────────────────────────────────────────────────────

public sealed record BranchDto(long BranchId, string Code, string NameAr, string Timezone, bool IsActive);

public sealed record StationDto(long StationId, long BranchId, string Code, string NameAr, bool IsPos, bool IsActive);

// ── الإعدادات ───────────────────────────────────────────────────────────────

public sealed record SettingDto(
    string SettingKey,
    string ValueText,
    string ValueType,
    string Category,
    string? DecisionRef,
    string ConfirmationStatus,
    string DescriptionAr);

// ── الكتالوج (G4 — §4 المنتجات وصيغ البيع والعبوات والباركود) ────────────────

public sealed record UnitDto(long UomId, string Code, string NameAr, string Dimension, bool IsBase, int Decimals);

public sealed record UnitConversionDto(long FromUomId, long ToUomId, decimal Factor, bool IsFixed);

public sealed record UnitsResponse(IReadOnlyList<UnitDto> Units, IReadOnlyList<UnitConversionDto> Conversions);

// ── مرجع المحرر: فئات وعلامات وقوائم سعرية (G4 — تغذية محرر المنتج) ────────

public sealed record CategoryDto(long CategoryId, string Code, string NameAr, bool IsActive);

public sealed record BrandDto(long BrandId, string Code, string NameAr, bool IsActive);

public sealed record PriceListDto(long PriceListId, string Code, string NameAr, string Currency, bool IsDefault);

public sealed record CatalogReferenceResponse(
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<BrandDto> Brands,
    IReadOnlyList<PriceListDto> PriceLists);

/// <summary>إعدادات الوزن لصيغة WEIGHT (تُقبل فقط عند sale_mode=WEIGHT — §4.4 هـ).</summary>
public sealed record SkuWeightSettings(
    int? WeightPrecisionG,
    int? WeightStepG,
    bool AllowManualWeight,
    bool AllowAmountTarget,
    decimal? MinWeightKg,
    decimal? MaxWeightKg,
    decimal DefaultTareKg);

public sealed record SkuUpsertRequest(
    string SkuCode,
    string SaleMode,              // COUNT | WEIGHT | PACK | SERIALIZED — صريح دائماً
    long BaseUomId,
    long SaleUomId,
    string? NameAr,               // اسم الصيغة (كيس/علبة/كرتونة) — اختياري
    SkuWeightSettings? Weight,    // إلزامي عند WEIGHT، ممنوع عند غيرها
    decimal ReorderLevel,
    bool? AllowNegative);

public sealed record BarcodeUpsertRequest(string BarcodeValue, string BarcodeType, bool IsPrintedLabel, int SkuOrdinal);

public sealed record PluUpsertRequest(string PluCode, int SkuOrdinal);

public sealed record PriceUpsertRequest(
    int SkuOrdinal,                 // ترتيب الصيغة داخل الطلب (يفك الارتباط قبل توليد المفاتيح)
    long PriceListId,
    long UnitId,
    decimal UnitPrice,
    string TaxTreatment);         // TAXABLE | ZERO_RATED | EXEMPT

public sealed record PackageUpsertRequest(
    int PackageSkuOrdinal,          // ترتيب صيغة العبوة داخل الطلب
    int ContentSkuOrdinal,          // ترتيب صيغة المحتوى داخل الطلب
    decimal UnitsPerPackage,
    bool OpenPackageAllowed);

/// <summary>
/// إنشاء منتج متعدد الأقسام — معاملة واحدة: إما يُحفظ الكل أو لا شيء (لا حفظ جزئي).
/// المراجعة قبل الحفظ مسؤولية الواجهة (مراجعة المحرر)؛ الخادم يعيد بنية محفوظة كاملة.
/// </summary>
public sealed record CreateProductRequest(
    string Code,
    string NameAr,
    string? NameEn,
    long CategoryId,
    long? BrandId,
    string? Description,
    bool TrackLots,
    bool TrackSerials,
    IReadOnlyList<SkuUpsertRequest> Skus,
    IReadOnlyList<BarcodeUpsertRequest> Barcodes,   // مرتبطة بالترتيب مع Skus
    IReadOnlyList<PluUpsertRequest> Plus,
    IReadOnlyList<PriceUpsertRequest> Prices,
    IReadOnlyList<PackageUpsertRequest> Packages);

public sealed record BarcodeDto(long BarcodeId, long SkuId, string BarcodeValue, string BarcodeNormalized, string BarcodeType, string Status, bool IsPrintedLabel);

public sealed record PluDto(long PluId, long SkuId, string PluCode, bool IsActive);

public sealed record PriceDto(long PriceId, long SkuId, long PriceListId, string PriceListCode, long UnitId, decimal UnitPrice, string TaxTreatment);

public sealed record PackageDto(long PackageSkuId, long ContentSkuId, decimal UnitsPerPackage, bool OpenPackageAllowed);

public sealed record SkuDto(
    long SkuId,
    string SkuCode,
    string? NameAr,
    string SaleMode,
    long BaseUomId,
    string BaseUomCode,
    long SaleUomId,
    string SaleUomCode,
    string Status,
    SkuWeightSettings? Weight,
    decimal ReorderLevel,
    bool? AllowNegative,
    IReadOnlyList<BarcodeDto> Barcodes,
    IReadOnlyList<PluDto> Plus,
    IReadOnlyList<PriceDto> Prices);

public sealed record ProductDto(
    long ProductId,
    string Code,
    string NameAr,
    string? NameEn,
    long CategoryId,
    string CategoryNameAr,
    long? BrandId,
    string? BrandNameAr,
    string? Description,
    string Status,
    bool TrackLots,
    bool TrackSerials,
    IReadOnlyList<SkuDto> Skus,
    IReadOnlyList<PackageDto> Packages);

/// <summary>نتيجة البحث النصي المحدود — ترقيم من الخادم دائماً (§7.2.1: ملايين السجلات).</summary>
public sealed record CatalogSearchResponse(
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages,
    IReadOnlyList<ProductDto> Items);

/// <summary>نتيجة المطابقة الدقيقة بالباركود/SKU/PLU (Exact Match §3.1).</summary>
public sealed record CatalogLookupResponse(
    string MatchKind,             // BARCODE | SKU | PLU
    string MatchedValue,
    ProductDto Product,
    SkuDto Sku);

/// <summary>ترحيل حركة مخزون موثقة (إثبات تحويل المخزون G4 — الدفتر Append-only).</summary>
public sealed record PostStockMovementRequest(
    string MovementType,          // OPENING/RECEIVE/SALE/SALE_RETURN/ADJUSTMENT_IN/ADJUSTMENT_OUT...
    long WarehouseId,
    decimal Quantity,             // بوحدة المصدر (وزن للوزني — عدد للبقية)
    long SourceUomId,
    string SourceDocType,
    long SourceDocId,
    string? SourceLineRef,
    string? Reason,
    IReadOnlyList<string>? Serials);

public sealed record PostedStockMovementResponse(
    long MovementId,
    long SkuId,                   // SKU التي وقع عليها الأثر (المحتوى عند العبوة)
    decimal BaseQty,
    long SourceUomId,
    decimal ConversionFactor,
    string MovementType,
    int Direction);

public sealed record SetProductStatusRequest(string Status, string? ReasonAr);

// ── الأخطاء (RFC 7807) ──────────────────────────────────────────────────────

/// <summary>خطأ موحد على هيئة ProblemDetails — بلا أسرار ولا مسارات داخلية.</summary>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string? CorrelationId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string[]>? Errors = null);
