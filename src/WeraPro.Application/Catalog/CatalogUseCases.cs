namespace WeraPro.Application.Catalog;

using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Catalog;
using WeraPro.Domain.SharedKernel;

/// <summary>
/// إنشاء منتج متعدد الأقسام (§4.4): تحقق كامل من كل قسم قبل أي كتابة —
/// أبعاد الوحدات وحقول الوزن للوزني حصراً وأسعار موجبة وتفرد الأكواد وربط
/// العبوات — ثم تنفيذ واحد ذري: كل الأقسام أو لا شيء (لا حفظ جزئي).
/// المرجع: بوابة G4 «اكتمال تعريف المنتجات والـSKU وصيغ البيع والوحدات والعبوات
/// والتحويلات والأسعار والضرائب والباركود وPLU».
/// </summary>
public sealed partial class CreateProductUseCase
{
    private readonly ICatalogRepository _catalog;
    private readonly IUnitCatalog _units;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _clock;
    private readonly ILogger<CreateProductUseCase> _logger;

    public CreateProductUseCase(
        ICatalogRepository catalog,
        IUnitCatalog units,
        IAuditWriter audit,
        ICurrentUser user,
        TimeProvider clock,
        ILogger<CreateProductUseCase> logger)
    {
        _catalog = catalog;
        _units = units;
        _audit = audit;
        _user = user;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<ProductDto>> ExecuteAsync(CreateProductRequest request, string? correlationId, CancellationToken ct = default)
    {
        // ── 1) الفئات والقوائم السعرية موجودة ──
        var categories = await _catalog.ListCategoriesAsync(activeOnly: true, ct);
        if (categories.All(c => c.CategoryId != request.CategoryId))
        {
            return Result.Fail<ProductDto>(new("CATEGORY_NOT_FOUND", "الفئة غير موجودة أو غير نشطة"));
        }
        if (request.BrandId is { } brandId)
        {
            var brands = await _catalog.ListBrandsAsync(activeOnly: true, ct);
            if (brands.All(b => b.BrandId != brandId))
            {
                return Result.Fail<ProductDto>(new("BRAND_NOT_FOUND", "العلامة التجارية غير موجودة أو غير نشطة"));
            }
        }

        var priceLists = await _catalog.ListPriceListsAsync(activeOnly: true, ct);
        var priceListIds = priceLists.Select(p => p.PriceListId).ToHashSet();

        // ── 2) مخطط الوحدات المفحوص (أبعاد/دورات/تعارض) ──
        var graphResult = await _units.LoadGraphAsync(ct);
        if (!graphResult.IsSuccess)
        {
            return Result.Fail<ProductDto>(graphResult.Error!);
        }
        var graph = graphResult.Value!;

        // ── 3) الصيغ: sale_mode صريح + وحدات موجودة + الوزن للوزني حصراً ──
        if (request.Skus.Count == 0)
        {
            return Result.Fail<ProductDto>(new("SKUS_REQUIRED", "المنتج يحتاج صيغة بيع واحدة على الأقل"));
        }
        for (var i = 0; i < request.Skus.Count; i++)
        {
            var sku = request.Skus[i];

            SaleMode mode;
            try
            {
                mode = SaleModeCodes.FromCode(sku.SaleMode);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Result.Fail<ProductDto>(new("SALE_MODE_INVALID", $"صيغة البيع «{sku.SaleMode}» غير معروفة (COUNT/WEIGHT/PACK/SERIALIZED)"));
            }

            if (!graph.HasUnit(sku.BaseUomId) || !graph.HasUnit(sku.SaleUomId))
            {
                return Result.Fail<ProductDto>(new("UNIT_UNKNOWN", $"وحدات الصيغة «{sku.SkuCode}» غير معرفة"));
            }
            if (graph.Unit(sku.BaseUomId).Dimension != graph.Unit(sku.SaleUomId).Dimension)
            {
                return Result.Fail<ProductDto>(new(
                    "UNIT_DIMENSION_MISMATCH",
                    $"وحدة الأساس ووحدة البيع للصيغة «{sku.SkuCode}» من بُعدين مختلفين — غير جائز"));
            }
            if (mode == SaleMode.Weight
                && (graph.Unit(sku.BaseUomId).Dimension != UnitDimension.Mass
                    || graph.Unit(sku.SaleUomId).Dimension != UnitDimension.Mass))
            {
                return Result.Fail<ProductDto>(new(
                    "WEIGHT_MODE_REQUIRES_MASS",
                    "الصيغة الوزنية تتطلب وحدات كتلة (كجم/جرام) أساساً وبيعاً"));
            }
            if (mode == SaleMode.Serialized && !request.TrackSerials)
            {
                return Result.Fail<ProductDto>(new(
                    "SERIALIZED_REQUIRES_TRACK_SERIALS",
                    "صيغة SERIALIZED تتطلب تفعيل تتبع السيريال على المنتج"));
            }

            // حقول الوزن: إلزامية عند WEIGHT وممنوعة عند غيرها (مرآة قيد sku_weight_mode_fields)
            var w = sku.Weight;
            if (mode == SaleMode.Weight)
            {
                if (w is null || w.WeightPrecisionG is null || w.WeightStepG is null
                    || w.WeightPrecisionG <= 0 || w.WeightStepG <= 0 || w.WeightStepG < w.WeightPrecisionG)
                {
                    return Result.Fail<ProductDto>(new(
                        "WEIGHT_FIELDS_REQUIRED",
                        "الصيغة الوزنية توجب دقة وخطوة وزن موجبتين والخطوة ≥ الدقة (D-07)"));
                }
                if (w.MinWeightKg is < 0 || (w.MinWeightKg is not null && w.MaxWeightKg is not null && w.MinWeightKg >= w.MaxWeightKg))
                {
                    return Result.Fail<ProductDto>(new("WEIGHT_BOUNDS_INVALID", "حدود الوزن غير صالحة (الأدنى < الأقصى)"));
                }
                if (w.DefaultTareKg < 0)
                {
                    return Result.Fail<ProductDto>(new("TARE_NEGATIVE", "التصفير الافتراضي لا يكون سالباً"));
                }
            }
            else if (w is not null && (w.WeightPrecisionG is not null || w.WeightStepG is not null
                         || w.MinWeightKg is not null || w.MaxWeightKg is not null || w.DefaultTareKg != 0))
            {
                return Result.Fail<ProductDto>(new(
                    "WEIGHT_FIELDS_FORBIDDEN",
                    "حقول الوزن تُقبل للصيغة الوزنية فقط"));
            }
        }

        // صيغ PACK توجب تعريف عبوة يغطيها، وتعريف العبوة يشير لصيغتين موجودتين
        var packageSkus = request.Skus
            .Select((s, i) => new { s.SkuCode, s.SaleMode, Ordinal = i })
            .ToList();
        foreach (var pkg in request.Packages)
        {
            var packageSku = packageSkus.FirstOrDefault(s => s.Ordinal == pkg.PackageSkuOrdinal);
            var contentSku = packageSkus.FirstOrDefault(s => s.Ordinal == pkg.ContentSkuOrdinal);
            if (packageSku is null || contentSku is null)
            {
                return Result.Fail<ProductDto>(new("PACKAGE_ORDINAL_INVALID", "تعريف عبوة يشير لصيغة غير موجودة في الطلب"));
            }
            if (packageSku.SaleMode is not (SaleModeCodes.Pack or SaleModeCodes.Count))
            {
                return Result.Fail<ProductDto>(new(
                    "PACKAGE_MODE_INVALID",
                    $"SKU العبوة ({packageSku.SkuCode}) صيغتها {packageSku.SaleMode} — يجب أن تكون PACK أو COUNT"));
            }
            if (pkg.PackageSkuOrdinal == pkg.ContentSkuOrdinal)
            {
                return Result.Fail<ProductDto>(new("PACKAGE_SELF_FORBIDDEN", "العبوة لا تحتوي نفسها"));
            }
            if (pkg.UnitsPerPackage <= 0)
            {
                return Result.Fail<ProductDto>(new("PACKAGE_UNITS_INVALID", "عدد الوحدات داخل العبوة يجب أن يكون موجباً"));
            }
            // وحدة المحتوى: بيع العبوة يخصم من رصيد المحتوى بوحدته الأساسية — المحتوى صيغة مفردة
            // (عددي/سيريال): لا عبوة داخل عبوة ولا عبوة من صنف وزني (الموزونة مسبقاً تُرحّل بوزنها في G6)
            var contentMode = SaleModeCodes.FromCode(contentSku.SaleMode);
            if (contentMode == SaleMode.Pack)
            {
                return Result.Fail<ProductDto>(new(
                    "PACKAGE_CONTENT_NESTED",
                    "لا تُعبّأ عبوة داخل عبوة — المحتوى يجب أن يكون صيغة مفردة"));
            }
            if (contentMode == SaleMode.Weight)
            {
                return Result.Fail<ProductDto>(new(
                    "PACKAGE_CONTENT_WEIGHT_NOT_ALLOWED",
                    "العبوة من صنف وزني (موزونة مسبقاً) تُرحّل بوزنها الثابت في G6 — ليست كمية عددية"));
            }
        }
        foreach (var sku in packageSkus.Where(s => s.SaleMode == SaleModeCodes.Pack))
        {
            if (request.Packages.All(p => p.PackageSkuOrdinal != sku.Ordinal))
            {
                return Result.Fail<ProductDto>(new(
                    "PACKAGE_DEFINITION_REQUIRED",
                    $"الصيغة «{sku.SkuCode}» معلنة PACK بلا تعريف عبوة"));
            }
        }

        // ── 4) الباركود: تنميط وتحقق رقم التحقق وتفرد النشط ──
        var normalizedBarcodes = new List<BarcodePlan>();
        foreach (var barcode in request.Barcodes)
        {
            if (barcode.SkuOrdinal >= request.Skus.Count)
            {
                return Result.Fail<ProductDto>(new("BARCODE_SKU_INVALID", "باركود يشير لصيغة غير موجودة في الطلب"));
            }
            var validated = BarcodeNormalizer.Validate(barcode.BarcodeValue, barcode.BarcodeType);
            if (!validated.IsSuccess)
            {
                return Result.Fail<ProductDto>(validated.Error!);
            }
            normalizedBarcodes.Add(new BarcodePlan(
                barcode.BarcodeValue.Trim(), validated.Value!, barcode.BarcodeType, barcode.IsPrintedLabel, barcode.SkuOrdinal));
        }
        var duplicateInRequest = normalizedBarcodes
            .GroupBy(b => b.BarcodeNormalized)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateInRequest is not null)
        {
            return Result.Fail<ProductDto>(new(
                "BARCODE_DUPLICATE",
                $"الباركود {duplicateInRequest.Key} مكرر داخل الطلب — نطاق التفرد يشمل كل الصيغ"));
        }

        // ── 5) PLU: أرقام 4–5 خانات وتفرد ──
        var plus = new List<PluPlan>();
        foreach (var plu in request.Plus)
        {
            if (plu.SkuOrdinal >= request.Skus.Count)
            {
                return Result.Fail<ProductDto>(new("PLU_SKU_INVALID", "PLU يشير لصيغة غير موجودة في الطلب"));
            }
            var code = plu.PluCode.Trim();
            if (code.Length is < 4 or > 5 || !code.All(char.IsAsciiDigit))
            {
                return Result.Fail<ProductDto>(new("PLU_INVALID", "كود PLU من 4 إلى 5 أرقام كما في الميزان"));
            }
            plus.Add(new PluPlan(code, plu.SkuOrdinal));
        }

        // ── 6) الأسعار: موجبة دائماً (لا سعر صفر غير مقصود) وقائمة معرّفة ──
        foreach (var price in request.Prices)
        {
            if (price.SkuOrdinal >= request.Skus.Count)
            {
                return Result.Fail<ProductDto>(new("PRICE_SKU_INVALID", "سعر يشير لصيغة غير موجودة في الطلب"));
            }
            if (price.UnitPrice <= 0)
            {
                return Result.Fail<ProductDto>(new(
                    "PRICE_NOT_POSITIVE",
                    "السعر صفر أو سالب مرفوض — لا سعر صفر غير مقصود (§4.4 د)"));
            }
            if (!priceListIds.Contains(price.PriceListId))
            {
                return Result.Fail<ProductDto>(new("PRICE_LIST_NOT_FOUND", "القائمة السعرية غير موجودة أو غير نشطة"));
            }
            if (!graph.HasUnit(price.UnitId))
            {
                return Result.Fail<ProductDto>(new("UNIT_UNKNOWN", "وحدة السعر غير معرفة"));
            }
            // وحدة السعر يجب أن تطابق بُعد صيغة البيع (سعر الكيلو للوزني/سعر القطعة للعددي)
            var skuMode = SaleModeCodes.FromCode(request.Skus[(int)price.SkuOrdinal].SaleMode);
            var priceUnitDimension = graph.Unit(price.UnitId).Dimension;
            var saleDimension = graph.Unit(request.Skus[(int)price.SkuOrdinal].SaleUomId).Dimension;
            if (priceUnitDimension != saleDimension && skuMode != SaleMode.Pack)
            {
                return Result.Fail<ProductDto>(new(
                    "PRICE_UNIT_MISMATCH",
                    "بُعد وحدة السعر لا يطابق بُعد وحدة البيع للصيغة"));
            }
            if (price.TaxTreatment is not ("TAXABLE" or "ZERO_RATED" or "EXEMPT"))
            {
                return Result.Fail<ProductDto>(new("TAX_TREATMENT_INVALID", "معاملة ضريبية غير معروفة (TAXABLE/ZERO_RATED/EXEMPT)"));
            }
        }
        // سعر مستقل لكل صيغة: كل صيغة تُباع تحتاج سعراً في القائمة الافتراضية
        var defaultList = priceLists.FirstOrDefault(p => p.IsDefault);
        if (defaultList is not null)
        {
            for (var i = 0; i < request.Skus.Count; i++)
            {
                if (request.Prices.All(p => p.SkuOrdinal != i))
                {
                    return Result.Fail<ProductDto>(new(
                        "PRICE_REQUIRED_PER_SKU",
                        $"الصيغة «{request.Skus[i].SkuCode}» بلا سعر — كل صيغة بيع تحتاج سعراً مستقلاً"));
                }
            }
        }

        // ── 7) تفرد الأكواد ضد المخزون القائم (نطاق التفرد §3.1) ──
        if (await _catalog.ProductCodeExistsAsync(request.Code.Trim(), ct))
        {
            return Result.Fail<ProductDto>(new("PRODUCT_CODE_DUPLICATE", $"كود المنتج {request.Code} مستخدم — التفرد على مستوى النشاط"));
        }
        foreach (var sku in request.Skus)
        {
            if (await _catalog.SkuCodeExistsAsync(sku.SkuCode.Trim(), ct))
            {
                return Result.Fail<ProductDto>(new("SKU_CODE_DUPLICATE", $"كود الصيغة {sku.SkuCode} مستخدم — التفرد على مستوى النشاط"));
            }
        }
        foreach (var barcode in normalizedBarcodes)
        {
            if (await _catalog.ActiveBarcodeExistsAsync(barcode.BarcodeNormalized, ct))
            {
                return Result.Fail<ProductDto>(new("BARCODE_DUPLICATE", $"الباركود {barcode.BarcodeNormalized} نشط لصيغة أخرى — نطاق التفرد"));
            }
        }
        foreach (var plu in plus)
        {
            if (await _catalog.PluCodeExistsAsync(plu.PluCode, ct))
            {
                return Result.Fail<ProductDto>(new("PLU_DUPLICATE", $"كود PLU {plu.PluCode} مستخدم"));
            }
        }
        var duplicateSkuInRequest = request.Skus
            .GroupBy(s => s.SkuCode.Trim(), StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateSkuInRequest is not null)
        {
            return Result.Fail<ProductDto>(new("SKU_CODE_DUPLICATE", $"كود الصيغة {duplicateSkuInRequest.Key} مكرر داخل الطلب"));
        }

        // ── 8) التنفيذ الذري: كل الأقسام أو لا شيء ──
        var plan = new CreateProductPlan(
            request.Code.Trim(), request.NameAr.Trim(), request.NameEn?.Trim(),
            request.CategoryId, request.BrandId, request.Description?.Trim(),
            request.TrackLots, request.TrackSerials,
            [.. request.Skus.Select((s, i) => new SkuPlan(
                s.SkuCode.Trim(), s.SaleMode, s.BaseUomId, s.SaleUomId, s.NameAr?.Trim(),
                s.Weight?.WeightPrecisionG, s.Weight?.WeightStepG,
                s.Weight?.AllowManualWeight ?? true, s.Weight?.AllowAmountTarget ?? true,
                s.Weight?.MinWeightKg, s.Weight?.MaxWeightKg, s.Weight?.DefaultTareKg ?? 0,
                s.ReorderLevel, s.AllowNegative))],
            [.. normalizedBarcodes],
            [.. plus],
            [.. request.Prices.Select(p => new PricePlan(p.SkuOrdinal, p.PriceListId, p.UnitId, p.UnitPrice, p.TaxTreatment))],
            [.. request.Packages.Select(p => new PackagePlan((int)p.PackageSkuOrdinal, (int)p.ContentSkuOrdinal, p.UnitsPerPackage, p.OpenPackageAllowed))]);

        CreatedProduct created;
        try
        {
            created = await _catalog.CreateProductAsync(plan, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
        {
            // سباق على التفرد بين الطلبين — رسالة موحدة (القيد الحاسم في القاعدة)
            return Result.Fail<ProductDto>(new("CODE_RACE_DUPLICATE", "تعارض تفرد مع طلب متزامن — أعد المحاولة بكود مختلف"));
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "catalog.products", EntityId: created.ProductId, Action: "PRODUCT_CREATE", Result: "SUCCESS",
            Reason: $"code={plan.Code} · skus={plan.Skus.Count} · barcodes={plan.Barcodes.Count}",
            CorrelationId: correlationId), ct);

        LogProductCreated(plan.Code, created.ProductId, plan.Skus.Count, correlationId);

        var full = await _catalog.FindProductAsync(created.ProductId, ct);
        return full is null
            ? Result.Fail<ProductDto>(new("PRODUCT_READ_BACK_FAILED", "حُفظ المنتج وتعذر قراءته — تحقق بالمعرف"))
            : Result.Ok(full);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "منتج جديد {Code} (id={ProductId}) بصيغ {SkuCount} — correlation={CorrelationId}")]
    private partial void LogProductCreated(string code, long productId, int skuCount, string? correlationId);
}

