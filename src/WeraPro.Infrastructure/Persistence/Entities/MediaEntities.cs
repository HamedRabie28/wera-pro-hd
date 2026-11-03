namespace WeraPro.Infrastructure.Persistence.Entities;

using System.ComponentModel.DataAnnotations.Schema;

/// <summary>
/// كيانات الوسائط (G5) — صور مباشرة لجداول V002 (media_assets/product_images)
/// وأعمدة V016 (المفاتيح الثلاثة + ahash + processed_at) وجداول مهام الاستيراد.
/// </summary>

[Table("media_assets", Schema = "catalog")]
public sealed class MediaAssetRow
{
    public long MediaId { get; set; }
    public required string StorageKey { get; set; }
    public required string Sha256 { get; set; }
    public required string MimeType { get; set; }
    public int? WidthPx { get; set; }
    public int? HeightPx { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>عمود G1 الأصلي — غير مستخدم في G5 (يبقى بلا حذف).</summary>
    public string? ThumbKey { get; set; }
    public long? UploadedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // ── V016 ──
    public string? ThumbCatalogKey { get; set; }
    public string? ThumbPosKey { get; set; }
    public string? PreviewKey { get; set; }
    public long? Ahash { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

[Table("product_images", Schema = "catalog")]
public sealed class ProductImageRow
{
    public long LinkId { get; set; }
    public long? ProductId { get; set; }
    public long? SkuId { get; set; }
    public long MediaId { get; set; }
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
}

[Table("media_import_jobs", Schema = "catalog")]
public sealed class MediaImportJobRow
{
    public long JobId { get; set; }
    public required string Label { get; set; }
    public required string State { get; set; } = "OPEN";
    public long CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

[Table("media_import_files", Schema = "catalog")]
public sealed class MediaImportFileRow
{
    public long FileId { get; set; }
    public long JobId { get; set; }
    public int Ordinal { get; set; }
    public required string ClientName { get; set; }
    public string? StagingKey { get; set; }
    public long SizeBytes { get; set; }
    public required string State { get; set; } = "PENDING";
    public string? Sha256 { get; set; }
    public string? ErrorCode { get; set; }
    public string? SkipReason { get; set; }
    public long? MediaId { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
