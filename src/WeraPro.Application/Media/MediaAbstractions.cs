namespace WeraPro.Application.Media;

using WeraPro.Domain.Media;

/// <summary>
/// مستودع مكتبة الوسائط (G5 — catalog.media_assets/product_images):
/// قراءات مركّبة بترقيم خادمي، وكتابات داخل معاملات — وكل رفض يُترجم
/// برمزاً معلوماً (409/400) لا خطأ خادم.
/// </summary>
public interface IMediaRepository
{
    /// <summary>الأصل المطابق تماماً بالبصمة (فحص التكرر §6.1) — null إن لم يوجد.</summary>
    Task<MediaAssetInfo?> FindByShaAsync(string sha256, CancellationToken ct = default);

    Task<MediaAssetInfo?> FindMediaAsync(long mediaId, CancellationToken ct = default);

    /// <summary>بحث المكتبة بترقيم خادمي (العدد الإجمالي من استعلام فعلي §6.2).</summary>
    Task<(IReadOnlyList<MediaAssetInfo> Items, long TotalCount)> SearchMediaAsync(
        MediaSearchFilter filter, CancellationToken ct = default);

    Task<IReadOnlyList<MediaLinkInfo>> ListLinksAsync(long mediaId, CancellationToken ct = default);

    Task<long> CountLinksAsync(long mediaId, CancellationToken ct = default);

    /// <summary>إدراج أصل داخل معاملة واحدة — يرمي بخرق الفهارس الفريدة عند التزاحم.</summary>
    Task<long> InsertAssetAsync(MediaAssetDraft draft, CancellationToken ct = default);

    /// <summary>حذف صف الأصل (لا يُستدعى إلا بعد التحقق من صفر روابط) — true إن حُذف.</summary>
    Task<bool> DeleteAssetAsync(long mediaId, CancellationToken ct = default);

    /// <summary>تحديث مفاتيح النسخ والبصمة الإدراكية ووقت المعالجة (إعادة المعالجة).</summary>
    Task<bool> UpdateAssetVariantsAsync(long mediaId, MediaVariantsUpdate update, CancellationToken ct = default);

    /// <summary>إدراج ربط داخل معاملة (يخلي الرئيسية السابقة للهدف عند makePrimary)
    /// ويعيد الربط المُنشأ — يرمي MediaRepositoryConflictException برمز معلوم عند الازدواج/غياب الهدف.</summary>
    Task<MediaLinkInfo> InsertLinkAsync(MediaLinkDraft draft, CancellationToken ct = default);

    Task<MediaLinkInfo?> FindLinkAsync(long linkId, CancellationToken ct = default);

    /// <summary>حذف صف ربط — لا يمس الأصل إطلاقاً (إزالة الربط لا تحذف §4.4 حـ).</summary>
    Task<bool> DeleteLinkAsync(long linkId, CancellationToken ct = default);

    /// <summary>تعيين رئيسية داخل معاملة: تخلي باقي روابط الهدف ثم تعيّن هذه — 0 إن لم يوجد الربط.</summary>
    Task<int> SetLinkPrimaryAsync(long linkId, CancellationToken ct = default);

    /// <summary>تيار البصمات الإدراكية لتقرير المشتبه بصرياً (§6.2 — تقرير فقط).</summary>
    Task<IReadOnlyList<(long Id, long Ahash)>> ListAhashesAsync(CancellationToken ct = default);

    /// <summary>مقطع أولوية الصورة الافتراضية للصيغة (SKU ثم المنتج §6.3) — null إن لم توجد الصيغة.</summary>
    Task<SkuImageLinksSnapshot?> LoadSkuImageLinksAsync(long skuId, CancellationToken ct = default);

    /// <summary>آخر لقاءات «مطابق تماماً» (ملفات SKIPPED من مهام الاستيراد) — مرقّم.</summary>
    Task<(IReadOnlyList<MediaDuplicateEncounter> Items, long TotalCount)> ListExactEncountersAsync(
        int page, int pageSize, CancellationToken ct = default);
}

/// <summary>مستودع مهام الاستيراد الدفعي (G5 — catalog.media_import_jobs/files).</summary>
public interface IMediaImportRepository
{
    /// <summary>إنشاء مهمة مختومة ببصمة نطاق التخزين (جذر الـstaging الذي تُرحَّل إليه بايتاتها).</summary>
    Task<long> CreateJobAsync(string label, long userId, string storageScope, CancellationToken ct = default);

    Task<MediaImportJobInfo?> FindJobAsync(long jobId, CancellationToken ct = default);

    /// <summary>المهام الجارية في نطاق التخزين المعطى حصراً — لا يرى العامل مهمات جذرٍ لا يملك بايتاتها المؤقتة (سلامة المعالجة الموزعة).</summary>
    Task<IReadOnlyList<long>> ListRunningJobIdsAsync(string storageScope, CancellationToken ct = default);

