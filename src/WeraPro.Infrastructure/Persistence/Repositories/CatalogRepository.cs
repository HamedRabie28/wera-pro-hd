namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WeraPro.Application.Catalog;
using WeraPro.Domain.Catalog;
using WeraPro.Domain.SharedKernel;
using WeraPro.Infrastructure.Persistence;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>مستودع الوحدات والتحويلات — يعيد المخطط المفحوص جاهزاً (G4).</summary>
public sealed class UnitCatalog : IUnitCatalog
{
    private readonly WeraDbContext _db;

    public UnitCatalog(WeraDbContext db) => _db = db;

    public async Task<Result<UnitConversionGraph>> LoadGraphAsync(CancellationToken ct = default)
    {
        var units = await ListUnitsAsync(ct);
        var conversions = await ListConversionsAsync(ct);
        return UnitConversionGraph.TryCreate([.. units], [.. conversions]);
    }

    public async Task<IReadOnlyList<UnitDefinition>> ListUnitsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Set<UnitRow>().AsNoTracking().OrderBy(u => u.UomId).ToListAsync(ct);
        return [.. rows.Select(r => new UnitDefinition(
            r.UomId, r.Code, UnitDimensionCodes.FromCode(r.Dimension), r.IsBase, r.Decimals))];
    }

    public async Task<IReadOnlyList<ConversionDefinition>> ListConversionsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Set<UnitConversionRow>().AsNoTracking().OrderBy(c => c.ConversionId).ToListAsync(ct);
        return [.. rows.Select(r => new ConversionDefinition(r.FromUomId, r.ToUomId, r.Factor))];
    }
}

