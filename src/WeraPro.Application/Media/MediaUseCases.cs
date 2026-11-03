namespace WeraPro.Application.Media;

using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Media;
using WeraPro.Domain.SharedKernel;

/// <summary>
/// رفع أصل واحد (رفع تفاعلي من المحرر/المكتبة): تحقق ترويسة ← بصمة ←
/// فحص التكرر (مكرر موجود يُعاد كاملاً مع إشارته — لا يُنشأ ثانياً) ← مساحة
/// حرة ← فك وتصغير (CORRUPT_IMAGE عند التلف) ← كتابة الأصل والنسخ الثلاث ←
/// إدراج الصف. فشل الإدراج = تنظيف تعويضي للملفات المكتوبة (لا أيتام).
/// </summary>
public sealed class UploadMediaUseCase(
    IMediaRepository repo,
    IMediaStorage storage,
    IMediaImageProcessor processor,
    IAuditWriter audit,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<Result<UploadOutcome>> ExecuteAsync(
        Stream file, string clientName, string? correlationId, CancellationToken ct = default)
    {
        if (!file.CanSeek)
        {
            return Result.Fail<UploadOutcome>(new("UNSUPPORTED_MEDIA_TYPE", "الرفع يتطلب تياراً قابلاً للتموضع"));
        }
        if (file.Length == 0 || file.Length > MediaPolicy.MaxFileBytes)
        {
            return Result.Fail<UploadOutcome>(new(MediaErrorCodes.TooLarge, MediaErrorCodes.MessageAr(MediaErrorCodes.TooLarge)));
        }

        file.Seek(0, SeekOrigin.Begin);
        var sniff = ImageHeaderSniffer.Sniff(file);
        if (sniff.Kind is MediaSniffResultKind.UnsupportedMediaType or MediaSniffResultKind.DimensionsExceedLimit or MediaSniffResultKind.Corrupt)
        {
            var code = sniff.Kind switch
            {
                MediaSniffResultKind.UnsupportedMediaType => MediaErrorCodes.UnsupportedMediaType,
                MediaSniffResultKind.DimensionsExceedLimit => MediaErrorCodes.DimensionsExceedLimit,
                _ => MediaErrorCodes.CorruptImage,
            };
            return Result.Fail<UploadOutcome>(new(code, MediaErrorCodes.MessageAr(code)));
        }

        var mime = sniff.MimeType!;
        var width = sniff.Width!.Value;
        var height = sniff.Height!.Value;

        file.Seek(0, SeekOrigin.Begin);
        var sha = await MediaHasher.ComputeHexAsync(file, ct);
        if (!MediaStorageKeys.IsValidSha256(sha))
        {
            return Result.Fail<UploadOutcome>(new(MediaErrorCodes.CorruptImage, MediaErrorCodes.MessageAr(MediaErrorCodes.CorruptImage)));
        }

        // فحص التكرر قبل أي كتابة (§6.1): مطابق تماماً → يُعاد الموجود كاملاً (لا يُنشأ ثانياً)
        if (await repo.FindByShaAsync(sha, ct) is { } existing)
        {
            return Result.Ok(UploadOutcome.Duplicate(existing));
        }

        // المساحة الحرة (§6.1) — غير المعروفة تُسمح (تصريح موثق)
        if (storage.GetAvailableFreeBytes() is { } free && free < MediaPolicy.MinFreeBytesForUpload)
        {
            return Result.Fail<UploadOutcome>(new(MediaErrorCodes.DiskSpace, MediaErrorCodes.MessageAr(MediaErrorCodes.DiskSpace)));
        }

        // فك وتصغير من التيار نفسه (لا كتابة قبل نجاح الفك — التالف لا يترك أثراً)
        ProcessedImageVariants variants;
        try
        {
            file.Seek(0, SeekOrigin.Begin);
            variants = processor.Process(file, mime, width, height);
        }
        catch (MediaProcessingException)
        {
            return Result.Fail<UploadOutcome>(new(MediaErrorCodes.CorruptImage, MediaErrorCodes.MessageAr(MediaErrorCodes.CorruptImage)));
        }

        var ext = MediaPolicy.StorageExtension(mime);
        var vext = variants.ThumbCatalogExt; // امتداد النسخ بامتداد المصدر (PNG→PNG · JPEG→JPEG)
        var keys = new
        {
            Orig = MediaStorageKeys.Original(sha, ext),
            ThumbCatalog = MediaStorageKeys.ThumbCatalog(sha, vext),
            ThumbPos = MediaStorageKeys.ThumbPos(sha, vext),
            Preview = MediaStorageKeys.Preview(sha, vext),
        };

        var written = new List<string>(4);
        try
        {
            file.Seek(0, SeekOrigin.Begin);
            await storage.WriteAsync(keys.Orig, file, ct);
            written.Add(keys.Orig);

            await storage.WriteAsync(keys.ThumbCatalog, new MemoryStream(variants.ThumbCatalog), ct);
            written.Add(keys.ThumbCatalog);
            await storage.WriteAsync(keys.ThumbPos, new MemoryStream(variants.ThumbPos), ct);
            written.Add(keys.ThumbPos);
            await storage.WriteAsync(keys.Preview, new MemoryStream(variants.Preview), ct);
            written.Add(keys.Preview);

            var ahash = AverageHash.Compute(variants.Gray8x8);
            var draft = new MediaAssetDraft(
                Sha256: sha,
                MimeType: mime,
                WidthPx: width,
                HeightPx: height,
                SizeBytes: file.Length,
                StorageKey: keys.Orig,
                ThumbCatalogKey: keys.ThumbCatalog,
                ThumbPosKey: keys.ThumbPos,
                PreviewKey: keys.Preview,
                Ahash: ahash,
                UploadedBy: user.IsAuthenticated ? user.UserId : null,
                ProcessedAt: clock.GetUtcNow());

            var mediaId = await repo.InsertAssetAsync(draft, ct);

            await audit.WriteAsync(new AuditEntry(
                UserId: user.IsAuthenticated ? user.UserId : null,
                RoleCode: null,
                StationId: user.StationId,
                BranchId: user.BranchId,
                EntitySchema: "catalog.media_assets",
                EntityId: mediaId,
                Action: "MEDIA_UPLOAD",
                Result: "SUCCESS",
                Reason: $"{mime} {width}×{height} {file.Length}B",
                CorrelationId: correlationId), ct);

            var created = new MediaAssetInfo(
                mediaId, sha, mime, width, height, file.Length, ahash,
                HasThumbCatalog: true, HasThumbPos: true, HasPreview: true,
                StorageKey: keys.Orig,
                ThumbCatalogKey: keys.ThumbCatalog,
                ThumbPosKey: keys.ThumbPos,
                PreviewKey: keys.Preview,
                LinkCount: 0,
                UploadedBy: draft.UploadedBy,
                CreatedAt: draft.ProcessedAt,
                ProcessedAt: draft.ProcessedAt);
            return Result.Ok(UploadOutcome.CreatedNew(created));
        }
        catch
        {
            // تنظيف تعويضي: فشل الإدراج لا يترك ملفات يتيمة (أفضل جهد — مسجل)
            foreach (var key in written)
            {
                try { await storage.DeleteAsync(key, CancellationToken.None); }
                catch { /* أفضل جهد: الملفات الموجهة بالمحتوى غير مؤذية عند التزاحم */ }
            }
            throw;
        }
    }
}

