namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using WeraPro.Application.Media;
using WeraPro.Domain.Media;
using WeraPro.Infrastructure.Persistence;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>
/// مستودع الوسائط (G5) — ينفذ واجهتي المكتبة ومهام الاستيراد معاً: كل كتابة
/// داخل معاملة، وكل خرق تفرّد من القاعدة يُترجم برمز معلوم (409) لا 500،
/// وكل مفتاح تخزين يمر عبر MediaStorageKeys قبل مساس أي قرص.
/// </summary>
public sealed class MediaRepository : IMediaRepository, IMediaImportRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public MediaRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ══ IMediaRepository — المكتبة ═════════════════════════════════════════

    public async Task<MediaAssetInfo?> FindByShaAsync(string sha256, CancellationToken ct = default)
    {
        var row = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Sha256 == sha256, ct);
        return row is null ? null : MapAsset(row, linkCount: 0); // لفحص التكرر يكفي الوجود
    }

    public async Task<MediaAssetInfo?> FindMediaAsync(long mediaId, CancellationToken ct = default)
    {
        var row = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.MediaId == mediaId, ct);
        if (row is null)
        {
            return null;
        }
        var linkCount = await _db.ProductImages.AsNoTracking().CountAsync(l => l.MediaId == mediaId, ct);
        return MapAsset(row, linkCount);
    }

    public async Task<(IReadOnlyList<MediaAssetInfo> Items, long TotalCount)> SearchMediaAsync(
        MediaSearchFilter filter, CancellationToken ct = default)
    {
        var query = _db.MediaAssets.AsNoTracking().AsQueryable();
        if (filter.Sha256Prefix is { Length: > 0 } prefix)
        {
            query = query.Where(m => m.Sha256.StartsWith(prefix));
        }
        if (filter.MimeType is not null)
        {
            query = query.Where(m => m.MimeType == filter.MimeType);
        }

        // فلترة حالة الربط خادمياً (EXISTS) — لا مسح غير محدود
        if (filter.LinkState is "LINKED" or "UNLINKED")
        {
            var linked = filter.LinkState == "LINKED";
            query = query.Where(m => linked
                ? _db.ProductImages.Any(p => p.MediaId == m.MediaId)
                : !_db.ProductImages.Any(p => p.MediaId == m.MediaId));
        }

        var total = await query.LongCountAsync(ct);
        var pageRows = await query
            .OrderByDescending(m => m.MediaId)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);

        // LinkCount دفعة واحدة للمصفوفة (لا N+1)
        var ids = pageRows.Select(r => r.MediaId).ToArray();
        var counts = ids.Length == 0
            ? new Dictionary<long, long>()
            : await _db.ProductImages.AsNoTracking()
                .Where(p => ids.Contains(p.MediaId))
                .GroupBy(p => p.MediaId)
                .Select(g => new { g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var items = pageRows.Select(r => MapAsset(r, counts.GetValueOrDefault(r.MediaId))).ToList();
        return (items, total);
    }

    public async Task<IReadOnlyList<MediaLinkInfo>> ListLinksAsync(long mediaId, CancellationToken ct = default)
    {
        var result = new List<MediaLinkInfo>();

        var productLinks = await _db.ProductImages.AsNoTracking()
            .Where(l => l.MediaId == mediaId && l.ProductId != null)
            .Join(_db.Products.AsNoTracking(), l => l.ProductId, p => p.ProductId, (l, p) => new { l, p })
            .Join(_db.MediaAssets.AsNoTracking(), x => x.l.MediaId, m => m.MediaId, (x, m) => new { x.l, x.p, m })
            .OrderBy(x => x.l.SortOrder).ThenBy(x => x.l.LinkId)
            .ToListAsync(ct);
        result.AddRange(productLinks.Select(x => new MediaLinkInfo(
            x.l.LinkId, x.l.MediaId, "PRODUCT", x.l.ProductId!.Value, x.p.Code, x.p.NameAr, x.l.IsPrimary, x.l.SortOrder,
            x.m.ThumbPosKey != null)));

        var skuLinks = await _db.ProductImages.AsNoTracking()
            .Where(l => l.MediaId == mediaId && l.SkuId != null)
            .Join(_db.Skus.AsNoTracking(), l => l.SkuId, s => s.SkuId, (l, s) => new { l, s })
            .Join(_db.Products.AsNoTracking(), x => x.s.ProductId, p => p.ProductId, (x, p) => new { x.l, x.s, p })
            .Join(_db.MediaAssets.AsNoTracking(), x => x.l.MediaId, m => m.MediaId, (x, m) => new { x.l, x.s, x.p, m })
            .OrderBy(x => x.l.SortOrder).ThenBy(x => x.l.LinkId)
            .ToListAsync(ct);
        result.AddRange(skuLinks.Select(x => new MediaLinkInfo(
            x.l.LinkId, x.l.MediaId, "SKU", x.l.SkuId!.Value, x.s.SkuCode, x.s.SkuNameAr ?? x.p.NameAr, x.l.IsPrimary, x.l.SortOrder,
            x.m.ThumbPosKey != null)));

        result.Sort(static (a, b) => a.TargetType == b.TargetType
            ? (a.SortOrder, a.LinkId).CompareTo((b.SortOrder, b.LinkId))
            : string.CompareOrdinal(a.TargetType, b.TargetType));
        return result;
    }

    public Task<long> CountLinksAsync(long mediaId, CancellationToken ct = default) =>
        _db.ProductImages.AsNoTracking().LongCountAsync(l => l.MediaId == mediaId, ct);

    public async Task<long> InsertAssetAsync(MediaAssetDraft draft, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = new MediaAssetRow
                {
                    StorageKey = draft.StorageKey,
                    Sha256 = draft.Sha256,
                    MimeType = draft.MimeType,
                    WidthPx = draft.WidthPx,
                    HeightPx = draft.HeightPx,
                    SizeBytes = draft.SizeBytes,
                    UploadedBy = draft.UploadedBy,
                    CreatedAt = _clock.GetUtcNow(),
                    ThumbCatalogKey = draft.ThumbCatalogKey,
                    ThumbPosKey = draft.ThumbPosKey,
                    PreviewKey = draft.PreviewKey,
                    Ahash = draft.Ahash,
                    ProcessedAt = draft.ProcessedAt,
                };
                _db.MediaAssets.Add(row);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return row.MediaId;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await tx.RollbackAsync(CancellationToken.None);
                // مفتاح التخزين فريد (موجّه بالمحتوى): سباق رفعين متزامنين لنفس المحتوى = تكرر تام
                throw new MediaRepositoryConflictException(
                    MediaErrorCodes.ExactDuplicate,
                    MediaErrorCodes.MessageAr(MediaErrorCodes.ExactDuplicate));
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public async Task<bool> DeleteAssetAsync(long mediaId, CancellationToken ct = default) =>
        await _db.MediaAssets.AsNoTracking()
            .Where(m => m.MediaId == mediaId)
            .ExecuteDeleteAsync(ct) > 0;

    public async Task<bool> UpdateAssetVariantsAsync(long mediaId, MediaVariantsUpdate update, CancellationToken ct = default)
    {
        var affected = await _db.MediaAssets.AsNoTracking()
            .Where(m => m.MediaId == mediaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.ThumbCatalogKey, update.ThumbCatalogKey)
                .SetProperty(m => m.ThumbPosKey, update.ThumbPosKey)
                .SetProperty(m => m.PreviewKey, update.PreviewKey)
                .SetProperty(m => m.Ahash, update.Ahash)
                .SetProperty(m => m.ProcessedAt, update.ProcessedAt), ct);
        return affected > 0;
    }

    public async Task<MediaLinkInfo> InsertLinkAsync(MediaLinkDraft draft, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var mediaRow = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.MediaId == draft.MediaId, ct);
                if (mediaRow is null)
                {
                    throw new MediaRepositoryConflictException(
                        MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound));
                }

                string targetCode;
                string targetNameAr;
                if (draft.ProductId is { } productId)
                {
                    var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId, ct);
                    if (product is null)
                    {
                        throw new MediaRepositoryConflictException(
                            MediaErrorCodes.TargetNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.TargetNotFound));
                    }
                    targetCode = product.Code;
                    targetNameAr = product.NameAr;

                    if (await _db.ProductImages.AsNoTracking().AnyAsync(
                            l => l.MediaId == draft.MediaId && l.ProductId == productId, ct))
                    {
                        throw new MediaRepositoryConflictException(
                            MediaErrorCodes.LinkExists, MediaErrorCodes.MessageAr(MediaErrorCodes.LinkExists));
                    }

                    if (draft.MakePrimary)
                    {
                        await _db.ProductImages.AsNoTracking()
                            .Where(l => l.ProductId == productId && l.IsPrimary && l.MediaId != draft.MediaId)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, false), ct);
                    }

                    var link = new ProductImageRow
                    {
                        MediaId = draft.MediaId,
                        ProductId = productId,
                        SkuId = null,
                        SortOrder = 0,
                        IsPrimary = draft.MakePrimary,
                    };
                    _db.ProductImages.Add(link);
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return new MediaLinkInfo(link.LinkId, draft.MediaId, "PRODUCT", productId, targetCode, targetNameAr,
                        draft.MakePrimary, 0, mediaRow.ThumbPosKey != null);
                }

                var skuId = draft.SkuId!.Value;
                var sku = await _db.Skus.AsNoTracking().FirstOrDefaultAsync(s => s.SkuId == skuId, ct);
                if (sku is null)
                {
                    throw new MediaRepositoryConflictException(
                        MediaErrorCodes.TargetNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.TargetNotFound));
                }
                var product2 = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == sku.ProductId, ct);
                targetCode = sku.SkuCode;
                targetNameAr = sku.SkuNameAr ?? product2?.NameAr ?? sku.SkuCode;

                if (await _db.ProductImages.AsNoTracking().AnyAsync(
                        l => l.MediaId == draft.MediaId && l.SkuId == skuId, ct))
                {
                    throw new MediaRepositoryConflictException(
                        MediaErrorCodes.LinkExists, MediaErrorCodes.MessageAr(MediaErrorCodes.LinkExists));
                }

                if (draft.MakePrimary)
                {
                    await _db.ProductImages.AsNoTracking()
                        .Where(l => l.SkuId == skuId && l.IsPrimary && l.MediaId != draft.MediaId)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, false), ct);
                }

                var link2 = new ProductImageRow
                {
                    MediaId = draft.MediaId,
                    ProductId = null,
                    SkuId = skuId,
                    SortOrder = 0,
                    IsPrimary = draft.MakePrimary,
                };
                _db.ProductImages.Add(link2);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new MediaLinkInfo(link2.LinkId, draft.MediaId, "SKU", skuId, targetCode, targetNameAr,
                    draft.MakePrimary, 0, mediaRow.ThumbPosKey != null);
            }
            catch (PostgresException pg) when (pg.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation)
            {
                await tx.RollbackAsync(CancellationToken.None);
                var code = pg.ConstraintName switch
                {
                    "uq_product_images_media_product" or "uq_product_images_media_sku" => MediaErrorCodes.LinkExists,
                    "uq_product_images_primary_sku" or "uq_product_images_primary_product" => MediaErrorCodes.LinkExists,
                    "media_assets_storage_key_key" => MediaErrorCodes.ExactDuplicate,
                    _ when pg.SqlState == PostgresErrorCodes.ForeignKeyViolation => MediaErrorCodes.MediaNotFound,
                    _ => MediaErrorCodes.LinkExists,
                };
                throw new MediaRepositoryConflictException(code, MediaErrorCodes.MessageAr(code));
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public async Task<MediaLinkInfo?> FindLinkAsync(long linkId, CancellationToken ct = default)
    {
        var row = await _db.ProductImages.AsNoTracking().FirstOrDefaultAsync(l => l.LinkId == linkId, ct);
        if (row is null)
        {
            return null;
        }
        var media = await _db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.MediaId == row.MediaId, ct);
        if (row.ProductId is { } productId)
        {
            var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == productId, ct);
            if (product is null)
            {
                return null;
            }
            return new MediaLinkInfo(linkId, row.MediaId, "PRODUCT", productId, product.Code, product.NameAr,
                row.IsPrimary, row.SortOrder, media?.ThumbPosKey != null);
        }
        var sku = await _db.Skus.AsNoTracking().FirstOrDefaultAsync(s => s.SkuId == row.SkuId, ct);
        if (sku is null)
        {
            return null;
        }
        var product2 = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == sku.ProductId, ct);
        return new MediaLinkInfo(linkId, row.MediaId, "SKU", row.SkuId!.Value, sku.SkuCode,
            sku.SkuNameAr ?? product2?.NameAr ?? sku.SkuCode, row.IsPrimary, row.SortOrder, media?.ThumbPosKey != null);
    }

    public async Task<bool> DeleteLinkAsync(long linkId, CancellationToken ct = default) =>
        await _db.ProductImages.AsNoTracking()
            .Where(l => l.LinkId == linkId)
            .ExecuteDeleteAsync(ct) > 0;

    public async Task<int> SetLinkPrimaryAsync(long linkId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = await _db.ProductImages.AsNoTracking().FirstOrDefaultAsync(l => l.LinkId == linkId, ct);
                if (row is null)
                {
                    return 0;
                }

                // تخلي باقي روابط الهدف نفسه ثم تعيين هذه — الفهرس الفريد الجزئي حارس أخير
                if (row.ProductId is { } productId)
                {
                    await _db.ProductImages.AsNoTracking()
                        .Where(l => l.ProductId == productId && l.LinkId != linkId)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, false), ct);
                }
                else
                {
                    await _db.ProductImages.AsNoTracking()
                        .Where(l => l.SkuId == row.SkuId && l.LinkId != linkId)
                        .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, false), ct);
                }

                var affected = await _db.ProductImages.AsNoTracking()
                    .Where(l => l.LinkId == linkId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, true), ct);
                await tx.CommitAsync(ct);
                return affected;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw new MediaRepositoryConflictException(
                    MediaErrorCodes.LinkExists, "تعارض رئيسية — أعد المحاولة");
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public async Task<IReadOnlyList<(long Id, long Ahash)>> ListAhashesAsync(CancellationToken ct = default) =>
        await _db.MediaAssets.AsNoTracking()
            .Where(m => m.Ahash != null)
            .Select(m => new ValueTuple<long, long>(m.MediaId, m.Ahash!.Value))
            .ToListAsync(ct);

    public async Task<SkuImageLinksSnapshot?> LoadSkuImageLinksAsync(long skuId, CancellationToken ct = default)
    {
        var sku = await _db.Skus.AsNoTracking().FirstOrDefaultAsync(s => s.SkuId == skuId, ct);
        if (sku is null)
        {
            return null;
        }
        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == sku.ProductId, ct);
        if (product is null)
        {
            return null;
        }

        var skuLevel = await _db.ProductImages.AsNoTracking()
            .Where(l => l.SkuId == skuId)
            .Join(_db.MediaAssets.AsNoTracking(), l => l.MediaId, m => m.MediaId, (l, m) => new { l, m })
            .OrderBy(x => x.l.SortOrder).ThenBy(x => x.l.LinkId)
            .ToListAsync(ct);

        var productLevel = await _db.ProductImages.AsNoTracking()
            .Where(l => l.ProductId == product.ProductId)
            .Join(_db.MediaAssets.AsNoTracking(), l => l.MediaId, m => m.MediaId, (l, m) => new { l, m })
            .OrderBy(x => x.l.SortOrder).ThenBy(x => x.l.LinkId)
            .ToListAsync(ct);

        return new SkuImageLinksSnapshot(
            skuId, sku.SkuCode, product.ProductId, product.Code,
            SkuLevel: [.. skuLevel.Select(x => new MediaLinkInfo(
                x.l.LinkId, x.l.MediaId, "SKU", skuId, sku.SkuCode, sku.SkuNameAr ?? product.NameAr, x.l.IsPrimary, x.l.SortOrder, x.m.ThumbPosKey != null))],
            ProductLevel: [.. productLevel.Select(x => new MediaLinkInfo(
                x.l.LinkId, x.l.MediaId, "PRODUCT", product.ProductId, product.Code, product.NameAr, x.l.IsPrimary, x.l.SortOrder, x.m.ThumbPosKey != null))]);
    }

    public async Task<(IReadOnlyList<MediaDuplicateEncounter> Items, long TotalCount)> ListExactEncountersAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.MediaImportFiles.AsNoTracking()
            .Where(f => f.State == "SKIPPED" && f.Sha256 != null)
            .Join(_db.MediaImportJobs.AsNoTracking(), f => f.JobId, j => j.JobId, (f, j) => new { f, j });

        var total = await query.LongCountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.f.FileId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return ([.. rows.Select(x => new MediaDuplicateEncounter(
            x.f.FileId, x.f.JobId, x.j.Label, x.f.ClientName, x.f.Sha256!, ParseExistingMediaId(x.f.SkipReason), x.f.ProcessedAt))], total);
    }

    /// <summary>EXACT_DUPLICATE_OF:{id} → المعرف — للعرض في التقرير.</summary>
    private static long? ParseExistingMediaId(string? skipReason)
    {
        if (skipReason is null || !skipReason.StartsWith("EXACT_DUPLICATE_OF:", StringComparison.Ordinal))
        {
            return null;
        }
        return long.TryParse(skipReason["EXACT_DUPLICATE_OF:".Length..], out var id) ? id : null;
    }

    private static MediaAssetInfo MapAsset(MediaAssetRow row, long linkCount) => new(
        row.MediaId, row.Sha256, row.MimeType, row.WidthPx ?? 0, row.HeightPx ?? 0, row.SizeBytes,
        row.Ahash,
        HasThumbCatalog: row.ThumbCatalogKey != null,
        HasThumbPos: row.ThumbPosKey != null,
        HasPreview: row.PreviewKey != null,
        StorageKey: row.StorageKey,
        ThumbCatalogKey: row.ThumbCatalogKey,
        ThumbPosKey: row.ThumbPosKey,
        PreviewKey: row.PreviewKey,
        LinkCount: linkCount,
        UploadedBy: row.UploadedBy,
        CreatedAt: row.CreatedAt,
        ProcessedAt: row.ProcessedAt);

    // ══ IMediaImportRepository — مهام الاستيراد ════════════════════════════

    public async Task<long> CreateJobAsync(string label, long userId, string storageScope, CancellationToken ct = default)
    {
        if (!MediaStorageScope.IsValid(storageScope))
        {
            // رفض صريح لا تخمين: البصمة صيغة معروفة أو لا إدراج
            throw new InvalidOperationException($"بصمة نطاق تخزين غير صالحة: '{storageScope}'");
        }
        var row = new MediaImportJobRow
        {
            Label = label,
            State = "OPEN",
            StorageScope = storageScope,
            CreatedBy = userId,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.MediaImportJobs.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.JobId;
    }

    public async Task<MediaImportJobInfo?> FindJobAsync(long jobId, CancellationToken ct = default)
    {
        var row = await _db.MediaImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == jobId, ct);
        return row is null ? null : MapJob(row);
    }

    public async Task<IReadOnlyList<long>> ListRunningJobIdsAsync(string storageScope, CancellationToken ct = default) =>
        await _db.MediaImportJobs.AsNoTracking()
            .Where(j => j.State == "RUNNING" && j.StorageScope == storageScope)
            .OrderBy(j => j.JobId)
            .Select(j => j.JobId)
            .ToListAsync(ct);

    public async Task<bool> TryStartJobAsync(long jobId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var affected = await _db.MediaImportJobs.AsNoTracking()
                    .Where(j => j.JobId == jobId && (j.State == "OPEN" || j.State == "CANCELLED"))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.State, "RUNNING")
                        .SetProperty(j => j.StartedAt, j => j.StartedAt ?? _clock.GetUtcNow()), ct);

                // تعافي الانهيار عند التشغيل/الاستئناف — بحراسة المهلة نفسها:
                // لا يُعاد إدراج ملف قيد المعالجة لدى عامل حي في مثيل آخر.
                var cutoff = _clock.GetUtcNow().AddSeconds(-MediaPolicy.StaleProcessingGraceSeconds);
                await _db.MediaImportFiles.AsNoTracking()
                    .Where(f => f.JobId == jobId && f.State == "PROCESSING"
                        && (f.ProcessedAt == null || f.ProcessedAt < cutoff))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.State, "PENDING")
                        .SetProperty(f => f.ProcessedAt, (DateTimeOffset?)null), ct);

                await tx.CommitAsync(ct);
                return affected > 0;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public async Task<bool> TryCancelJobAsync(long jobId, CancellationToken ct = default)
    {
        var affected = await _db.MediaImportJobs.AsNoTracking()
            .Where(j => j.JobId == jobId && (j.State == "RUNNING" || j.State == "OPEN"))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, "CANCELLED")
                .SetProperty(j => j.FinishedAt, _clock.GetUtcNow()), ct);
        return affected > 0;
    }

    public async Task CompleteJobAsync(long jobId, CancellationToken ct = default)
    {
        var affected = await _db.MediaImportJobs.AsNoTracking()
            .Where(j => j.JobId == jobId && j.State == "RUNNING")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, "COMPLETED")
                .SetProperty(j => j.FinishedAt, _clock.GetUtcNow()), ct);
        if (affected == 0)
        {
            // سباق إقفال بين عاملين: إن أقفلها غيرنا فلا خطأ — idempotent
            var state = await _db.MediaImportJobs.AsNoTracking()
                .Where(j => j.JobId == jobId).Select(j => j.State).FirstOrDefaultAsync(ct);
            if (state == "COMPLETED")
            {
                return;
            }
            throw new InvalidOperationException("إقفال المهمة يتطلب حالة RUNNING");
        }
    }

    public async Task<bool> HasRemainingWorkAsync(long jobId, CancellationToken ct = default) =>
        await _db.MediaImportFiles.AsNoTracking()
            .AnyAsync(f => f.JobId == jobId && (f.State == "PENDING" || f.State == "PROCESSING"), ct);

    public async Task<int> NextOrdinalAsync(long jobId, CancellationToken ct = default)
    {
        // قفل استشاري داخل معاملة: يسلّل الإضافات المتوازية لنفس المهمة
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtextextended('media_import_add:' || @j::text, 0))",
                    [new NpgsqlParameter("j", jobId)], ct);

                var max = await _db.MediaImportFiles.AsNoTracking()
                    .Where(f => f.JobId == jobId)
                    .MaxAsync(f => (int?)f.Ordinal, ct);

                await tx.CommitAsync(ct);
                return (max ?? 0) + 1;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public async Task<ImportFileRowInfo> AddFileAsync(long jobId, ImportStagedFile file, CancellationToken ct = default)
    {
        try
        {
            var row = new MediaImportFileRow
            {
                JobId = jobId,
                Ordinal = file.Ordinal,
                ClientName = file.ClientName,
                StagingKey = file.StagingKey,
                SizeBytes = file.SizeBytes,
                State = file.State,
                ErrorCode = file.ErrorCode,
                Sha256 = null,
                SkipReason = null,
                MediaId = null,
                ProcessedAt = null,
            };
            _db.MediaImportFiles.Add(row);
            await _db.SaveChangesAsync(ct);
            return MapFile(row);
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation && pg.ConstraintName == "mif_job_ordinal_uq")
        {
            // لا يحدث مع القفل الاستشاري — حارس أخير صريح
            throw new MediaRepositoryConflictException("ORDINAL_RACE", "تزاحم ترتيب الملفات — أعد المحاولة");
        }
    }

    public async Task<ImportFileRowInfo?> TryPickNextPendingAsync(long jobId, CancellationToken ct = default)
    {
        // التقاط ذري واحد لكل ملف: FOR UPDATE SKIP LOCKED يوزّع الملفات بين
        // العمال المتوازيين على خادم مشترك (عامل واحد في المثيل الواحد — لكن
        // الإقلاعات المتوازية والمصانع المتعددة حقيقة قائمة)، و processed_at
        // تُختم وقت الالتقاط: حراسة التعافي لا تلمس ملفاً ملتقطاً حديثاً بل
        // المعلّق فعلاً من انهيار سابق (أنظر RequeueStaleProcessingAsync).
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE catalog.media_import_files
               SET state = 'PROCESSING',
                   processed_at = now()
             WHERE file_id = (
                   SELECT file_id
                     FROM catalog.media_import_files
                    WHERE job_id = @j AND state = 'PENDING'
                    ORDER BY ordinal
                      FOR UPDATE SKIP LOCKED
                     LIMIT 1)
            RETURNING file_id, job_id, ordinal, client_name, staging_key, size_bytes,
                      state, sha256, error_code, skip_reason, media_id, processed_at
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "j";
        parameter.Value = jobId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new ImportFileRowInfo(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : new DateTimeOffset(
                DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc)));
    }

    public async Task<int> RequeueStaleProcessingAsync(long jobId, CancellationToken ct = default)
    {
        // تعافي الانهيار المحتاط: لا يُعاد إدراج إلا ما هو معلّق فعلاً — أي
        // PROCESSING بلا ختم التقاط (NULL) أو بختم أقدم من مهلة السماح.
        // الملف الملتقط قبل لحظات (عامل حي في مثيل آخر) لا يُلمس أبداً —
        // وإلا لتضاعفت معالجته بين العمال المتوازيين.
        var cutoff = _clock.GetUtcNow().AddSeconds(-MediaPolicy.StaleProcessingGraceSeconds);
        return await _db.MediaImportFiles.AsNoTracking()
            .Where(f => f.JobId == jobId && f.State == "PROCESSING"
                && (f.ProcessedAt == null || f.ProcessedAt < cutoff))
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.State, "PENDING")
                .SetProperty(f => f.ProcessedAt, (DateTimeOffset?)null), ct);
    }

    public async Task<ImportFileRowInfo?> FindFileAsync(long fileId, CancellationToken ct = default)
    {
        var row = await _db.MediaImportFiles.AsNoTracking().FirstOrDefaultAsync(f => f.FileId == fileId, ct);
        return row is null ? null : MapFile(row);
    }

    public async Task FinalizeFileAsync(ImportFileOutcome outcome, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = _clock.GetUtcNow();

                if (outcome.TerminalState == "DONE" && outcome.Asset is { } draft)
                {
                    // صف الأصل وصف الملف في معاملة واحدة — لا يُعلن DONE بلا أصل
                    var asset = new MediaAssetRow
                    {
                        StorageKey = draft.StorageKey,
                        Sha256 = draft.Sha256,
                        MimeType = draft.MimeType,
                        WidthPx = draft.WidthPx,
                        HeightPx = draft.HeightPx,
                        SizeBytes = draft.SizeBytes,
                        UploadedBy = draft.UploadedBy,
                        CreatedAt = now,
                        ThumbCatalogKey = draft.ThumbCatalogKey,
                        ThumbPosKey = draft.ThumbPosKey,
                        PreviewKey = draft.PreviewKey,
                        Ahash = draft.Ahash,
                        ProcessedAt = draft.ProcessedAt,
                    };
                    _db.MediaAssets.Add(asset);
                    await _db.SaveChangesAsync(ct);

                    await _db.MediaImportFiles.AsNoTracking()
                        .Where(f => f.FileId == outcome.FileId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(f => f.State, "DONE")
                            .SetProperty(f => f.Sha256, outcome.Sha256)
                            .SetProperty(f => f.MediaId, asset.MediaId)
                            .SetProperty(f => f.ErrorCode, (string?)null)
                            .SetProperty(f => f.SkipReason, (string?)null)
                            .SetProperty(f => f.ProcessedAt, now), ct);
                }
                else
                {
                    await _db.MediaImportFiles.AsNoTracking()
                        .Where(f => f.FileId == outcome.FileId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(f => f.State, outcome.TerminalState)
                            .SetProperty(f => f.Sha256, outcome.Sha256)
                            .SetProperty(f => f.ErrorCode, outcome.ErrorCode)
                            .SetProperty(f => f.SkipReason, outcome.SkipReason)
                            .SetProperty(f => f.MediaId, (long?)null)
                            .SetProperty(f => f.ProcessedAt, now), ct);
                }

                await tx.CommitAsync(ct);
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.UniqueViolation && pg.ConstraintName == "media_assets_storage_key_key")
            {
                await tx.RollbackAsync(CancellationToken.None);
                // سباق معالجة متوازية لنفس المحتوى (لا يحدث — معالج واحد) — تكرر تام
                throw new MediaRepositoryConflictException(
                    MediaErrorCodes.ExactDuplicate, MediaErrorCodes.MessageAr(MediaErrorCodes.ExactDuplicate));
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public async Task<(IReadOnlyList<ImportFileRowInfo> Items, long TotalCount)> ListFilesAsync(
        long jobId, string? state, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.MediaImportFiles.AsNoTracking().Where(f => f.JobId == jobId);
        if (state is not null)
        {
            query = query.Where(f => f.State == state);
        }
        var total = await query.LongCountAsync(ct);
        var rows = await query
            .OrderBy(f => f.Ordinal)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return ([.. rows.Select(MapFile)], total);
    }

    public async Task<MediaImportCounts> CountByStateAsync(long jobId, CancellationToken ct = default)
    {
        var grouped = await _db.MediaImportFiles.AsNoTracking()
            .Where(f => f.JobId == jobId)
            .GroupBy(f => f.State)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var map = grouped.ToDictionary(g => g.Key, g => g.Count);
        return new MediaImportCounts(
            Pending: map.GetValueOrDefault("PENDING"),
            Processing: map.GetValueOrDefault("PROCESSING"),
            Done: map.GetValueOrDefault("DONE"),
            Failed: map.GetValueOrDefault("FAILED"),
            Skipped: map.GetValueOrDefault("SKIPPED"));
    }

    public async Task<IReadOnlyDictionary<string, int>> ErrorHistogramAsync(long jobId, CancellationToken ct = default)
    {
        var rows = await _db.MediaImportFiles.AsNoTracking()
            .Where(f => f.JobId == jobId && f.State == "FAILED" && f.ErrorCode != null)
            .GroupBy(f => f.ErrorCode!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.Count);
    }

    private static MediaImportJobInfo MapJob(MediaImportJobRow row) => new(
        row.JobId, row.Label, row.State, row.StorageScope, row.CreatedBy, row.CreatedAt, row.StartedAt, row.FinishedAt);

    private static ImportFileRowInfo MapFile(MediaImportFileRow row) => new(
        row.FileId, row.JobId, row.Ordinal, row.ClientName, row.StagingKey, row.SizeBytes,
        row.State, row.Sha256, row.ErrorCode, row.SkipReason, row.MediaId, row.ProcessedAt);
}