/// <summary>
/// مستودع الكتالوج (G4): الإنشاء متعدد الأقسام داخل معاملة واحدة — أي فشل
/// يلغي كل شيء (لا حفظ جزئي)، والقراءات مركّبة بصيغها وباركودها وأسعارها
/// وعبواتها، والبحث النصي محدود بترقيم خادمي صارم.
/// </summary>
public sealed class CatalogRepository : ICatalogRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public CatalogRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ── وجود مسبق (نطاق التفرد §3.1) ─────────────────────────────────────

    public async Task<bool> ProductCodeExistsAsync(string code, CancellationToken ct = default) =>
        await _db.Products.AsNoTracking().AnyAsync(p => p.Code == code, ct);

    public async Task<bool> SkuCodeExistsAsync(string skuCode, CancellationToken ct = default) =>
        await _db.Set<SkuRow>().AsNoTracking().AnyAsync(s => s.SkuCode == skuCode, ct);

    public async Task<bool> ActiveBarcodeExistsAsync(string barcodeNormalized, CancellationToken ct = default) =>
        await _db.Set<BarcodeRow>().AsNoTracking()
            .AnyAsync(b => b.BarcodeNormalized == barcodeNormalized && b.Status == "ACTIVE", ct);

    public async Task<bool> PluCodeExistsAsync(string pluCode, CancellationToken ct = default) =>
        await _db.Set<PluRow>().AsNoTracking().AnyAsync(p => p.PluCode == pluCode, ct);

    public async Task<IReadOnlyList<CatalogCategory>> ListCategoriesAsync(bool activeOnly, CancellationToken ct = default)
    {
        var query = _db.Set<CategoryRow>().AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(c => c.IsActive);
        }
        var rows = await query.OrderBy(c => c.Code).ToListAsync(ct);
        return [.. rows.Select(r => new CatalogCategory(r.CategoryId, r.Code, r.NameAr, r.IsActive))];
    }

    public async Task<IReadOnlyList<CatalogBrand>> ListBrandsAsync(bool activeOnly, CancellationToken ct = default)
    {
        var query = _db.Set<BrandRow>().AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(b => b.IsActive);
        }
        var rows = await query.OrderBy(b => b.Code).ToListAsync(ct);
        return [.. rows.Select(r => new CatalogBrand(r.BrandId, r.Code, r.NameAr, r.IsActive))];
    }

    public async Task<IReadOnlyList<PriceListInfo>> ListPriceListsAsync(bool activeOnly, CancellationToken ct = default)
    {
        var query = _db.Set<PriceListRow>().AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(p => p.IsActive);
        }
        var rows = await query.OrderBy(p => p.PriceListId).ToListAsync(ct);
        return [.. rows.Select(r => new PriceListInfo(r.PriceListId, r.Code, r.NameAr, r.Currency, r.IsDefault))];
    }

    // ── الإنشاء الذري ────────────────────────────────────────────────────

    public async Task<CreatedProduct> CreateProductAsync(CreateProductPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = _clock.GetUtcNow();

                var product = new ProductRow
                {
                    Code = plan.Code,
                    NameAr = plan.NameAr,
                    NameEn = plan.NameEn,
                    CategoryId = plan.CategoryId,
                    BrandId = plan.BrandId,
                    Description = plan.Description,
                    Status = "ACTIVE",
                    TrackLots = plan.TrackLots,
                    TrackSerials = plan.TrackSerials,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.Products.Add(product);
                await _db.SaveChangesAsync(ct);

                var skuIds = new List<(long SkuId, string SkuCode)>();
                foreach (var sku in plan.Skus)
                {
                    var row = new SkuRow
                    {
                        ProductId = product.ProductId,
                        SkuCode = sku.SkuCode,
                        SaleMode = sku.SaleMode,
                        BaseUomId = sku.BaseUomId,
                        SaleUomId = sku.SaleUomId,
                        SkuNameAr = sku.NameAr,
                        Status = "ACTIVE",
                        WeightPrecisionG = sku.WeightPrecisionG,
                        WeightStepG = sku.WeightStepG,
                        AllowManualWeight = sku.AllowManualWeight,
                        AllowAmountTarget = sku.AllowAmountTarget,
                        MinWeightKg = sku.MinWeightKg,
                        MaxWeightKg = sku.MaxWeightKg,
                        DefaultTareKg = sku.DefaultTareKg,
                        ReorderLevel = sku.ReorderLevel,
                        AllowNegative = sku.AllowNegative,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    _db.Set<SkuRow>().Add(row);
                    await _db.SaveChangesAsync(ct);
                    skuIds.Add((row.SkuId, row.SkuCode));
                }

                var barcodeIds = new List<long>();
                foreach (var barcode in plan.Barcodes)
                {
                    var row = new BarcodeRow
                    {
                        SkuId = skuIds[barcode.SkuOrdinal].SkuId,
                        BarcodeValue = barcode.BarcodeValue,
                        BarcodeNormalized = barcode.BarcodeNormalized,
                        BarcodeType = barcode.BarcodeType,
                        Status = "ACTIVE",
                        IsPrintedLabel = barcode.IsPrintedLabel,
                        CreatedAt = now,
                    };
                    _db.Set<BarcodeRow>().Add(row);
                    await _db.SaveChangesAsync(ct);
                    barcodeIds.Add(row.BarcodeId);
                }

                var pluIds = new List<long>();
                foreach (var plu in plan.Plus)
                {
                    var row = new PluRow
                    {
                        PluCode = plu.PluCode,
                        SkuId = skuIds[plu.SkuOrdinal].SkuId,
                        IsActive = true,
                    };
                    _db.Set<PluRow>().Add(row);
                    await _db.SaveChangesAsync(ct);
                    pluIds.Add(row.PluId);
                }

                var priceIds = new List<long>();
                foreach (var price in plan.Prices)
                {
                    var row = new ProductPriceRow
                    {
                        SkuId = skuIds[(int)price.SkuOrdinal].SkuId,
                        PriceListId = price.PriceListId,
                        UnitId = price.UnitId,
                        UnitPrice = price.UnitPrice,
                        TaxTreatment = price.TaxTreatment,
                        ValidFrom = now,
                        ValidTo = null,
                        CreatedAt = now,
                    };
                    _db.Set<ProductPriceRow>().Add(row);
                    await _db.SaveChangesAsync(ct);
                    priceIds.Add(row.PriceId);
                }

                var packageIds = new List<long>();
                foreach (var package in plan.Packages)
                {
                    var row = new PackageRow
                    {
                        PackageSkuId = skuIds[package.PackageSkuOrdinal].SkuId,
                        ContentSkuId = skuIds[package.ContentSkuOrdinal].SkuId,
                        UnitsPerPackage = package.UnitsPerPackage,
                        OpenPackageAllowed = package.OpenPackageAllowed,
                        ValidFrom = now,
                        ValidTo = null,
                    };
                    _db.Set<PackageRow>().Add(row);
                    await _db.SaveChangesAsync(ct);
                    packageIds.Add(row.PackageId);
                }

                await tx.CommitAsync(ct);
                return new CreatedProduct(product.ProductId, skuIds, barcodeIds, pluIds, priceIds, packageIds);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    // ── القراءات المركّبة ────────────────────────────────────────────────

    public async Task<Contracts.V1.ProductDto?> FindProductAsync(long productId, CancellationToken ct = default)
    {
        var product = await _db.Products.AsNoTracking()
            .Include(p => p.Category).Include(p => p.Brand)
            .FirstOrDefaultAsync(p => p.ProductId == productId, ct);
        if (product is null)
        {
            return null;
        }
        var composites = await LoadSkuCompositesAsync([productId], ct);
        List<PackageRow> packages = [];
        if (composites.TryGetValue(productId, out var list))
        {
            packages.AddRange(list.SelectMany(c => c.OwnPackages));
        }
        return ComposeProduct(product, composites, packages);
    }

    public async Task<(IReadOnlyList<Contracts.V1.ProductDto> Items, long TotalCount)> SearchProductsAsync(
        string? query, string? saleMode, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var products = _db.Products.AsNoTracking().Include(p => p.Category).Include(p => p.Brand).AsQueryable();

        if (status is not null)
        {
            products = products.Where(p => p.Status == status);
        }
        if (query is not null)
        {
            // بحث نصي محدود: ILIKE PostgreSQL (حالة-غير-حساسة) على الاسم/الكود —
            // يترجم لـ lower() في القاعدة فوق فهارس G1 (لا مسح غير محدود)
            var pattern = "%" + EscapeLikePattern(query) + "%";
            products = products.Where(p =>
                EF.Functions.ILike(p.NameAr, pattern)
                || (p.NameEn != null && EF.Functions.ILike(p.NameEn, pattern))
                || EF.Functions.ILike(p.Code, pattern));
        }
        if (saleMode is not null)
        {
            // فلتر صيغة البيع على مستوى الصيغ: منتج له SKU بهذه الصيغة النشطة
            var modeProducts = _db.Set<SkuRow>().AsNoTracking()
                .Where(s => s.SaleMode == saleMode && s.Status == "ACTIVE")
                .Select(s => s.ProductId);
            products = products.Where(p => modeProducts.Contains(p.ProductId));
        }

        var total = await products.LongCountAsync(ct);
        var pageRows = await products
            .OrderBy(p => p.ProductId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        var composites = await LoadSkuCompositesAsync([.. pageRows.Select(p => p.ProductId)], ct);

        var items = pageRows.Select(p =>
        {
            List<PackageRow> packages = [];
            if (composites.TryGetValue(p.ProductId, out var list))
            {
                packages.AddRange(list.SelectMany(c => c.OwnPackages));
            }
            return ComposeProduct(p, composites, packages);
        }).ToList();
        return (items, total);
    }

    /// <summary>تهريب محارف نمط ILIKE (%, _, \\) في نص البحث المستخدم.</summary>
    private static string EscapeLikePattern(string input) =>
        input.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<Contracts.V1.CatalogLookupResponse?> FindByBarcodeAsync(string barcodeNormalized, CancellationToken ct = default)
    {
        var hit = await (
            from barcode in _db.Set<BarcodeRow>().AsNoTracking()
            join sku in _db.Set<SkuRow>().AsNoTracking() on barcode.SkuId equals sku.SkuId
            where barcode.BarcodeNormalized == barcodeNormalized && barcode.Status == "ACTIVE"
            select new { barcode.BarcodeValue, sku.ProductId, sku.SkuId })
            .FirstOrDefaultAsync(ct);
        if (hit is null)
        {
            return null;
        }
        return await ComposeLookupAsync("BARCODE", hit.BarcodeValue, hit.ProductId, hit.SkuId, ct);
    }

    public async Task<Contracts.V1.CatalogLookupResponse?> FindBySkuCodeAsync(string skuCode, CancellationToken ct = default)
    {
        var hit = await _db.Set<SkuRow>().AsNoTracking()
            .Where(s => s.SkuCode == skuCode && s.Status == "ACTIVE")
            .Select(s => new { s.SkuCode, s.ProductId, s.SkuId })
            .FirstOrDefaultAsync(ct);
        if (hit is null)
        {
            return null;
        }
        return await ComposeLookupAsync("SKU", hit.SkuCode, hit.ProductId, hit.SkuId, ct);
    }

    public async Task<Contracts.V1.CatalogLookupResponse?> FindByPluAsync(string pluCode, CancellationToken ct = default)
    {
        var hit = await (
            from plu in _db.Set<PluRow>().AsNoTracking()
            join sku in _db.Set<SkuRow>().AsNoTracking() on plu.SkuId equals sku.SkuId
            where plu.PluCode == pluCode && plu.IsActive && sku.Status == "ACTIVE"
            select new { plu.PluCode, sku.ProductId, sku.SkuId })
            .FirstOrDefaultAsync(ct);
        if (hit is null)
        {
            return null;
        }
        return await ComposeLookupAsync("PLU", hit.PluCode, hit.ProductId, hit.SkuId, ct);
    }

    public async Task<Result<long>> SetProductStatusAsync(long productId, string status, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductId == productId, ct);
        if (product is null)
        {
            return Result.Fail<long>(new DomainError("PRODUCT_NOT_FOUND", "المنتج غير موجود"));
        }
        product.Status = status;
        product.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Result.Ok(product.ProductId);
    }

    public async Task<SkuSaleInfo?> FindSkuSaleInfoAsync(long skuId, CancellationToken ct = default)
    {
        var row = await _db.Set<SkuRow>().AsNoTracking().FirstOrDefaultAsync(s => s.SkuId == skuId, ct);
        return row is null
            ? null
            : new SkuSaleInfo(
                row.SkuId, row.ProductId, row.SkuCode, SaleModeCodes.FromCode(row.SaleMode),
                row.BaseUomId, row.SaleUomId, row.MinWeightKg, row.MaxWeightKg);
    }

    public async Task<PackageInfo?> FindActivePackageAsync(long packageSkuId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var row = await _db.Set<PackageRow>().AsNoTracking()
            .Where(p => p.PackageSkuId == packageSkuId
                && (p.ValidTo == null || p.ValidTo > now))
            .OrderByDescending(p => p.ValidFrom)
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new PackageInfo(row.PackageSkuId, row.ContentSkuId, row.UnitsPerPackage, row.OpenPackageAllowed);
    }

    public async Task<bool> WarehouseExistsAsync(long warehouseId, CancellationToken ct = default) =>
        await _db.Set<WarehouseRow>().AsNoTracking().AnyAsync(w => w.WarehouseId == warehouseId, ct);

    public async Task<long?> FindWarehouseBranchAsync(long warehouseId, CancellationToken ct = default) =>
        await _db.Set<WarehouseRow>().AsNoTracking()
            .Where(w => w.WarehouseId == warehouseId)
            .Select(w => (long?)w.BranchId)
            .FirstOrDefaultAsync(ct);

    public async Task<long> PostStockMovementAsync(StockMovementDraft draft, CancellationToken ct = default)
    {
        var row = new StockMovementRow
        {
            SkuId = draft.SkuId,
            WarehouseId = draft.WarehouseId,
            MovementType = draft.MovementType,
            Direction = (short)draft.Direction,
            BaseQty = draft.BaseQty,
            SourceUomId = draft.SourceUomId,
            ConversionFactor = draft.ConversionFactor,
            SourceDocType = draft.SourceDocType,
            SourceDocId = draft.SourceDocId,
            SourceLineRef = draft.SourceLineRef,
            Reason = draft.Reason,
            BranchId = draft.BranchId,
            UserId = draft.UserId,
            OccurredAt = _clock.GetUtcNow(),
        };
        _db.Set<StockMovementRow>().Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (
            ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            // قيود حاسمة (D-03 وقيود الحركة) — تُترجم لرسالة مفهومة بلا كشف داخلي
            var raw = ((PostgresException)ex.InnerException).MessageText;
            var detail = raw.Contains("NEGATIVE_STOCK_NEEDS_APPROVAL", StringComparison.Ordinal)
                ? "الرصيد السالب يتطلب اعتماد مشرف معتمداً (D-03=SUPERVISOR_APPROVAL)"
                : raw.Contains("NEGATIVE_STOCK_BLOCKED", StringComparison.Ordinal)
                    ? "السياسة تمنع الرصيد السالب (D-03=BLOCK)"
                    : "الحركة تخالف قيود الدفتر";
            throw new WeraPro.Application.Catalog.StockPolicyViolationException(detail);
        }
        return row.MovementId;
    }

    // ── التركيب الداخلي ──────────────────────────────────────────────────

    /// <summary>تحميل صيغ المنتجات مع كل ملحقاتها (باركود/PLU/أسعار/عبوات) في استعلامات مجمّعة.</summary>
    private async Task<Dictionary<long, List<SkuCompose>>> LoadSkuCompositesAsync(
        IReadOnlyList<long> productIds, CancellationToken ct)
    {
        var skuRows = await _db.Set<SkuRow>().AsNoTracking()
            .Where(s => productIds.Contains(s.ProductId))
            .OrderBy(s => s.SkuId)
            .ToListAsync(ct);
        if (skuRows.Count == 0)
        {
            return [];
        }

        var skuIds = skuRows.Select(s => s.SkuId).ToList();

        var barcodeRows = await _db.Set<BarcodeRow>().AsNoTracking()
            .Where(b => skuIds.Contains(b.SkuId)).OrderBy(b => b.BarcodeId).ToListAsync(ct);
        var pluRows = await _db.Set<PluRow>().AsNoTracking()
            .Where(p => skuIds.Contains(p.SkuId)).OrderBy(p => p.PluId).ToListAsync(ct);
        var priceRows = await (
            from price in _db.Set<ProductPriceRow>().AsNoTracking()
            join list in _db.Set<PriceListRow>().AsNoTracking() on price.PriceListId equals list.PriceListId
            where skuIds.Contains(price.SkuId)
            orderby price.PriceId
            select new PriceWithList(price, list.Code)).ToListAsync(ct);
        var packageRows = await _db.Set<PackageRow>().AsNoTracking()
            .Where(p => skuIds.Contains(p.PackageSkuId) || skuIds.Contains(p.ContentSkuId))
            .OrderBy(p => p.PackageId).ToListAsync(ct);

        var result = new Dictionary<long, List<SkuCompose>>();
        var unitCodes = (await _db.Set<UnitRow>().AsNoTracking().ToListAsync(ct))
            .ToDictionary(u => u.UomId, u => u.Code);
        foreach (var group in skuRows.GroupBy(s => s.ProductId))
        {
            var composites = new List<SkuCompose>();
            foreach (var sku in group)
            {
                composites.Add(new SkuCompose(
                    sku,
                    [.. barcodeRows.Where(b => b.SkuId == sku.SkuId)],
                    [.. pluRows.Where(p => p.SkuId == sku.SkuId)],
                    [.. priceRows.Where(p => p.Price.SkuId == sku.SkuId).Select(p => (p.Price, p.ListCode))],
                    [.. packageRows.Where(p => p.PackageSkuId == sku.SkuId)],
                    BaseCode: unitCodes.TryGetValue(sku.BaseUomId, out var baseCode) ? baseCode : "?",
                    SaleCode: unitCodes.TryGetValue(sku.SaleUomId, out var saleCode) ? saleCode : "?"));
            }
            result[group.Key] = composites;
        }
        return result;
    }

    private static Contracts.V1.ProductDto ComposeProduct(
        ProductRow product,
        Dictionary<long, List<SkuCompose>> skus,
        IReadOnlyList<PackageRow> packages)
    {
        var skuDtos = (skus.TryGetValue(product.ProductId, out var composites) ? composites : [])
            .Select(c => new Contracts.V1.SkuDto(
                c.Sku.SkuId,
                c.Sku.SkuCode,
                c.Sku.SkuNameAr,
                c.Sku.SaleMode,
                c.Sku.BaseUomId,
                c.BaseCode,
                c.Sku.SaleUomId,
                c.SaleCode,
                c.Sku.Status,
                Weight: c.Sku.SaleMode == "WEIGHT"
                    ? new Contracts.V1.SkuWeightSettings(
                        c.Sku.WeightPrecisionG, c.Sku.WeightStepG,
                        c.Sku.AllowManualWeight, c.Sku.AllowAmountTarget,
                        c.Sku.MinWeightKg, c.Sku.MaxWeightKg, c.Sku.DefaultTareKg)
                    : null,
                c.Sku.ReorderLevel,
                c.Sku.AllowNegative,
                [.. c.Barcodes.Select(b => new Contracts.V1.BarcodeDto(
                    b.BarcodeId, b.SkuId, b.BarcodeValue, b.BarcodeNormalized, b.BarcodeType, b.Status, b.IsPrintedLabel))],
                [.. c.Plus.Select(p => new Contracts.V1.PluDto(p.PluId, p.SkuId, p.PluCode, p.IsActive))],
                [.. c.Prices.Select(p => new Contracts.V1.PriceDto(
                    p.Price.PriceId, p.Price.SkuId, p.Price.PriceListId, p.ListCode,
                    p.Price.UnitId, p.Price.UnitPrice, p.Price.TaxTreatment))]))
            .ToList();

        return new Contracts.V1.ProductDto(
            product.ProductId,
            product.Code,
            product.NameAr,
            product.NameEn,
            product.CategoryId,
            product.Category?.NameAr ?? "?",
            product.BrandId,
            product.Brand?.NameAr,
            product.Description,
            product.Status,
            product.TrackLots,
            product.TrackSerials,
            skuDtos,
            [.. packages.Select(p => new Contracts.V1.PackageDto(
                p.PackageSkuId, p.ContentSkuId, p.UnitsPerPackage, p.OpenPackageAllowed))]);
    }

    private async Task<Contracts.V1.CatalogLookupResponse> ComposeLookupAsync(
        string kind, string value, long productId, long skuId, CancellationToken ct)
    {
        var product = await _db.Products.AsNoTracking()
            .Include(p => p.Category).Include(p => p.Brand)
            .FirstAsync(p => p.ProductId == productId, ct);
        var composites = await LoadSkuCompositesAsync([productId], ct);
        var packages = composites.TryGetValue(productId, out var list)
            ? list.SelectMany(c => c.OwnPackages).ToList()
            : [];
        var dto = ComposeProduct(product, composites, packages);
        var sku = dto.Skus.First(s => s.SkuId == skuId);
        return new Contracts.V1.CatalogLookupResponse(kind, value, dto, sku);
    }

    /// <summary>مادة تركيب الصيغة مع ملحقاتها.</summary>
    private sealed record SkuCompose(
        SkuRow Sku,
        List<BarcodeRow> Barcodes,
        List<PluRow> Plus,
        List<(ProductPriceRow Price, string ListCode)> Prices,
        List<PackageRow> OwnPackages,
        string BaseCode,
        string SaleCode);

    /// <summary>سعر مع كود قائمته (نتيجة وصلة).</summary>
    private sealed record PriceWithList(ProductPriceRow Price, string ListCode);
}
