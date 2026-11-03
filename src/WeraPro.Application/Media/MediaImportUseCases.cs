namespace WeraPro.Application.Media;

using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Media;
using WeraPro.Domain.SharedKernel;

/// <summary>إنشاء مهمة استيراد (OPEN) — الملفات تُضاف ثم يُبدأ التشغيل. تُختَم ببصمة نطاق تخزين هذا المثيل فلا يعالجها إلا من يملك جذر بايتاتها.</summary>
public sealed class CreateImportJobUseCase(IMediaImportRepository repo, IMediaStorage storage, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<MediaImportJobInfo>> ExecuteAsync(string label, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length is < 3 or > 120)
        {
            return Result.Fail<MediaImportJobInfo>(new("LABEL_INVALID", "اسم المهمة بين 3 و120 محرفاً"));
        }
        var jobId = await repo.CreateJobAsync(label.Trim(), user.IsAuthenticated ? user.UserId : 0, storage.StorageScopeId, ct);
        var job = await repo.FindJobAsync(jobId, ct);

        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.media_import_jobs",
            EntityId: jobId,
            Action: "MEDIA_IMPORT_JOB_CREATE",
            Result: "SUCCESS",
            Reason: $"label={job!.Label}",
            CorrelationId: correlationId), ct);

        return Result.Ok(job!);
    }
}

/// <summary>حالة المهمة + الأعداد من استعلام فعلي (لا عداد تراكمي — §6.1).</summary>
public sealed class GetImportJobUseCase(IMediaImportRepository repo)
{
    public async Task<Result<ImportJobStatus>> ExecuteAsync(long jobId, CancellationToken ct = default)
    {
        var job = await repo.FindJobAsync(jobId, ct);
        if (job is null)
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.JobNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.JobNotFound)));
        }
        var counts = await repo.CountByStateAsync(jobId, ct);
        return Result.Ok(new ImportJobStatus(job, counts));
    }
}

public sealed record ImportJobStatus(MediaImportJobInfo Job, MediaImportCounts Counts);

/// <summary>
/// إضافة ملفات إلى المهمة (مهمة OPEN أو CANCELLED): لكل ملف — ترتيب ←
/// تحقق ترويسة؛ الرافض يُسجل FAILED عند الإضافة (بلا تخزين مؤقت)، والسليم
/// يُرحَّل إلى staging ثم يسجل PENDING. اسم العميل يُنقّى (للعرض فقط).
/// </summary>
public sealed class AddImportFilesUseCase(
    IMediaImportRepository repo,
    IMediaStorage storage,
    IAuditWriter audit,
    ICurrentUser user)
{
    public async Task<Result<ImportAddOutcome>> ExecuteAsync(
        long jobId,
        IReadOnlyList<(string ClientName, Stream Content)> files,
        string? correlationId,
        CancellationToken ct = default)
    {
        var job = await repo.FindJobAsync(jobId, ct);
        if (job is null)
        {
            return Result.Fail<ImportAddOutcome>(new(MediaErrorCodes.JobNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.JobNotFound)));
        }
        if (job.State is not ("OPEN" or "CANCELLED"))
        {
            return Result.Fail<ImportAddOutcome>(new(MediaErrorCodes.InvalidJobState, "تُضاف الملفات لمهمة OPEN أو CANCELLED فقط — أنشئ مهمة جديدة"));
        }
        if (files.Count is 0 or > 500)
        {
            return Result.Fail<ImportAddOutcome>(new("BATCH_INVALID", "دفعة الإضافة بين 1 و500 ملف"));
        }

        var rows = new List<ImportFileRowInfo>(files.Count);
        foreach (var (rawName, content) in files)
        {
            var clientName = Sanitize(rawName);
            var ordinal = await repo.NextOrdinalAsync(jobId, ct);

            if (!content.CanSeek || content.Length == 0 || content.Length > MediaPolicy.MaxFileBytes)
            {
                rows.Add(await repo.AddFileAsync(jobId,
                    new ImportStagedFile(ordinal, clientName, null, content.Length, "FAILED", MediaErrorCodes.TooLarge), ct));
                continue;
            }

            content.Seek(0, SeekOrigin.Begin);
            var sniff = ImageHeaderSniffer.Sniff(content);
            if (sniff.Kind is not MediaSniffResultKind.Ok)
            {
                var code = sniff.Kind switch
                {
                    MediaSniffResultKind.UnsupportedMediaType => MediaErrorCodes.UnsupportedMediaType,
                    MediaSniffResultKind.DimensionsExceedLimit => MediaErrorCodes.DimensionsExceedLimit,
                    _ => MediaErrorCodes.CorruptImage,
                };
                rows.Add(await repo.AddFileAsync(jobId,
                    new ImportStagedFile(ordinal, clientName, null, content.Length, "FAILED", code), ct));
                continue;
            }

            var stagingKey = MediaStorageKeys.Staging(jobId, ordinal);
            content.Seek(0, SeekOrigin.Begin);
            await storage.WriteAsync(stagingKey, content, ct);
            rows.Add(await repo.AddFileAsync(jobId,
                new ImportStagedFile(ordinal, clientName, stagingKey, content.Length, "PENDING", null), ct));
        }

        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.media_import_jobs",
            EntityId: jobId,
            Action: "MEDIA_IMPORT_ADD",
            Result: "SUCCESS",
            Reason: $"أُضيف {rows.Count} ملفاً ({rows.Count(r => r.State == "FAILED")} رُفض عند الإضافة)",
            CorrelationId: correlationId), ct);

        return Result.Ok(new ImportAddOutcome(jobId, rows));
    }

    /// <summary>تنقية اسم العميل للعرض فقط — بلا مسارات/فواصل أبداً (حارس path traversal §6.1).</summary>
    private static string Sanitize(string raw)
    {
        var name = (raw ?? "file").Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            name = name[(lastSlash + 1)..];
        }
        name = name.Trim();
        if (name.Length == 0)
        {
            name = "file";
        }
        return name.Length > 200 ? name[..200] : name;
    }
}