/// <summary>
/// البحث في الكتالوج: مطابقة دقيقة بالباركود/SKU/PLU أولاً (Exact Match §3.1)،
/// وبحث نصي محدود بترقيم خادمي للجدول المتقدم (§7.2.1 — ملايين السجلات).
/// </summary>
public sealed class SearchCatalogUseCase
{
    public const int MaxPageSize = 50;

    private readonly ICatalogRepository _catalog;

    public SearchCatalogUseCase(ICatalogRepository catalog) => _catalog = catalog;

    public async Task<Result<CatalogLookupResponse>> LookupAsync(string rawCode, CancellationToken ct = default)
    {
        var normalized = BarcodeNormalizer.NormalizeForLookup(rawCode);
        if (normalized.Length == 0)
        {
            return Result.Fail<CatalogLookupResponse>(new("LOOKUP_EMPTY", "قيمة البحث مطلوبة"));
        }

        var byBarcode = await _catalog.FindByBarcodeAsync(normalized, ct);
        if (byBarcode is not null)
        {
            return Result.Ok(byBarcode);
        }
        var bySku = await _catalog.FindBySkuCodeAsync(rawCode.Trim().ToUpperInvariant(), ct);
        if (bySku is not null)
        {
            return Result.Ok(bySku);
        }
        var byPlu = await _catalog.FindByPluAsync(normalized, ct);
        if (byPlu is not null)
        {
            return Result.Ok(byPlu);
        }

        return Result.Fail<CatalogLookupResponse>(new(
            "LOOKUP_NOT_FOUND",
            $"لا صيغة بيع تطابق «{rawCode}» بالباركود أو SKU أو PLU"));
    }

