namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Media;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Media;
using WeraPro.Domain.Security;

/// <summary>
/// نقاط نهاية الوسائط (G5 — §6): رفع/بحث/نسخ/ربط/فك ربط/رئيسية/حذف محمي/
/// إعادة معالجة/تقرير المكررات + الصورة الافتراضية للصيغة (تغذي POS والكتالوج)
/// + مهام الاستيراد الدفعي (إلغاء آمن واستئناف وتقرير لكل ملف).
/// الصلاحيات ذرية خادمية: القراءة لكل الأدوار (V016)، والإدارة والإستيراد لمن يملكها.
/// </summary>
public static class MediaEndpoints
{
    private const int MaxSingleUploadBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/media").WithTags("Media")
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── رفع أصل واحد (multipart) ────────────────────────────────────────
        group.MapPost("/", async (
            HttpRequest http,
            [FromServices] UploadMediaUseCase upload,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "طلب رفع غير صالح", "المحتوى يجب أن يكون multipart/form-data");
            }
            var form = await http.ReadFormAsync(ct);
            IFormFile? file = null;
            for (var i = 0; i < form.Files.Count; i++)
            {
                if (form.Files[i].Name == "file")
                {
                    file = form.Files[i];
                    break;
                }
            }
            file ??= form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "طلب رفع غير صالح", "حقل «file» مفقود أو فارغ");
            }
            if (file.Length > MaxSingleUploadBytes)
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "تعذر الرفع",
                    MediaErrorCodes.MessageAr(MediaErrorCodes.TooLarge), MediaErrorCodes.TooLarge);
            }

            // ننسخ إلى تيار مملوك قبل أي عمل: تيارات IFormFile مرتبطة بدورة حياة
            // الطلب وتُتلف قبيل اكتمال المعالجة — التيار المملوك هو عقد الاستخدام
            // (ملف واحد محدود بـ 10MB — نسخ مقنّن).
            await using var requestStream = file.OpenReadStream();
            using var owned = new MemoryStream(capacity: (int)file.Length);
            await requestStream.CopyToAsync(owned, 64 * 1024, ct);
            owned.Position = 0;

            var result = await upload.ExecuteAsync(owned, file.FileName, ctx.GetCorrelationId(), ct);
            if (result.IsSuccess && result.Value is { Created: true, New: { } created })
            {
                var dto = ToDto(created);
                return Results.Created($"/api/v1/media/{dto.MediaId}", new UploadMediaResponse(true, null, dto));
            }
            if (result.IsSuccess && result.Value is { Existing: { } existing })
            {
                // مطابق تماماً: 409 مع الإشارة للموجود كاملاً — يُربط الموجود ولا يُنشأ ثانٍ
                return Results.Conflict(new UploadMediaResponse(false, existing.MediaId, ToDto(existing)));
            }
            var status = result.Error!.Code switch
            {
                MediaErrorCodes.ExactDuplicate => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Problem(ctx, status, "تعذر الرفع", result.Error.MessageAr, result.Error.Code);
        }).DisableAntiforgery().RequireAuthorization(PermissionCodes.CatalogMediaManage);

        // ── بحث المكتبة ─────────────────────────────────────────────────────
        group.MapGet("/", async (
            string? sha, string? mime, string? linkState, int? page, int? pageSize,
            [FromServices] SearchMediaUseCase search,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await search.ExecuteAsync(sha, mime, linkState, page, pageSize, ct);
            if (!result.IsSuccess)
            {
                return Problem(http, StatusCodes.Status400BadRequest, "طلب بحث غير صالح", result.Error!.MessageAr);
            }
            var outcome = result.Value!;
            return Results.Ok(new MediaSearchResponse(
                outcome.Page, outcome.PageSize, outcome.TotalCount, outcome.TotalPages,
                [.. outcome.Items.Select(ToDto)]));
        }).RequireAuthorization(PermissionCodes.CatalogMediaRead);

        // ── تفاصيل أصل + روابطه ─────────────────────────────────────────────
        group.MapGet("/{mediaId:long}", async (
            long mediaId,
            [FromServices] GetMediaDetailsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(mediaId, ct);
            return result.IsSuccess
                ? Results.Ok(ToDetails(result.Value!))
                : Problem(http, StatusCodes.Status404NotFound, "الأصل غير موجود", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogMediaRead).WithName("GetMediaById");

        // ── بايتات نسخة (الأصل/المصغرات/المعاينة) ───────────────────────────
        group.MapGet("/{mediaId:long}/content/{variant}", async (
            long mediaId,
            string variant,
            [FromServices] ServeMediaVariantUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(mediaId, variant, ct);
            if (!result.IsSuccess)
            {
                var status = result.Error!.Code == MediaErrorCodes.MediaNotFound || result.Error.Code == MediaErrorCodes.VariantNotReady
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Problem(http, status, "تعذر جلب النسخة", result.Error.MessageAr, result.Error.Code);
            }
            var media = result.Value!;
            // ETag من البصمة والنسخة: تخزين مؤقت آمن للعميل بلا حمولة إضافية
            http.Response.Headers.ETag = $"\"{media.Sha256[..16]}-{variant}\"";
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Stream(media.Content, media.MimeType);
        }).RequireAuthorization(PermissionCodes.CatalogMediaRead);

        // ── روابط أصل (بيانات مراجعة الحذف) ─────────────────────────────────
        group.MapGet("/{mediaId:long}/links", async (
            long mediaId,
            [FromServices] GetMediaDetailsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(mediaId, ct);
            return result.IsSuccess
                ? Results.Ok(ToDetails(result.Value!).Links)
                : Problem(http, StatusCodes.Status404NotFound, "الأصل غير موجود", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogMediaRead);

        // ── تقرير المكررات (عرض فقط — لا دمج/حذف تلقائي §6.2) ───────────────
        group.MapGet("/duplicates", async (
            int? page,
            [FromServices] GetDuplicatesReportUseCase useCase,
            CancellationToken ct) =>
        {
            var report = await useCase.ExecuteAsync(page, ct);
            return Results.Ok(new MediaDuplicatesReportResponse(
                new MediaExactEncountersDto(
                    report.Exact.TotalCount, report.Exact.Page,
                    [.. report.Exact.Items.Select(e => new MediaDuplicateEncounterDto(
                        e.FileId, e.JobId, e.JobLabel, e.ClientName, e.Sha256, e.ExistingMediaId, e.ProcessedAt))]),
                new MediaVisualSuspectsDto(
                    report.VisualSuspects.TotalCount,
                    [.. report.VisualSuspects.Items.Select(p => new MediaVisualSuspectPairDto(p.FirstMediaId, p.SecondMediaId, p.AhashDistance))])));
        }).RequireAuthorization(PermissionCodes.CatalogMediaRead);

        // ── ربط صورة بهدف واحد ──────────────────────────────────────────────
        group.MapPost("/{mediaId:long}/links", async (
            long mediaId,
            [FromBody] LinkMediaRequest request,
            [FromServices] LinkMediaUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(
                new MediaLinkDraft(mediaId, request.ProductId, request.SkuId, request.MakePrimary),
                http.GetCorrelationId(), ct);
            if (result.IsSuccess)
            {
                return Results.Created($"/api/v1/media/links/{result.Value!.LinkId}", ToLinkDto(result.Value));
            }
            var status = result.Error!.Code switch
            {
                MediaErrorCodes.LinkExists or MediaErrorCodes.ExactDuplicate => StatusCodes.Status409Conflict,
                MediaErrorCodes.MediaNotFound or MediaErrorCodes.TargetNotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest,
            };
            return Problem(http, status, "تعذر الربط", result.Error.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CatalogMediaManage);

        // ── فك ربط (لا يمس الأصل) ───────────────────────────────────────────
        group.MapDelete("/links/{linkId:long}", async (
            long linkId,
            [FromServices] UnlinkMediaUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(linkId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { linkId, remainingLinksForAsset = result.Value })
                : Problem(http, StatusCodes.Status404NotFound, "تعذر فك الربط", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogMediaManage);

        // ── تعيين رئيسية ────────────────────────────────────────────────────
        group.MapPost("/links/{linkId:long}/set-primary", async (
            long linkId,
            [FromServices] SetPrimaryLinkUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(linkId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(ToLinkDto(result.Value!))
                : Problem(http, StatusCodes.Status404NotFound, "تعذر تعيين الرئيسية", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogMediaManage);

        // ── حذف أصل نهائي (محمي بالمراجعة: يُرفض ما دام مربوطاً) ────────────
        group.MapDelete("/{mediaId:long}", async (
            long mediaId,
            [FromServices] DeleteMediaUseCase useCase,
            [FromServices] GetMediaDetailsUseCase details,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(mediaId, http.GetCorrelationId(), ct);
            if (result.IsSuccess)
            {
                return Results.NoContent();
            }
            if (result.Error!.Code == MediaErrorCodes.MediaInUse)
            {
                // الرفض يحمل قائمة الروابط كلها — هذه هي «المراجعة» (لا حذف بلا فك صريح)
                var inUse = await details.ExecuteAsync(mediaId, ct);
                return Results.Conflict(new
                {
                    type = "https://wera.example/errors/media_in_use",
                    title = "الأصل مستخدم — لا يُحذف دون مراجعة",
                    status = 409,
                    detail = result.Error.MessageAr,
                    correlationId = http.GetCorrelationId(),
                    references = inUse.IsSuccess
                        ? ToDetails(inUse.Value!).Links.ToList()
                        : new List<MediaLinkDto>(),
                });
            }
            return Problem(http, StatusCodes.Status404NotFound, "تعذر الحذف", result.Error.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogMediaManage);

        // ── إعادة معالجة النسخ ──────────────────────────────────────────────
        group.MapPost("/{mediaId:long}/reprocess-thumbnails", async (
            long mediaId,
            [FromServices] ReprocessThumbnailsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(mediaId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(ToDto(result.Value!))
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إعادة المعالجة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CatalogMediaManage);

        MapImportEndpoints(group);

        return app;
    }

    private static void MapImportEndpoints(RouteGroupBuilder group)
    {
        var jobs = group.MapGroup("/import-jobs").WithTags("MediaImport");

        // ── إنشاء مهمة ─────────────────────────────────────────────────────
        jobs.MapPost("/", async (
            [FromBody] CreateImportJobRequest request,
            [FromServices] CreateImportJobUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request.Label, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/media/import-jobs/{result.Value!.JobId}", ToJobDto(result.Value, null))
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء المهمة", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogImportRun);

        // ── حالة المهمة (أعداد من استعلام فعلي) ─────────────────────────────
        jobs.MapGet("/{jobId:long}", async (
            long jobId,
            [FromServices] GetImportJobUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(jobId, ct);
            return result.IsSuccess
                ? Results.Ok(ToJobDto(result.Value!.Job, result.Value.Counts))
                : Problem(http, StatusCodes.Status404NotFound, "المهمة غير موجودة", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogImportRun).WithName("GetImportJobById");

        // ── إضافة ملفات (multipart متعدد) ───────────────────────────────────
        jobs.MapPost("/{jobId:long}/files", async (
            long jobId,
            HttpRequest http,
            [FromServices] AddImportFilesUseCase useCase,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "طلب إضافة غير صالح", "المحتوى يجب أن يكون multipart/form-data");
            }
            var form = await http.ReadFormAsync(ct);
            var files = new List<(string, Stream)>();
            var rejectedOversize = new List<string>();
            foreach (var f in form.Files)
            {
                if (f.Length == 0)
                {
                    continue;
                }
                if (f.Length > MaxSingleUploadBytes)
                {
                    rejectedOversize.Add(f.FileName);
                    continue;
                }
                // تيار مملوك لكل ملف — تيارات IFormFile تُتلف بدورة حياة الطلب
                await using var part = f.OpenReadStream();
                var owned = new MemoryStream(capacity: (int)f.Length);
                await part.CopyToAsync(owned, 64 * 1024, ct);
                owned.Position = 0;
                files.Add((f.FileName, owned));
            }
            if (files.Count == 0)
            {
                return Problem(ctx, StatusCodes.Status400BadRequest, "طلب إضافة غير صالح",
                    rejectedOversize.Count > 0
                        ? "كل الملفات فوق الحد (10MB): " + string.Join("، ", rejectedOversize)
                        : "لا ملفات صالحة في الطلب");
            }

            try
            {
                var result = await useCase.ExecuteAsync(jobId, files, ctx.GetCorrelationId(), ct);
                if (!result.IsSuccess)
                {
                    var status = result.Error!.Code switch
                    {
                        MediaErrorCodes.JobNotFound => StatusCodes.Status404NotFound,
                        MediaErrorCodes.InvalidJobState => StatusCodes.Status409Conflict,
                        _ => StatusCodes.Status400BadRequest,
                    };
                    return Problem(ctx, status, "تعذر إضافة الملفات", result.Error.MessageAr, result.Error.Code);
                }
                return Results.Ok(new ImportAddFilesResponse(jobId,
                    [.. result.Value!.Rows.Select(ToFileDto)]));
            }
            finally
            {
                foreach (var (_, stream) in files)
                {
                    await stream.DisposeAsync();
                }
            }
        }).DisableAntiforgery().RequireAuthorization(PermissionCodes.CatalogImportRun);

        // ── تشغيل (OPEN/CANCELLED → RUNNING — الاستئناف) ─────────────────────
        jobs.MapPost("/{jobId:long}/start", async (
            long jobId,
            [FromServices] StartImportJobUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(jobId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(ToJobDto(result.Value!.Job, result.Value.Counts))
                : Problem(http, StatusCodes.Status409Conflict, "تعذر التشغيل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CatalogImportRun);

        // ── إلغاء آمن ───────────────────────────────────────────────────────
        jobs.MapPost("/{jobId:long}/cancel", async (
            long jobId,
            [FromServices] CancelImportJobUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(jobId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(ToJobDto(result.Value!.Job, result.Value.Counts))
                : Problem(http, StatusCodes.Status409Conflict, "تعذر الإلغاء", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CatalogImportRun);

        // ── تقرير الملفات (مرقّم — حالة لكل ملف) ────────────────────────────
        jobs.MapGet("/{jobId:long}/files", async (
            long jobId,
            string? state, int? page, int? pageSize,
            [FromServices] ListImportFilesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(jobId, state, page, pageSize, ct);
            if (!result.IsSuccess)
            {
                return Problem(http, StatusCodes.Status400BadRequest, "طلب غير صالح", result.Error!.MessageAr);
            }
            var p = result.Value!;
            return Results.Ok(new ImportFilesPageResponse(
                p.Page, p.PageSize, p.TotalCount, [.. p.Items.Select(ToFileDto)]));
        }).RequireAuthorization(PermissionCodes.CatalogImportRun);

        // ── ملخص التنفيذ ────────────────────────────────────────────────────
        jobs.MapGet("/{jobId:long}/report", async (
            long jobId,
            [FromServices] GetImportReportUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(jobId, ct);
            return result.IsSuccess
                ? Results.Ok(new ImportReportResponse(ToJobDto(result.Value!.Job, result.Value.Counts), result.Value.ErrorsByCode))
                : Problem(http, StatusCodes.Status404NotFound, "المهمة غير موجودة", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogImportRun);
    }

    // ── التحويلات ────────────────────────────────────────────────────────────

    private static MediaDto ToDto(MediaAssetInfo m) => new(
        m.MediaId, m.Sha256, m.MimeType, m.WidthPx, m.HeightPx, m.SizeBytes,
        m.HasThumbCatalog, m.HasThumbPos, m.HasPreview, m.LinkCount, m.UploadedBy, m.CreatedAt);

    private static MediaLinkDto ToLinkDto(MediaLinkInfo l) => new(
        l.LinkId, l.MediaId, l.TargetType, l.TargetId, l.TargetCode, l.TargetNameAr, l.IsPrimary, l.SortOrder, l.HasThumbPos);

    private static MediaDetailsResponse ToDetails(MediaDetails d) => new(
        ToDto(d.Asset), [.. d.Links.Select(ToLinkDto)]);

    private static ImportJobDto ToJobDto(MediaImportJobInfo job, MediaImportCounts? counts)
    {
        var c = counts ?? new MediaImportCounts(0, 0, 0, 0, 0);
        return new ImportJobDto(
            job.JobId, job.Label, job.State, job.StorageScope, job.CreatedAt, job.StartedAt, job.FinishedAt,
            new ImportCountsDto(c.Pending, c.Processing, c.Done, c.Failed, c.Skipped));
    }

    private static ImportFileDto ToFileDto(ImportFileRowInfo f) => new(
        f.FileId, f.Ordinal, f.ClientName, f.SizeBytes, f.State, f.Sha256, f.ErrorCode, f.SkipReason, f.MediaId, f.ProcessedAt);

    private static IResult Problem(
        HttpContext http, int status, string title, string detail, string? code = null) =>
        Results.Json(
            new ApiProblem(
                Type: "https://wera.example/errors/" + (code ?? "media").ToLowerInvariant(),
                Title: title,
                Status: status,
                Detail: detail,
                CorrelationId: http.GetCorrelationId(),
                OccurredAtUtc: DateTimeOffset.UtcNow),
            statusCode: status,
            contentType: "application/problem+json");
}