public sealed record ImportAddOutcome(long JobId, IReadOnlyList<ImportFileRowInfo> Rows);

/// <summary>تشغيل المهمة (OPEN|CANCELLED → RUNNING) مع إعادة إدراج PROCESSING المعلقة (تعافي الانهيار).</summary>
public sealed class StartImportJobUseCase(IMediaImportRepository repo, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<ImportJobStatus>> ExecuteAsync(long jobId, string? correlationId, CancellationToken ct = default)
    {
        var job = await repo.FindJobAsync(jobId, ct);
        if (job is null)
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.JobNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.JobNotFound)));
        }
        if (job.State is not ("OPEN" or "CANCELLED"))
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.InvalidJobState,
                "التشغيل من OPEN أو CANCELLED فقط (الاستئناف) — RUNNING بالفعل أو مكتملة"));
        }
        if (!await repo.TryStartJobAsync(jobId, ct))
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.InvalidJobState, "فشل الانتقال إلى RUNNING (تزاحم) — أعد المحاولة"));
        }
        var counts = await repo.CountByStateAsync(jobId, ct);
        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.media_import_jobs",
            EntityId: jobId,
            Action: "MEDIA_IMPORT_START",
            Result: "SUCCESS",
            Reason: job.State == "CANCELLED" ? "استئناف بعد إلغاء" : "بدء تشغيل",
            CorrelationId: correlationId), ct);
        var updated = await repo.FindJobAsync(jobId, ct);
        return Result.Ok(new ImportJobStatus(updated!, counts));
    }
}