    public async Task<Result<CatalogSearchResponse>> SearchAsync(
        string? query, string? saleMode, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize is 0 ? 25 : pageSize, 1, MaxPageSize);
        if (saleMode is not null and not ("COUNT" or "WEIGHT" or "PACK" or "SERIALIZED"))
        {
            return Result.Fail<CatalogSearchResponse>(new("SALE_MODE_INVALID", "فلتر صيغة البيع غير معروف"));
        }
        if (status is not null and not ("ACTIVE" or "INACTIVE" or "DISCONTINUED"))
        {
            return Result.Fail<CatalogSearchResponse>(new("STATUS_INVALID", "فلتر الحالة غير معروف"));
        }
        if (query is not null)
        {
            query = query.Trim();
            if (query.Length < 2)
            {
                return Result.Fail<CatalogSearchResponse>(new(
                    "QUERY_TOO_SHORT",
                    "البحث النصي من حرفين على الأقل — للمفرد استخدم المطابقة الدقيقة"));
            }
            if (query.Length > 100)
            {
                query = query[..100]; // حد نص البحث — لا مسح غير محدود
            }
        }

        var (items, total) = await _catalog.SearchProductsAsync(query, saleMode, status, page, pageSize, ct);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return Result.Ok(new CatalogSearchResponse(page, pageSize, total, totalPages, items));
    }
}

