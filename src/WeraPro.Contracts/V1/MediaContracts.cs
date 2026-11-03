namespace WeraPro.Contracts.V1;

// ── الوسائط (G5 — §6 مكتبة الصور والربط) ─────────────────────────────────────

/// <summary>أصل وسائط في المكتبة — بيانات وصفية وبصمة فقط (لا BLOB في الصف §3.1).</summary>
public sealed record MediaDto(
    long MediaId,
    string Sha256,
    string MimeType,
    int WidthPx,
    int HeightPx,
    long SizeBytes,
    bool HasThumbCatalog,
    bool HasThumbPos,
    bool HasPreview,
    long LinkCount,
    long? UploadedBy,
    DateTimeOffset UploadedAt);

public sealed record MediaSearchResponse(
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages,
    IReadOnlyList<MediaDto> Items);

public sealed record MediaLinkDto(
    long LinkId,
    long MediaId,
    string TargetType,        // PRODUCT | SKU
    long TargetId,
    string TargetCode,
    string TargetNameAr,
    bool IsPrimary,
    int SortOrder,
    bool HasThumbPos);

public sealed record MediaDetailsResponse(MediaDto Media, IReadOnlyList<MediaLinkDto> Links);

public sealed record LinkMediaRequest(long? ProductId, long? SkuId, bool MakePrimary);

/// <summary>نتيجة رفع أصل واحد — أو الإشارة إلى الموجود عند التطابق التام.</summary>
public sealed record UploadMediaResponse(
    bool Created,
    long? ExistingMediaId,   // عند 409 EXACT_DUPLICATE
    MediaDto? Media);

/// <summary>تقرير المكررات — عرض فقط (لا دمج/حذف تلقائي §6.2).</summary>
public sealed record MediaDuplicatesReportResponse(
    MediaExactEncountersDto Exact,
    MediaVisualSuspectsDto VisualSuspects);

public sealed record MediaExactEncountersDto(
    long TotalCount, int Page, IReadOnlyList<MediaDuplicateEncounterDto> Items);

public sealed record MediaDuplicateEncounterDto(
    long FileId,
    long JobId,
    string JobLabel,
    string ClientName,
    string Sha256,
    long? ExistingMediaId,
    DateTimeOffset? ProcessedAt);

public sealed record MediaVisualSuspectsDto(
    long TotalCount, IReadOnlyList<MediaVisualSuspectPairDto> Items);

public sealed record MediaVisualSuspectPairDto(long FirstMediaId, long SecondMediaId, int AhashDistance);

/// <summary>الصورة الافتراضية لصيغة بيع (تغذي POS والكتالوج §6.3) — أولوية واضحة.</summary>
public sealed record SkuDefaultImageResponse(
    long SkuId,
    string SkuCode,
    string Source,           // SKU | PRODUCT | NONE
    long? MediaId,
    bool HasThumbPos);

// ── مهام استيراد الصور الدفعي (G5 — §6.1) ───────────────────────────────────

public sealed record ImportJobDto(
    long JobId,
    string Label,
    string State,            // OPEN | RUNNING | COMPLETED | CANCELLED
    string StorageScope,     // بصمة نطاق التخزين (16 سداسياً) — تشخيص الملكية
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ImportCountsDto Counts);

public sealed record ImportCountsDto(int Pending, int Processing, int Done, int Failed, int Skipped)
{
    public int Total => Pending + Processing + Done + Failed + Skipped;
}

public sealed record CreateImportJobRequest(string Label);

public sealed record ImportAddFilesResponse(long JobId, IReadOnlyList<ImportFileDto> Rows);

public sealed record ImportFileDto(
    long FileId,
    int Ordinal,
    string ClientName,
    long SizeBytes,
    string State,            // PENDING | PROCESSING | DONE | FAILED | SKIPPED
    string? Sha256,
    string? ErrorCode,
    string? SkipReason,
    long? MediaId,
    DateTimeOffset? ProcessedAt);

public sealed record ImportFilesPageResponse(
    int Page, int PageSize, long TotalCount, IReadOnlyList<ImportFileDto> Items);

public sealed record ImportReportResponse(
    ImportJobDto Job,
    IReadOnlyDictionary<string, int> ErrorsByCode);