/// <summary>إلغاء آمن: يتوقف الالتقاط الجديد — الملف قيد المعالجة يُكمل ثم تتوقف (لا يُلقَ الملف).</summary>
public sealed class CancelImportJobUseCase(IMediaImportRepository repo, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<ImportJobStatus>> ExecuteAsync(long jobId, string? correlationId, CancellationToken ct = default)
    {
        var job = await repo.FindJobAsync(jobId, ct);
        if (job is null)
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.JobNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.JobNotFound)));
        }
        if (job.State is not ("RUNNING" or "OPEN"))
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.InvalidJobState, "الإلغاء من RUNNING أو OPEN فقط"));
        }
        if (!await repo.TryCancelJobAsync(jobId, ct))
        {
            return Result.Fail<ImportJobStatus>(new(MediaErrorCodes.InvalidJobState, "فشل الانتقال إلى CANCELLED (تزاحم)"));
        }
        var counts = await repo.CountByStateAsync(jobId, ct);
        await audit.WriteAsync(new AuditEntry(
            UserId: user.IsAuthenticated ? user.UserId : null,
            RoleCode: null,
            StationId: user.StationId,
            BranchId: user.BranchId,
            EntitySchema: "catalog.media_import_jobs",
            EntityId: jobId,
            Action: "MEDIA_IMPORT_CANCEL",
            Result: "SUCCESS",
            Reason: "إلغاء آمن — المتبقي يبقى PENDING والاستئناف متاح",
            CorrelationId: correlationId), ct);
        var updated = await repo.FindJobAsync(jobId, ct);
        return Result.Ok(new ImportJobStatus(updated!, counts));
    }
}

/// <summary>تقرير الملفات (مرقّم خادمياً) — فلتر الحالة اختياري.</summary>
public sealed class ListImportFilesUseCase(IMediaImportRepository repo)
{
    public async Task<Result<ImportFilesPage>> ExecuteAsync(long jobId, string? state, int? page, int? pageSize, CancellationToken ct = default)
    {
        if (state is not null && state.ToUpperInvariant() is not ("PENDING" or "PROCESSING" or "DONE" or "FAILED" or "SKIPPED"))
        {
            return Result.Fail<ImportFilesPage>(new("STATE_INVALID", "الحالة: PENDING/PROCESSING/DONE/FAILED/SKIPPED"));
        }
        var normalizedPage = Math.Max(1, page ?? 1);
        var normalizedSize = Math.Clamp(pageSize ?? 50, 1, 200);
        var (items, total) = await repo.ListFilesAsync(jobId, state?.ToUpperInvariant(), normalizedPage, normalizedSize, ct);
        return Result.Ok(new ImportFilesPage(items, normalizedPage, normalizedSize, total));
    }
}

public sealed record ImportFilesPage(IReadOnlyList<ImportFileRowInfo> Items, int Page, int PageSize, long TotalCount);

/// <summary>ملخص تنفيذ المهمة: أعداد + histogram الأخطاء (تقرير لكل ملف §6.1).</summary>
public sealed class GetImportReportUseCase(IMediaImportRepository repo)
{
    public async Task<Result<ImportReport>> ExecuteAsync(long jobId, CancellationToken ct = default)
    {
        var job = await repo.FindJobAsync(jobId, ct);
        if (job is null)
        {
            return Result.Fail<ImportReport>(new(MediaErrorCodes.JobNotFound, MediaErrorCodes.MessageAr(MediaErrorCodes.JobNotFound)));
        }
        var counts = await repo.CountByStateAsync(jobId, ct);
        var errors = await repo.ErrorHistogramAsync(jobId, ct);
        return Result.Ok(new ImportReport(job, counts, errors));
    }
}

public sealed record ImportReport(MediaImportJobInfo Job, MediaImportCounts Counts, IReadOnlyDictionary<string, int> ErrorsByCode);