public sealed record UploadOutcome(bool Created, MediaAssetInfo? Existing, MediaAssetInfo? New)
{
    public static UploadOutcome Duplicate(MediaAssetInfo existing) => new(false, existing, null);
    public static UploadOutcome CreatedNew(MediaAssetInfo created) => new(true, null, created);
}

/// <summary>بحث المكتبة بترقيم خادمي — العدد الإجمالي من استعلام فعلي (§6.2).</summary>
public sealed class SearchMediaUseCase(IMediaRepository repo)
{
    public async Task<Result<MediaSearchOutcome>> ExecuteAsync(
        string? shaPrefix, string? mime, string? linkState, int? page, int? pageSize, CancellationToken ct = default)
    {
        var normalizedPage = Math.Max(1, page ?? 1);
        var normalizedSize = Math.Clamp(pageSize ?? 24, 1, 50); // طلب 500 يُقصّ إلى 50 — نمط G4

        shaPrefix = shaPrefix?.Trim().ToLowerInvariant();
        if (shaPrefix is { Length: > 0 } && (shaPrefix.Length < 8 || !shaPrefix.All("0123456789abcdef".Contains)))
        {
            return Result.Fail<MediaSearchOutcome>(new("SHA_PREFIX_INVALID", "بادئة البصمة يجب أن تكون 8 محارف سداسية عشرية صغيرة على الأقل"));
        }
        if (mime is not null && !MediaPolicy.SupportedMimeTypes.Contains(mime))
        {
            return Result.Fail<MediaSearchOutcome>(new("MIME_INVALID", "النوع المدعوم: image/jpeg أو image/png فقط"));
        }
        var state = (linkState ?? "ANY").ToUpperInvariant();
        if (state is not ("ANY" or "LINKED" or "UNLINKED"))
        {
            return Result.Fail<MediaSearchOutcome>(new("LINK_STATE_INVALID", "حالة الربط: ANY أو LINKED أو UNLINKED"));
        }

        var (items, total) = await repo.SearchMediaAsync(
            new MediaSearchFilter(
                Sha256Prefix: shaPrefix is { Length: > 0 } ? shaPrefix : null,
                MimeType: mime,
                LinkState: state,
                Page: normalizedPage,
                PageSize: normalizedSize), ct);

        return Result.Ok(new MediaSearchOutcome(
            items, normalizedPage, normalizedSize, total,
            TotalPages: (int)Math.Ceiling(total / (double)normalizedSize)));
    }
}