/// <summary>قراءة منتج كامل بصيغه وأسعاره وعبواته.</summary>
public sealed class GetProductUseCase
{
    private readonly ICatalogRepository _catalog;

    public GetProductUseCase(ICatalogRepository catalog) => _catalog = catalog;

    public async Task<Result<ProductDto>> ExecuteAsync(long productId, CancellationToken ct = default)
    {
        var product = await _catalog.FindProductAsync(productId, ct);
        return product is null
            ? Result.Fail<ProductDto>(new("PRODUCT_NOT_FOUND", "المنتج غير موجود"))
            : Result.Ok(product);
    }
}

/// <summary>قائمة الوحدات والتحويلات المفحوصة (لتغذية المحرر وفلانتر الواجهة).</summary>
public sealed class ListUnitsUseCase
{
    private readonly IUnitCatalog _units;

    public ListUnitsUseCase(IUnitCatalog units) => _units = units;

    public async Task<Result<UnitsResponse>> ExecuteAsync(CancellationToken ct = default)
    {
        var graphResult = await _units.LoadGraphAsync(ct);
        if (!graphResult.IsSuccess)
        {
            return Result.Fail<UnitsResponse>(graphResult.Error!);
        }
        var graph = graphResult.Value!;
        var units = graph.Units
            .Select(u => new UnitDto(u.UomId, u.Code, u.Code switch
            {
                "PIECE" => "قطعة",
                "KG" => "كيلوجرام",
                "G" => "جرام",
                "L" => "لتر",
                "ML" => "مليلتر",
                "M" => "متر",
                "CM" => "سنتيمتر",
                _ => u.Code,
            }, UnitDimensionCodes.ToCode(u.Dimension), u.IsBase, u.Decimals))
            .OrderBy(u => u.Dimension).ThenBy(u => u.Code)
            .ToList();
        var conversions = graph.Conversions
            .Select(c => new UnitConversionDto(c.FromUomId, c.ToUomId, c.Factor, IsFixed: true))
            .ToList();
        return Result.Ok(new UnitsResponse(units, conversions));
    }
}

