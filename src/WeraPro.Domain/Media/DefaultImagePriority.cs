namespace WeraPro.Domain.Media;

/// <summary>
/// أولوية الصورة الافتراضية للصيغة (§6.3: «يحدد مصدر الصورة الافتراضي
/// أولوية واضحة») — قاعدة مجال صرفة قابلة للاختبار:
/// رئيسية مستوى الصيغة ← أول صورة مستوى الصيغة (بترتيب sort ثم link_id)
/// ← رئيسية مستوى المنتج ← أول صورة مستوى المنتج ← لا شيء (Placeholder).
/// </summary>
public static class DefaultImagePriority
{
    public enum Source
    {
        None,
        Sku,
        Product,
    }

    public sealed record Candidate(long MediaId, bool IsPrimary, int SortOrder, long LinkId, bool HasThumbPos);

    public sealed record Choice(Source SourceKind, long MediaId, bool HasThumbPos)
    {
        public static readonly Choice Empty = new(Source.None, 0, false);
    }

    public static Choice Pick(IReadOnlyList<Candidate> skuLevel, IReadOnlyList<Candidate> productLevel)
    {
        var fromSku = BestOf(skuLevel);
        if (fromSku is { } skuChoice)
        {
            return new Choice(Source.Sku, skuChoice.MediaId, skuChoice.HasThumbPos);
        }
        var fromProduct = BestOf(productLevel);
        if (fromProduct is { } productChoice)
        {
            return new Choice(Source.Product, productChoice.MediaId, productChoice.HasThumbPos);
        }
        return Choice.Empty;
    }

    /// <summary>رئيسية أولاً — وإلا الأقدم ترتيباً (sort ثم link_id).</summary>
    private static Candidate? BestOf(IReadOnlyList<Candidate> links)
    {
        if (links.Count == 0)
        {
            return null;
        }
        return links
            .OrderByDescending(c => c.IsPrimary)
            .ThenBy(c => c.SortOrder)
            .ThenBy(c => c.LinkId)
            .First();
    }
}