public sealed record MediaSearchOutcome(
    IReadOnlyList<MediaAssetInfo> Items, int Page, int PageSize, long TotalCount, int TotalPages);

/// <summary>تفاصيل أصل + روابطه الحالية (بيانات المراجعة قبل أي حذف).</summary>
public sealed class GetMediaDetailsUseCase(IMediaRepository repo)
{
    public async Task<Result<MediaDetails>> ExecuteAsync(long mediaId, CancellationToken ct = default)
    {
        var asset = await repo.FindMediaAsync(mediaId, ct);
        if (asset is null)
        {
            return Result.Fail<MediaDetails>(new(MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound)));
        }
        var links = await repo.ListLinksAsync(mediaId, ct);
        return Result.Ok(new MediaDetails(asset, links));
    }
}

public sealed record MediaDetails(MediaAssetInfo Asset, IReadOnlyList<MediaLinkInfo> Links);

/// <summary>قراءة نسخة (الأصل أو إحدى الثلاث) — 404 VariantNotReady إن لم تُعالج بعد.</summary>
public sealed class ServeMediaVariantUseCase(IMediaRepository repo, IMediaStorage storage)
{
    public async Task<Result<MediaVariantContent>> ExecuteAsync(long mediaId, string variant, CancellationToken ct = default)
    {
        var asset = await repo.FindMediaAsync(mediaId, ct);
        if (asset is null)
        {
            return Result.Fail<MediaVariantContent>(new(MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound)));
        }

        var kind = NormalizeVariant(variant);
        if (kind is not (MediaVariant.Original or MediaVariant.ThumbCatalog or MediaVariant.ThumbPos or MediaVariant.Preview))
        {
            return Result.Fail<MediaVariantContent>(new(MediaErrorCodes.VariantInvalid, MediaErrorCodes.MessageAr(MediaErrorCodes.VariantInvalid)));
        }