/// <summary>مرجع المحرر: الفئات والعلامات والقوائم السعرية النشطة.</summary>
public sealed class GetCatalogReferenceUseCase
{
    private readonly ICatalogRepository _catalog;

    public GetCatalogReferenceUseCase(ICatalogRepository catalog) => _catalog = catalog;

    public async Task<Result<Contracts.V1.CatalogReferenceResponse>> ExecuteAsync(CancellationToken ct = default)
    {
        var categories = await _catalog.ListCategoriesAsync(activeOnly: true, ct);
        var brands = await _catalog.ListBrandsAsync(activeOnly: true, ct);
        var priceLists = await _catalog.ListPriceListsAsync(activeOnly: true, ct);
        return Result.Ok(new Contracts.V1.CatalogReferenceResponse(
            [.. categories.Select(c => new Contracts.V1.CategoryDto(c.CategoryId, c.Code, c.NameAr, c.IsActive))],
            [.. brands.Select(b => new Contracts.V1.BrandDto(b.BrandId, b.Code, b.NameAr, b.IsActive))],
            [.. priceLists.Select(p => new Contracts.V1.PriceListDto(p.PriceListId, p.Code, p.NameAr, p.Currency, p.IsDefault))]));
    }
}

/// <summary>
/// ترحيل حركة مخزون موثقة بمستند مصدر (G4 — إثبات تحويل المخزون الصحيح لكل
/// صيغة بيع في بيع/شراء/مرتجع/جرد): يحسب الكمية بالوحدة الأساسية بلقطة عامل
/// صريحة ثم يقيّد صف الدفتر Append-only. مستندات البيع والشراء الكاملة تبني
/// عليها G7/G6 — هذه هي البوابة الحسابية الموحدة للتحويل.
/// </summary>
public sealed partial class PostStockMovementUseCase
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        StockMovementTypes.Opening, StockMovementTypes.Receive, StockMovementTypes.Sale,
        StockMovementTypes.SaleReturn, StockMovementTypes.AdjustmentIn, StockMovementTypes.AdjustmentOut,
    };

    private readonly ICatalogRepository _catalog;
    private readonly IUnitCatalog _units;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<PostStockMovementUseCase> _logger;

    public PostStockMovementUseCase(
        ICatalogRepository catalog,
        IUnitCatalog units,
        IAuditWriter audit,
        ICurrentUser user,
        ILogger<PostStockMovementUseCase> logger)
    {
        _catalog = catalog;
        _units = units;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<PostedStockMovementResponse>> ExecuteAsync(
        long skuId, PostStockMovementRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!AllowedTypes.Contains(request.MovementType))
        {
            return Result.Fail<PostedStockMovementResponse>(new(
                "MOVEMENT_TYPE_NOT_ALLOWED",
                "نوع الحركة خارج نطاق G4 (المسموح: افتتاحي/استلام/بيع/مرتجع بيع/تسوية جرد ±)"));
        }
        if (request.Quantity <= 0)
        {
            return Result.Fail<PostedStockMovementResponse>(new("QTY_NOT_POSITIVE", "الكمية يجب أن تكون موجبة"));
        }
        if (string.IsNullOrWhiteSpace(request.SourceDocType) || request.SourceDocId <= 0)
        {
            return Result.Fail<PostedStockMovementResponse>(new(
                "MOVEMENT_SOURCE_REQUIRED",
                "الحركة توثق بمستند مصدر (نوع ومعرف) — الدفتر لا يقبل حركة بلا مصدر"));
        }
        if (!await _catalog.WarehouseExistsAsync(request.WarehouseId, ct))
        {
            return Result.Fail<PostedStockMovementResponse>(new("WAREHOUSE_NOT_FOUND", "المخزن غير موجود"));
        }
        var warehouseBranch = await _catalog.FindWarehouseBranchAsync(request.WarehouseId, ct);
        if (warehouseBranch is null)
        {
            return Result.Fail<PostedStockMovementResponse>(new("WAREHOUSE_NOT_FOUND", "المخزن غير موجود"));
        }
        if (_user.BranchId is not { } userBranch)
        {
            return Result.Fail<PostedStockMovementResponse>(new(
                "BRANCH_SCOPE_REQUIRED",
                "ترحيل الحركة يتطلب تسجيل دخول بنطاق فرع — الدفتر يوثق كل حركة بفرعها"));
        }
        if (warehouseBranch.Value != userBranch)
        {
            return Result.Fail<PostedStockMovementResponse>(new(
                "WAREHOUSE_BRANCH_MISMATCH",
                "المخزن يتبع فرعاً آخر غير نطاق فرعك"));
        }
        if (StockMovementTypes.StocktakeKinds.Contains(request.MovementType)
            && (request.Reason is null || request.Reason.Trim().Length < 3))
        {
            return Result.Fail<PostedStockMovementResponse>(new(
                "ADJUSTMENT_REASON_REQUIRED",
                "تسويات الجرد تلزم سبباً موثقاً (≥ 3 محارف)"));
        }

        var graphResult = await _units.LoadGraphAsync(ct);
        if (!graphResult.IsSuccess)
        {
            return Result.Fail<PostedStockMovementResponse>(graphResult.Error!);
        }
        var graph = graphResult.Value!;

        var sku = await _catalog.FindSkuSaleInfoAsync(skuId, ct);
        if (sku is null)
        {
            return Result.Fail<PostedStockMovementResponse>(new("SKU_NOT_FOUND", "صيغة البيع غير موجودة"));
        }

        // العبوة: الأثر يقع على المحتوى — نجلب تعريفها الساري وصنف محتواها
        PackageInfo? package = null;
        SkuSaleInfo? contentSku = null;
        if (sku.SaleMode == SaleMode.Pack)
        {
            package = await _catalog.FindActivePackageAsync(skuId, ct);
            if (package is null)
            {
                return Result.Fail<PostedStockMovementResponse>(new(
                    "PACKAGE_DEFINITION_REQUIRED",
                    "الصيغة PACK بلا تعريف عبوة ساري"));
            }
            contentSku = await _catalog.FindSkuSaleInfoAsync(package.ContentSkuId, ct);
            if (contentSku is null)
            {
                return Result.Fail<PostedStockMovementResponse>(new("PACKAGE_CONTENT_NOT_FOUND", "صنف محتوى العبوة غير موجود"));
            }
            if (contentSku.SaleMode == SaleMode.Weight)
            {
                // عبوة من صنف وزني = عبوة موزونة مسبقاً (PACK_PREWEIGHED §9.3 ط6): تُرحَّل بوزنها الثابت
                // — خارج نطاق G4 (يُنفذ في G6 مع استلام الموزون) — رفض صريح لا تخمين.
                return Result.Fail<PostedStockMovementResponse>(new(
                    "PACK_PREWEIGHED_NOT_SUPPORTED_HERE",
                    "عبوة من صنف وزني (موزونة مسبقاً) تُرحَّل بوزنها الثابت في G6 — ليست كمية عددية"));
            }
        }

        var snapshot = StockQuantityCalculator.Resolve(
            sku, request.Quantity, request.SourceUomId, graph,
            package: package, contentSku: contentSku, serials: request.Serials);
        if (!snapshot.IsSuccess)
        {
            return Result.Fail<PostedStockMovementResponse>(snapshot.Error!);
        }
        var snap = snapshot.Value!;

        var direction = StockMovementTypes.DirectionOf(request.MovementType);
        var movementId = await _catalog.PostStockMovementAsync(new StockMovementDraft(
            snap.SkuId, request.WarehouseId, request.MovementType, direction,
            snap.BaseQty, snap.SourceUomId, snap.ConversionFactor,
            request.SourceDocType, request.SourceDocId, request.SourceLineRef,
            request.Reason?.Trim(), userBranch, _user.UserId), ct);

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "inv.stock_movements", EntityId: movementId,
            Action: "STOCK_MOVEMENT_POST", Result: "SUCCESS",
            Reason: $"{request.MovementType} sku={sku.SkuCode} qty={snap.BaseQty} factor={snap.ConversionFactor}",
            CorrelationId: correlationId), ct);

        LogMovementPosted(movementId, request.MovementType, sku.SkuCode, snap.BaseQty, snap.ConversionFactor, correlationId);
        return Result.Ok(new PostedStockMovementResponse(
            movementId, snap.SkuId, snap.BaseQty, snap.SourceUomId, snap.ConversionFactor,
            request.MovementType, direction));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "حركة {MovementId} ({MovementType}) على {SkuCode}: base={BaseQty} factor={Factor} — correlation={CorrelationId}")]
    private partial void LogMovementPosted(long movementId, string movementType, string skuCode, decimal baseQty, decimal factor, string? correlationId);
}

/// <summary>تغيير حالة منتج — تعطيل/تفعيل/إيقاف دائم (لا حذف تاريخي §12).</summary>
public sealed class SetProductStatusUseCase
{
    private readonly ICatalogRepository _catalog;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public SetProductStatusUseCase(ICatalogRepository catalog, IAuditWriter audit, ICurrentUser user)
    {
        _catalog = catalog;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<long>> ExecuteAsync(long productId, SetProductStatusRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request.Status is not ("ACTIVE" or "INACTIVE" or "DISCONTINUED"))
        {
            return Result.Fail<long>(new("STATUS_INVALID", "الحالة يجب أن تكون ACTIVE أو INACTIVE أو DISCONTINUED"));
        }
        var updated = await _catalog.SetProductStatusAsync(productId, request.Status, ct);
        if (!updated.IsSuccess)
        {
            return updated;
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "catalog.products", EntityId: productId, Action: "PRODUCT_STATUS_CHANGE",
            Result: "SUCCESS", Reason: $"{request.Status} · {request.ReasonAr}", CorrelationId: correlationId), ct);
        return updated;
    }
}