/// <summary>
/// معالج ملف واحد من مهمة استيراد (يستدعيه العامل الخلفي — ملف واحد في
/// اللحظة، بلا أي توازٍ داخل المهمة): قراءة staging → فك → بصمة → فحص
/// التكرر → كتابة → إقفال (صف الملف + صف الأصل في معاملة واحدة).
/// إقفال المهمة (COMPLETED + كنس staging) عند نفاد العمل.
/// </summary>
public sealed partial class MediaImportFileProcessor(
    IMediaRepository media,
    IMediaImportRepository imports,
    IMediaStorage storage,
    IMediaImageProcessor processor,
    IAuditWriter audit,
    ILogger<MediaImportFileProcessor> logger)
{
    /// <summary>دورة عمل واحدة — true إن عالجت ملفاً (يُستدعى فوراً مجدداً).</summary>
    public async Task<bool> RunOneCycleAsync(CancellationToken ct = default)
    {
        // مهمات نطاق هذا الجذر حصراً: لا يُلتقط ملفٌ بايتاته المؤقتة خارج الجذر
        // (مثيلات متعددة على قاعدة واحدة بأقراص مختلفة — سلامة المعالجة الموزعة)
        var jobIds = await imports.ListRunningJobIdsAsync(storage.StorageScopeId, ct);
        foreach (var jobId in jobIds)
        {
            // تعافي الانهيار أولاً: PROCESSING المعلقة من إقلاع سابق تعود PENDING
            // (صفر في التشغيل الطبيعي — لا يوازي هذا العامل أحد داخل المهمة)
            var healed = await imports.RequeueStaleProcessingAsync(jobId, ct);

            // الإلغاء بين الملفات: يفحص حالة المهمة قبل كل التقاط
            if (!await imports.HasRemainingWorkAsync(jobId, ct))
            {
                await FinalizeJobAsync(jobId, ct);
                continue;
            }
            var picked = await imports.TryPickNextPendingAsync(jobId, ct);
            if (picked is null)
            {
                continue;
            }

            // أعِد فحص الحالة بعد الالتقاط: لو أُلغيت أثناء الالتقاط نفسه، أعِد الملف PENDING
            var job = await imports.FindJobAsync(jobId, ct);
            if (job is not { State: "RUNNING" })
            {
                // إعادة الملف الملتقط إلى PENDING (لم يبدأ عمل فعلي)
                await imports.FinalizeFileAsync(new ImportFileOutcome(
                    picked.FileId, "PENDING", null, null, null, null, null), ct);
                continue;
            }

            await ProcessPickedFileAsync(job, picked, ct);
            return true; // ملف واحد في الدورة
        }
        return false;
    }

    private async Task ProcessPickedFileAsync(MediaImportJobInfo job, ImportFileRowInfo file, CancellationToken ct)
    {
        try
        {
            await using var staged = await storage.OpenReadAsync(file.StagingKey!, ct);

            var sniff = ImageHeaderSniffer.Sniff(staged);
            if (sniff.Kind is not MediaSniffResultKind.Ok)
            {
                var code = sniff.Kind switch
                {
                    MediaSniffResultKind.UnsupportedMediaType => MediaErrorCodes.UnsupportedMediaType,
                    MediaSniffResultKind.DimensionsExceedLimit => MediaErrorCodes.DimensionsExceedLimit,
                    _ => MediaErrorCodes.CorruptImage,
                };
                await imports.FinalizeFileAsync(Failed(file, code, null), ct);
                return;
            }

            var mime = sniff.MimeType!;
            var width = sniff.Width!.Value;
            var height = sniff.Height!.Value;

            staged.Seek(0, SeekOrigin.Begin);
            var sha = await MediaHasher.ComputeHexAsync(staged, ct);

            // فحص التكرر (مطابق تماماً): أصل قائم — أو ملف سابق من نفس المهمة (أُدرج أصلاً)
            if (await media.FindByShaAsync(sha, ct) is { } existing)
            {
                await imports.FinalizeFileAsync(new ImportFileOutcome(
                    file.FileId, "SKIPPED", null, sha, null, $"EXACT_DUPLICATE_OF:{existing.MediaId}", null), ct);
                return;
            }

            if (storage.GetAvailableFreeBytes() is { } free && free < MediaPolicy.MinFreeBytesForUpload)
            {
                await imports.FinalizeFileAsync(Failed(file, MediaErrorCodes.DiskSpace, sha), ct);
                return;
            }

            ProcessedImageVariants variants;
            try
            {
                staged.Seek(0, SeekOrigin.Begin);
                variants = processor.Process(staged, mime, width, height);
            }
            catch (MediaProcessingException)
            {
                await imports.FinalizeFileAsync(Failed(file, MediaErrorCodes.CorruptImage, sha), ct);
                return;
            }

            var ext = MediaPolicy.StorageExtension(mime);
            var vext = variants.ThumbCatalogExt;
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
                staged.Seek(0, SeekOrigin.Begin);
                await storage.WriteAsync(keys.Orig, staged, ct);
                written.Add(keys.Orig);
                await storage.WriteAsync(keys.ThumbCatalog, new MemoryStream(variants.ThumbCatalog), ct);
                written.Add(keys.ThumbCatalog);
                await storage.WriteAsync(keys.ThumbPos, new MemoryStream(variants.ThumbPos), ct);
                written.Add(keys.ThumbPos);
                await storage.WriteAsync(keys.Preview, new MemoryStream(variants.Preview), ct);
                written.Add(keys.Preview);
            }
            catch
            {
                foreach (var key in written)
                {
                    try { await storage.DeleteAsync(key, CancellationToken.None); }
                    catch { /* أفضل جهد */ }
                }
                await imports.FinalizeFileAsync(Failed(file, "STORAGE_WRITE_FAILURE", sha), ct);
                return;
            }

            var ahash = AverageHash.Compute(variants.Gray8x8);
            var draft = new MediaAssetDraft(
                sha, mime, width, height, file.SizeBytes, keys.Orig,
                keys.ThumbCatalog, keys.ThumbPos, keys.Preview, ahash,
                job.CreatedBy, DateTimeOffset.UtcNow);

            try
            {
                await imports.FinalizeFileAsync(new ImportFileOutcome(
                    file.FileId, "DONE", null, sha, null, null, draft), ct);
            }
            catch
            {
                // فشل الإدراج: تنظيف تعويضي ثم FAILED — لا يُعلن DONE بلا أصل
                foreach (var key in written)
                {
                    try { await storage.DeleteAsync(key, CancellationToken.None); }
                    catch { /* أفضل جهد */ }
                }
                await imports.FinalizeFileAsync(Failed(file, "ASSET_INSERT_FAILURE", sha), ct);
                return;
            }

            if (!await imports.HasRemainingWorkAsync(job.JobId, ct))
            {
                await FinalizeJobAsync(job.JobId, ct);
            }
        }
        catch (Exception ex)
        {
            LogImportFileFailure(ex, file.FileId);
            try
            {
                await imports.FinalizeFileAsync(Failed(file, "PROCESSING_FAILURE", null), ct);
            }
            catch (Exception ex2)
            {
                // تعذّر حتى تسجيل الفشل — يبقى PROCESSING ويُعاد إدراجه عند التشغيل القادم (تعافي الانهيار)
                LogImportFailureRecordFailure(ex2, file.FileId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "فشل غير متوقع في معالجة ملف استيراد {FileId}")]
    private partial void LogImportFileFailure(Exception ex, long fileId);

    [LoggerMessage(Level = LogLevel.Error, Message = "تعذر تسجيل فشل الملف {FileId} — سيُعاد إدراجه عند الاستئناف")]
    private partial void LogImportFailureRecordFailure(Exception ex, long fileId);

    private static ImportFileOutcome Failed(ImportFileRowInfo file, string code, string? sha) =>
        new(file.FileId, "FAILED", null, sha, code, null, null);

    /// <summary>إقفال المهمة عند نفاد العمل: COMPLETED + كنس شجرة staging للمهمة (حالتها النهائية فقط).</summary>
    private async Task FinalizeJobAsync(long jobId, CancellationToken ct)
    {
        var job = await imports.FindJobAsync(jobId, ct);
        if (job is { State: "RUNNING" })
        {
            await imports.CompleteJobAsync(jobId, ct);
            storage.SweepStaging(jobId);

            await audit.WriteAsync(new AuditEntry(
                UserId: job.CreatedBy,
                RoleCode: null,
                StationId: null,
                BranchId: null,
                EntitySchema: "catalog.media_import_jobs",
                EntityId: jobId,
                Action: "MEDIA_IMPORT_COMPLETED",
                Result: "SUCCESS",
                Reason: "اكتملت جميع الملفات وكُنس التخزين المؤقت",
                CorrelationId: null), ct);
        }
    }
}