        var ready = kind switch
        {
            MediaVariant.Original => true,
            MediaVariant.ThumbCatalog => asset.HasThumbCatalog,
            MediaVariant.ThumbPos => asset.HasThumbPos,
            _ => asset.HasPreview,
        };
        if (!ready)
        {
            return Result.Fail<MediaVariantContent>(new(MediaErrorCodes.VariantNotReady, MediaErrorCodes.MessageAr(MediaErrorCodes.VariantNotReady)));
        }

        // المفاتيح حتمية من البصمة والنوع (موجهة بالمحتوى) — النسخ بامتداد المصدر
        var ext = MediaPolicy.StorageExtension(asset.MimeType);
        var key = kind switch
        {
            MediaVariant.Original => MediaStorageKeys.Original(asset.Sha256, ext),
            MediaVariant.ThumbCatalog => MediaStorageKeys.ThumbCatalog(asset.Sha256, ext),
            MediaVariant.ThumbPos => MediaStorageKeys.ThumbPos(asset.Sha256, ext),
            _ => MediaStorageKeys.Preview(asset.Sha256, ext),
        };

        var stream = await storage.OpenReadAsync(key, ct);
        return Result.Ok(new MediaVariantContent(stream, asset.MimeType, asset.Sha256, variant));
    }

    private static MediaVariant NormalizeVariant(string variant) => variant.ToLowerInvariant() switch
    {
        "original" => MediaVariant.Original,
        "thumb-catalog" => MediaVariant.ThumbCatalog,
        "thumb-pos" => MediaVariant.ThumbPos,
        "preview" => MediaVariant.Preview,
        _ => (MediaVariant)99,
    };
}

public enum MediaVariant { Original, ThumbCatalog, ThumbPos, Preview }

public sealed record MediaVariantContent(Stream Content, string MimeType, string Sha256, string Variant);

/// <summary>
/// ربط صورة بهدف واحد (منتج أو صيغة — لا الاثنين: مرآة قيد img_one_target)
/// — makePrimary يخلي الرئيسية السابقة للهدف داخل المعاملة نفسها.
/// </summary>
public sealed class LinkMediaUseCase(IMediaRepository repo, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<MediaLinkInfo>> ExecuteAsync(
        MediaLinkDraft draft, string? correlationId, CancellationToken ct = default)
    {
        if ((draft.ProductId is null) == (draft.SkuId is null))
        {
            return Result.Fail<MediaLinkInfo>(new(MediaErrorCodes.BothTargets, MediaErrorCodes.MessageAr(MediaErrorCodes.BothTargets)));
        }
        if (await repo.FindMediaAsync(draft.MediaId, ct) is null)
        {
            return Result.Fail<MediaLinkInfo>(new(MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound)));
        }

        MediaLinkInfo link;
        try
        {
            link = await repo.InsertLinkAsync(draft, ct);
        }
        catch (MediaRepositoryConflictException ex)
        {
            return Result.Fail<MediaLinkInfo>(new(ex.Code, ex.MessageAr));
        }

        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.product_images",
            EntityId: link.LinkId,
            Action: "MEDIA_LINK",
            Result: "SUCCESS",
            Reason: $"media={draft.MediaId} → {(draft.ProductId is { } p ? $"product:{p}" : $"sku:{draft.SkuId}")}{(draft.MakePrimary ? " PRIMARY" : "")}",
            CorrelationId: correlationId), ct);

        return Result.Ok(link);
    }
}

/// <summary>فك ربط — لا يمس الأصل إطلاقاً (§4.4 حـ: الإزالة لا تحذف الأصل).</summary>
public sealed class UnlinkMediaUseCase(IMediaRepository repo, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<long>> ExecuteAsync(long linkId, string? correlationId, CancellationToken ct = default)
    {
        var link = await repo.FindLinkAsync(linkId, ct);
        if (link is null)
        {
            return Result.Fail<long>(new(MediaErrorCodes.LinkNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.LinkNotFound)));
        }
        if (!await repo.DeleteLinkAsync(linkId, ct))
        {
            return Result.Fail<long>(new(MediaErrorCodes.LinkNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.LinkNotFound)));
        }

        var remaining = await repo.CountLinksAsync(link.MediaId, ct);
        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.product_images",
            EntityId: linkId,
            Action: "MEDIA_UNLINK",
            Result: "SUCCESS",
            Reason: $"media={link.MediaId} ← {(link.TargetType == "PRODUCT" ? $"product:{link.TargetId}" : $"sku:{link.TargetId}")} (بقيت {remaining} روابط للأصل)",
            CorrelationId: correlationId), ct);

        return Result.Ok(remaining);
    }
}

