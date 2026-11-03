namespace WeraPro.Domain.Media;

/// <summary>
/// سياسة مكتبة الوسائط (G5 — §6.1): الأنواع المدعومة صراحة (JPEG/PNG فقط —
/// سياسة رفض واضحة لا تخمين)، وحدود الحجم والأبعاد قبل أي فك ترميز
/// (حارس قنبلة الفك: 24 مليون بكسل كحد أقصى)، وثلاث نسخ مصغرة مستقلة
/// (تصغير فقط — لا تكبير أبداً، والأصل محفوظ ولا يُستبدل §6.1).
/// </summary>
public static class MediaPolicy
{
    /// <summary>الأنواع المدعومة حصراً — ما عداها يُرفض برمز UNSUPPORTED_MEDIA_TYPE.</summary>
    public static readonly IReadOnlySet<string> SupportedMimeTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png" };

    /// <summary>امتداد التخزين حسب النوع — لا امتدادات من العميل أبداً.</summary>
    public static string StorageExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        _ => throw new NotSupportedException("نوع غير مدعوم: " + mimeType),
    };

    public const long MaxFileBytes = 10 * 1024 * 1024;             // 10MB للملف الواحد
    public const long MinFreeBytesForUpload = 64 * 1024 * 1024;    // §6.1: فحص المساحة الحرة قبل الرفع
    public const int MaxTotalPixels = 24_000_000;                  // 24MP — حد قنبلة الفك قبل الترميز
    public const int MinDimension = 1;

    /// <summary>النسخ المصغرة الثلاث — صناديق احتواء (fit box) بلا تكبير.</summary>
    public const int ThumbCatalogMaxPx = 320;
    public const int ThumbPosMaxPx = 128;
    public const int PreviewMaxPx = 1024;

    /// <summary>عتبة الشك البصري: مسافة هامنغ ≤ 8 من 64 بت (§6.2 — تقرير فقط، لا دمج/حذف تلقائي).</summary>
    public const int VisualSuspectHammingThreshold = 8;

    /// <summary>
    /// مهلة سماح تعافي الانهيار (§6.1): صف PROCESSING لا يُعاد إدراجه PENDING
    /// إلا بعد هذه المهلة من وقت الالتقاط (أو إن كان بلا ختم التقاط أصلاً) —
    /// فلا يُلمس ملف قيد المعالجة لدى عامل حي، ويُشفى المعلّق من انهيار سابق.
    /// </summary>
    public const int StaleProcessingGraceSeconds = 30;

    /// <summary>
    /// مقاس النسخة المصغرة: احتواء داخل الصندوق مع حفظ النسبة —
    /// تصغير فقط؛ إن كان المصدر أصغر من الصندوق يبقى بمقاسه (لا تكبير §6.1).
    /// </summary>
    public static (int Width, int Height) ThumbnailTarget(int srcW, int srcH, int boxMax)
    {
        if (srcW < MinDimension || srcH < MinDimension)
        {
            throw new ArgumentOutOfRangeException($"أبعاد غير صالحة: {srcW}×{srcH}");
        }
        if (srcW <= boxMax && srcH <= boxMax)
        {
            return (srcW, srcH); // أصغر أصلاً — لا تكبير
        }
        var scale = Math.Min((double)boxMax / srcW, (double)boxMax / srcH);
        return (Math.Max(1, (int)Math.Round(srcW * scale)), Math.Max(1, (int)Math.Round(srcH * scale)));
    }

    /// <summary>فحص الأبعاد من الترويسة (قبل أي فك — حارس القنبلة).</summary>
    public static bool DimensionsWithinLimits(int width, int height) =>
        width >= MinDimension && height >= MinDimension && (long)width * height <= MaxTotalPixels;
}

/// <summary>نتيجة التحقق من الترويسة — رفض صريح برمز معلوم لا استنتاج.</summary>
public enum MediaSniffResultKind
{
    Ok,
    UnsupportedMediaType,
    TooLarge,
    DimensionsExceedLimit,
    Corrupt,
}

public sealed record MediaSniffResult(MediaSniffResultKind Kind, string? MimeType, int? Width, int? Height);