    /// <summary>OPEN|CANCELLED → RUNNING + إعادة إدراج PROCESSING المعلقة (تعافي الانهيار) — false إن كانت الحالة لا تسمح.</summary>
    Task<bool> TryStartJobAsync(long jobId, CancellationToken ct = default);

    /// <summary>RUNNING|OPEN → CANCELLED (إلغاء آمن — يتوقف الالتقاط، لا يُلغى ملف قيد المعالجة) — false إن كانت حالة نهائية.</summary>
    Task<bool> TryCancelJobAsync(long jobId, CancellationToken ct = default);

    /// <summary>RUNNING → COMPLETED (بعد آخر ملف) — يرمي إن لم تكن RUNNING.</summary>
    Task CompleteJobAsync(long jobId, CancellationToken ct = default);

    Task<bool> HasRemainingWorkAsync(long jobId, CancellationToken ct = default);

    /// <summary>الترتيب التالي داخل المهمة (قفل استشاري داخلي يمنع تزاحم الإضافات المتوازية).</summary>
    Task<int> NextOrdinalAsync(long jobId, CancellationToken ct = default);

    /// <summary>إدراج صف ملف واحد (PENDING بعد كتابة staging — أو FAILED عند الإضافة) — يعيد الصف كما سُجل.</summary>
    Task<ImportFileRowInfo> AddFileAsync(long jobId, ImportStagedFile file, CancellationToken ct = default);

    /// <summary>التقط أدنى ترتيب PENDING وحوّله PROCESSING ذرياً — null إن لا عمل متبق.</summary>
    Task<ImportFileRowInfo?> TryPickNextPendingAsync(long jobId, CancellationToken ct = default);

    /// <summary>تعافي الانهيار المستقل: PROCESSING المعلقة (من إقلاع سابق) تعود PENDING — يعيدها العامل في كل دورة (صفر في التشغيل الطبيعي).</summary>
    Task<int> RequeueStaleProcessingAsync(long jobId, CancellationToken ct = default);

    Task<ImportFileRowInfo?> FindFileAsync(long fileId, CancellationToken ct = default);

    /// <summary>
    /// إقفال نتيجة ملف داخل معاملة واحدة: تحديث صفه إلى حالة نهائية،
    /// وعند DONE يُدرج صف الأصل في نفس المعاملة (لا يُعلَن DONE قبل ثبات الأصل).
    /// </summary>
    Task FinalizeFileAsync(ImportFileOutcome outcome, CancellationToken ct = default);

    Task<(IReadOnlyList<ImportFileRowInfo> Items, long TotalCount)> ListFilesAsync(
        long jobId, string? state, int page, int pageSize, CancellationToken ct = default);