/// <summary>تعيين رئيسية — معاملة: تخلي باقي روابط الهدف ثم تعيّن هذه (فهرس V002 الفريد الجزئي هو الحارس الأخير).</summary>
public sealed class SetPrimaryLinkUseCase(IMediaRepository repo, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<MediaLinkInfo>> ExecuteAsync(long linkId, string? correlationId, CancellationToken ct = default)
    {
        var link = await repo.FindLinkAsync(linkId, ct);
        if (link is null)
        {
            return Result.Fail<MediaLinkInfo>(new(MediaErrorCodes.LinkNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.LinkNotFound)));
        }
        var affected = await repo.SetLinkPrimaryAsync(linkId, ct);
        if (affected == 0)
        {
            return Result.Fail<MediaLinkInfo>(new(MediaErrorCodes.LinkNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.LinkNotFound)));
        }

        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.product_images",
            EntityId: linkId,
            Action: "MEDIA_PRIMARY_CHANGE",
            Result: "SUCCESS",
            Reason: $"media={link.MediaId} صارت رئيسية {(link.TargetType == "PRODUCT" ? $"product:{link.TargetId}" : $"sku:{link.TargetId}")}",
            CorrelationId: correlationId), ct);

        return Result.Ok(link with { IsPrimary = true });
    }
}

/// <summary>حذف الأصل النهائي — عملية مستقلة تتحقق أولاً من صفر مراجع (§6.1:
/// «لا يحذف ملف الأصل عندما تُزال علاقته؛ الحذف النهائي يتحقق من عدم
/// وجود مراجع») — مربوط → 409 مع قائمة الروابط (هي «المراجعة» الإلزامية).
/// ترتيب الحذف: الصف أولاً ثم الملفات (فشل الملفات يترك ملفات يتيمة
/// غير مؤذية — موجهة بالمحتوى — لا صفاً يشير لمفقود).
/// </summary>
public sealed partial class DeleteMediaUseCase(
    IMediaRepository repo,
    IMediaStorage storage,
    IAuditWriter audit,
    ICurrentUser user,
    ILogger<DeleteMediaUseCase> logger)
{
    public async Task<Result<MediaDeleted>> ExecuteAsync(long mediaId, string? correlationId, CancellationToken ct = default)
    {
        var asset = await repo.FindMediaAsync(mediaId, ct);
        if (asset is null)
        {
            return Result.Fail<MediaDeleted>(new(MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound)));
        }

        var links = await repo.ListLinksAsync(mediaId, ct);
        if (links.Count > 0)
        {
            // الرفض يحمل الروابط كلها — المراجعة على المشغل (لا حذف تلقائي §6.2)
            return Result.Fail<MediaDeleted>(new(
                MediaErrorCodes.MediaInUse,
                MediaErrorCodes.MessageAr(MediaErrorCodes.MediaInUse) + $" ({links.Count} ربطاً)"));
        }

        if (!await repo.DeleteAssetAsync(mediaId, ct))
        {
            return Result.Fail<MediaDeleted>(new(MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound)));
        }

        // الصف حُذف — كنس ملفات التخزين بمفاتيح الصف المخزنة (أفضل جهد، بلا رمي:
        // الأيتام الموجهة بالمحتوى غير مؤذية — ولا يبقى صف يشير لمفقود)
        var keysToDelete = new[]
        {
            asset.StorageKey,
            asset.ThumbCatalogKey,
            asset.ThumbPosKey,
            asset.PreviewKey,
        };
        foreach (var key in keysToDelete.Where(k => k is not null).Select(k => k!).Concat(
            // احتياط حتمي إن كانت مفاتيح الصف ناقصة (لا يحدث بعد الرفع — صون للتوافق)
            BuildFallbackKeys(asset)))
        {
            try { await storage.DeleteAsync(key, CancellationToken.None); }
            catch (Exception ex) { LogStorageSweepWarning(ex, key); }
        }

        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.media_assets",
            EntityId: mediaId,
            Action: "MEDIA_DELETE",
            Result: "SUCCESS",
            Reason: $"sha={asset.Sha256[..12]}… ({asset.MimeType})",
            CorrelationId: correlationId), ct);

        return Result.Ok(new MediaDeleted(mediaId));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "تعذر كنس ملف تخزين بعد حذف الصف: {Key}")]
    private partial void LogStorageSweepWarning(Exception ex, string key);

    /// <summary>مفاتيح حتمية من البصمة والنوع — احتياط عند نقص مفاتيح الصف.</summary>
    private static IEnumerable<string> BuildFallbackKeys(MediaAssetInfo asset)
    {
        var ext = MediaPolicy.StorageExtension(asset.MimeType);
        yield return MediaStorageKeys.Original(asset.Sha256, ext);
        yield return MediaStorageKeys.ThumbCatalog(asset.Sha256, ext);
        yield return MediaStorageKeys.ThumbPos(asset.Sha256, ext);
        yield return MediaStorageKeys.Preview(asset.Sha256, ext);
    }
}

public sealed record MediaDeleted(long MediaId);

/// <summary>إعادة معالجة النسخ من الأصل (أداة المكتبة §6.2) — تحدّث المفاتيح وahash وprocessed_at.</summary>
public sealed class ReprocessThumbnailsUseCase(
    IMediaRepository repo,
    IMediaStorage storage,
    IMediaImageProcessor processor,
    IAuditWriter audit,
    ICurrentUser user,
    TimeProvider clock)
{
    public async Task<Result<MediaAssetInfo>> ExecuteAsync(long mediaId, string? correlationId, CancellationToken ct = default)
    {
        var asset = await repo.FindMediaAsync(mediaId, ct);
        if (asset is null)
        {
            return Result.Fail<MediaAssetInfo>(new(MediaErrorCodes.MediaNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.MediaNotFound)));
        }

        ProcessedImageVariants variants;
        try
        {
            await using var original = await storage.OpenReadAsync(MediaStorageKeys.Original(asset.Sha256, MediaPolicy.StorageExtension(asset.MimeType)), ct);
            variants = processor.Process(original, asset.MimeType, asset.WidthPx, asset.HeightPx);
        }
        catch (MediaProcessingException)
        {
            return Result.Fail<MediaAssetInfo>(new(MediaErrorCodes.CorruptImage, MediaErrorCodes.MessageAr(MediaErrorCodes.CorruptImage)));
        }

        var vext = variants.ThumbCatalogExt;
        var thumbCatalogKey = MediaStorageKeys.ThumbCatalog(asset.Sha256, vext);
        var thumbPosKey = MediaStorageKeys.ThumbPos(asset.Sha256, vext);
        var previewKey = MediaStorageKeys.Preview(asset.Sha256, vext);

        await storage.WriteAsync(thumbCatalogKey, new MemoryStream(variants.ThumbCatalog), ct);
        await storage.WriteAsync(thumbPosKey, new MemoryStream(variants.ThumbPos), ct);
        await storage.WriteAsync(previewKey, new MemoryStream(variants.Preview), ct);

        var ahash = AverageHash.Compute(variants.Gray8x8);
        var processedAt = clock.GetUtcNow();
        await repo.UpdateAssetVariantsAsync(mediaId, new MediaVariantsUpdate(thumbCatalogKey, thumbPosKey, previewKey, ahash, processedAt), ct);

        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.media_assets",
            EntityId: mediaId,
            Action: "MEDIA_REPROCESS",
            Result: "SUCCESS",
            Reason: "إعادة توليد thumb-catalog/thumb-pos/preview + ahash",
            CorrelationId: correlationId), ct);

        return Result.Ok(asset with { HasThumbCatalog = true, HasThumbPos = true, HasPreview = true, Ahash = ahash, ProcessedAt = processedAt });
    }
}