    Task<MediaImportCounts> CountByStateAsync(long jobId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> ErrorHistogramAsync(long jobId, CancellationToken ct = default);
}

// ── نماذج القراءة ────────────────────────────────────────────────────────────

public sealed record MediaAssetInfo(
    long MediaId,
    string Sha256,
    string MimeType,
    int WidthPx,
    int HeightPx,
    long SizeBytes,
    long? Ahash,
    bool HasThumbCatalog,
    bool HasThumbPos,
    bool HasPreview,
    string StorageKey,
    string? ThumbCatalogKey,
    string? ThumbPosKey,
    string? PreviewKey,
    long LinkCount,
    long? UploadedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt);

public sealed record MediaLinkInfo(
    long LinkId,
    long MediaId,
    string TargetType,     // PRODUCT | SKU
    long TargetId,
    string TargetCode,     // كود المنتج/الصيغة (للعرض في قائمة المراجعة)
    string TargetNameAr,
    bool IsPrimary,
    int SortOrder,
    bool HasThumbPos);

public sealed record MediaSearchFilter(
    string? Sha256Prefix,   // ≥ 8 محارف سداسية عشرية صغيرة
    string? MimeType,       // image/jpeg | image/png
    string LinkState,       // ANY | LINKED | UNLINKED
    int Page,
    int PageSize);          // 1..50 (يُقصّ خادمياً)

/// <summary>مسودة إدراج أصل — مفتاح النسخ الثلاث + ahash معاً (إدراج واحد ذري).</summary>
public sealed record MediaAssetDraft(
    string Sha256,
    string MimeType,
    int WidthPx,
    int HeightPx,
    long SizeBytes,
    string StorageKey,
    string? ThumbCatalogKey,
    string? ThumbPosKey,
    string? PreviewKey,
    long? Ahash,
    long? UploadedBy,
    DateTimeOffset ProcessedAt);

public sealed record MediaVariantsUpdate(
    string? ThumbCatalogKey,
    string? ThumbPosKey,
    string? PreviewKey,
    long? Ahash,
    DateTimeOffset ProcessedAt);

public sealed record MediaLinkDraft(long MediaId, long? ProductId, long? SkuId, bool MakePrimary);

/// <summary>لقطة روابط صور الصيغة لتحديد المصدر الافتراضي (§6.3 أولوية واضحة).</summary>
public sealed record SkuImageLinksSnapshot(
    long SkuId,
    string SkuCode,
    long ProductId,
    string ProductCode,
    IReadOnlyList<MediaLinkInfo> SkuLevel,    // بترتيب sort ثم link_id
    IReadOnlyList<MediaLinkInfo> ProductLevel);

/// <summary>لقطة «مطابق تماماً» من مهام الاستيراد (SKIPPED) — عرض فقط.</summary>
public sealed record MediaDuplicateEncounter(
    long FileId,
    long JobId,
    string JobLabel,
    string ClientName,
    string Sha256,
    long? ExistingMediaId,   // من صيغة skip_reason (EXACT_DUPLICATE_OF:{id})
    DateTimeOffset? ProcessedAt);

// ── مهام الاستيراد ───────────────────────────────────────────────────────────

public sealed record MediaImportJobInfo(
    long JobId,
    string Label,
    string State,
    string StorageScope,
    long CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record MediaImportCounts(int Pending, int Processing, int Done, int Failed, int Skipped)
{
    public int Total => Pending + Processing + Done + Failed + Skipped;
    public bool HasRemaining => Pending > 0 || Processing > 0;
}

/// <summary>ملف مُرحَّل إلى التخزين المؤقت (PENDING) أو مرفوض عند الإضافة (FAILED).</summary>
public sealed record ImportStagedFile(
    int Ordinal,
    string ClientName,
    string? StagingKey,      // null عند الرفض عند الإضافة
    long SizeBytes,
    string State,            // PENDING | FAILED
    string? ErrorCode);

public sealed record ImportFileRowInfo(
    long FileId,
    long JobId,
    int Ordinal,
    string ClientName,
    string? StagingKey,
    long SizeBytes,
    string State,
    string? Sha256,
    string? ErrorCode,
    string? SkipReason,
    long? MediaId,
    DateTimeOffset? ProcessedAt);

/// <summary>نتيجة إقفال ملف داخل معاملة واحدة.</summary>
public sealed record ImportFileOutcome(
    long FileId,
    string TerminalState,    // DONE | FAILED | SKIPPED
    long? MediaId,           // DONE فقط
    string? Sha256,
    string? ErrorCode,       // FAILED فقط
    string? SkipReason,      // SKIPPED فقط
    MediaAssetDraft? Asset   // DONE فقط — يُدرج في نفس المعاملة
    );

// ── المعالج والتخزين (تنفيذان في البنية التحتية — عقدهما هنا) ───────────────

/// <summary>نتيجة معالجة صورة: النسخ الثلاث + مصفوفة 8×8 للبصمة الإدراكية.</summary>
public sealed record ProcessedImageVariants(
    byte[] ThumbCatalog, string ThumbCatalogExt,
    byte[] ThumbPos, string ThumbPosExt,
    byte[] Preview, string PreviewExt,
    byte[] Gray8x8);

/// <summary>
/// معالج الصور: فك ترميز كامل (يرفض التالف برمز CORRUPT_IMAGE) + تصغير
/// بلا تكبير + ترميز النسخ بامتداد المصدر — تنفيذ Skia في البنية التحتية.
/// </summary>
public interface IMediaImageProcessor
{
    /// <summary>يفك الصورة وينتج النسخ الثلاث ومصفوفة الرمادي — يرمي MediaProcessingException عند التلف.</summary>
    ProcessedImageVariants Process(Stream original, string mimeType, int widthPx, int heightPx);
}

/// <summary>فشل فك/ترميز — يُترجم CORRUPT_IMAGE (لا 500).</summary>
public sealed class MediaProcessingException(string code, string messageAr) : Exception(messageAr)
{
    public string Code { get; } = code;
    public string MessageAr { get; } = messageAr;
}

/// <summary>
/// تخزين ملفات مُدار بمفاتيح نسبية فقط (§6.1: لا مسارات محلية مطلقة
/// كمصدر للحقائق) — الجذر من الإعدادات/البيئة، وكل مفتاح يمر عبر
/// MediaStorageKeys.IsManagedKey قبل أي مساس بقرص.
/// </summary>
public interface IMediaStorage
{
    /// <summary>بصمة نطاق هذا الجذر (16 محرفاً سداسياً) — تُختَم بها المهام ويُسلَّم بها العامل (لا معالجة لبايتات خارج الجذر).</summary>
    string StorageScopeId { get; }

    long? GetAvailableFreeBytes();

    /// <summary>كتابة ذرية (مؤقت ثم نقل) — المفتاح يجب أن يكون مُداراً وإلا يرمي.</summary>
    Task WriteAsync(string managedKey, Stream content, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string managedKey, CancellationToken ct = default);

    Task<bool> DeleteAsync(string managedKey, CancellationToken ct = default);

    bool Exists(string managedKey);

    /// <summary>كنس شجرة التخزين المؤقت لمهمة (staging/ فقط — يُرفض أي جذر آخر).</summary>
    void SweepStaging(long jobId);
}