/// <summary>تقرير المكررات: مطابق تماماً (من مهام الاستيراد) + المشتبه بصرياً (ahash) — عرض فقط، لا دمج/حذف (§6.2).</summary>
public sealed class GetDuplicatesReportUseCase(IMediaRepository repo)
{
    private const int MaxVisualPairsInResponse = 500;

    public async Task<DuplicatesReport> ExecuteAsync(int? page, CancellationToken ct = default)
    {
        var normalizedPage = Math.Max(1, page ?? 1);
        var (encounters, totalEncounters) = await repo.ListExactEncountersAsync(normalizedPage, 50, ct);

        var hashes = await repo.ListAhashesAsync(ct);
        var pairs = AverageHash.FindSuspectedPairs(
            [.. hashes.Select(h => (h.Id, h.Ahash))]);

        return new DuplicatesReport(
            Exact: new(encounters, totalEncounters, normalizedPage),
            VisualSuspects: new(
                [.. pairs.Take(MaxVisualPairsInResponse).Select(p => new VisualSuspectPair(p.FirstId, p.SecondId, p.Distance))],
                pairs.Count,
                1));
    }
}

public sealed record DuplicatesReport(ExactEncounters Exact, VisualSuspectsSection VisualSuspects);

public sealed record ExactEncounters(IReadOnlyList<MediaDuplicateEncounter> Items, long TotalCount, int Page);

public sealed record VisualSuspectsSection(IReadOnlyList<VisualSuspectPair> Items, long TotalCount, int Page);

public sealed record VisualSuspectPair(long FirstMediaId, long SecondMediaId, int AhashDistance);

/// <summary>الصورة الافتراضية لصيغة بيع (تغذي POS وشبكة الكتالوج §6.3) — أولوية واضحة من المجال.</summary>
public sealed class GetSkuDefaultImageUseCase(IMediaRepository repo)
{
    public async Task<Result<SkuDefaultImage>> ExecuteAsync(long skuId, CancellationToken ct = default)
    {
        var snapshot = await repo.LoadSkuImageLinksAsync(skuId, ct);
        if (snapshot is null)
        {
            return Result.Fail<SkuDefaultImage>(new(MediaErrorCodes.TargetNotFound, "صيغة البيع غير موجودة"));
        }

        var choice = DefaultImagePriority.Pick(
            [.. snapshot.SkuLevel.Select(ToCandidate)],
            [.. snapshot.ProductLevel.Select(ToCandidate)]);

        return Result.Ok(new SkuDefaultImage(
            snapshot.SkuId,
            snapshot.SkuCode,
            SourceAr: choice.SourceKind switch
            {
                DefaultImagePriority.Source.Sku => "SKU",
                DefaultImagePriority.Source.Product => "PRODUCT",
                _ => "NONE",
            },
            MediaId: choice.SourceKind == DefaultImagePriority.Source.None ? null : choice.MediaId,
            HasThumbPos: choice.HasThumbPos));

        static DefaultImagePriority.Candidate ToCandidate(MediaLinkInfo l) =>
            new(l.MediaId, l.IsPrimary, l.SortOrder, l.LinkId, l.HasThumbPos);
    }
}

public sealed record SkuDefaultImage(long SkuId, string SkuCode, string SourceAr, long? MediaId, bool HasThumbPos);

/// <summary>خرق تفرّد من مستودع الوسائط (ازدواج ربط/سباق فهرس) — 409 برمز معلوم.</summary>
public sealed class MediaRepositoryConflictException(string code, string messageAr) : Exception(messageAr)
{
    public string Code { get; } = code;
    public string MessageAr { get; } = messageAr;
}
