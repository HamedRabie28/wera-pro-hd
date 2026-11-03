namespace WeraPro.Domain.Tests;

using WeraPro.Domain.Media;
using Xunit;

/// <summary>
/// اختبارات مجال الوسائط (G5): متطفل الترويسات · مفاتيح التخزين المُدارة ·
/// البصمة الإدراكية aHash ومسافة هامنغ وأزواج الشك البصري · مقاسات التصغير
/// بلا تكبير · أولوية الصورة الافتراضية (SKU ثم المنتج).
/// </summary>
public sealed class MediaPolicyTests
{
    [Fact]
    public void ThumbnailTarget_NeverUpscales_SmallSourceStaysAsIs()
    {
        Assert.Equal((64, 48), MediaPolicy.ThumbnailTarget(64, 48, MediaPolicy.ThumbCatalogMaxPx));
        Assert.Equal((8, 8), MediaPolicy.ThumbnailTarget(8, 8, MediaPolicy.ThumbPosMaxPx));
    }

    [Theory]
    [InlineData(1280, 960, 320, 240)]
    [InlineData(4000, 3000, 320, 240)]
    [InlineData(300, 5000, 19, 320)]
    [InlineData(1024, 768, 320, 240)]
    public void ThumbnailTarget_ScalesToBoundingBox_PreservesAspect(int w, int h, int expW, int expH)
    {
        var (tw, th) = MediaPolicy.ThumbnailTarget(w, h, 320);
        Assert.Equal(expW, tw);
        Assert.Equal(expH, th);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 5)]
    public void ThumbnailTarget_RejectsInvalidDimensions(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaPolicy.ThumbnailTarget(w, h, 320));
    }

    [Fact]
    public void Dimensions_24MpBoundary()
    {
        Assert.True(MediaPolicy.DimensionsWithinLimits(6000, 4000));      // 24MP بالحد
        Assert.False(MediaPolicy.DimensionsWithinLimits(6001, 4000));     // فوق الحد
        Assert.False(MediaPolicy.DimensionsWithinLimits(0, 100));
    }
}

public sealed class ImageHeaderSnifferTests
{
    /// <summary>PNG بتخطيط قطع حقيقي (IHDR ثم IDAT ثم IEND) — CRC أصفاراً (الفاحص البنيوي لا يتحقق قيم CRC، بل الأطوال والوصول إلى IEND).</summary>
    private static byte[] Png(int w, int h)
    {
        var bytes = new List<byte>
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        };
        // IHDR: الطول 13 + النوع + 13 بيانات + CRC
        bytes.AddRange([0x00, 0x00, 0x00, 0x0D]);
        bytes.AddRange("IHDR"u8);
        bytes.AddRange([(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w]);
        bytes.AddRange([(byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h]);
        bytes.AddRange([8, 6, 0, 0, 0]);
        bytes.AddRange(new byte[4]); // CRC
        // IDAT: الطول 4 + النوع + 4 بيانات + CRC
        bytes.AddRange([0x00, 0x00, 0x00, 0x04]);
        bytes.AddRange("IDAT"u8);
        bytes.AddRange(new byte[4]);
        bytes.AddRange(new byte[4]); // CRC
        // IEND: الطول 0 + النوع + CRC
        bytes.AddRange([0x00, 0x00, 0x00, 0x00]);
        bytes.AddRange("IEND"u8);
        bytes.AddRange(new byte[4]); // CRC
        return [.. bytes];
    }

    [Fact]
    public void ValidPng_ReadsTypeAndDimensions()
    {
        var result = ImageHeaderSniffer.Sniff(new MemoryStream(Png(640, 480)));
        Assert.Equal(MediaSniffResultKind.Ok, result.Kind);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(640, result.Width);
        Assert.Equal(480, result.Height);
    }

    [Fact]
    public void OversizedPng_DimensionsExceedLimit()
    {
        var result = ImageHeaderSniffer.Sniff(new MemoryStream(Png(30000, 30000)));
        Assert.Equal(MediaSniffResultKind.DimensionsExceedLimit, result.Kind);
    }

    [Fact]
    public void UnsupportedMagic_Gif_RejectedExplicitly()
    {
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0x00, 0x00 };
        var result = ImageHeaderSniffer.Sniff(new MemoryStream(gif));
        Assert.Equal(MediaSniffResultKind.UnsupportedMediaType, result.Kind);
    }

    [Fact]
    public void TooShortStream_Corrupt()
    {
        var result = ImageHeaderSniffer.Sniff(new MemoryStream([0x89, 0x50]));
        Assert.Equal(MediaSniffResultKind.Corrupt, result.Kind);
    }

    [Fact]
    public void PngHeaderWithoutIHDR_Corrupt()
    {
        // توقيع PNG ثم قطع لا تحمل أبعاداً
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
        Assert.Equal(MediaSniffResultKind.Corrupt, ImageHeaderSniffer.Sniff(new MemoryStream(bytes)).Kind);
    }

    [Fact]
    public void Sniffer_DoesNotConsumeStream()
    {
        var stream = new MemoryStream(Png(10, 10));
        var before = stream.Position;
        _ = ImageHeaderSniffer.Sniff(stream);
        Assert.Equal(before, stream.Position);
    }

    [Fact]
    public async Task Hasher_MatchesKnownSha256()
    {
        var stream = new MemoryStream("abc"u8.ToArray());
        var hex = await MediaHasher.ComputeHexAsync(stream);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
        Assert.Equal(64, hex.Length);
    }
}

public sealed class MediaStorageKeysTests
{
    [Fact]
    public void ContentAddressedLayout_IsDeterministic()
    {
        var sha = new string('f', 64);
        Assert.Equal("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/orig.png", MediaStorageKeys.Original(sha, "png"));
        Assert.Equal("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/thumb-catalog.jpg", MediaStorageKeys.ThumbCatalog(sha, "jpg"));
        Assert.Equal("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/thumb-pos.png", MediaStorageKeys.ThumbPos(sha, "png"));
        Assert.Equal("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/preview.jpg", MediaStorageKeys.Preview(sha, "jpg"));
    }

    [Fact]
    public void StagingKeys_NeverUseClientNames()
    {
        Assert.Equal("staging/0000000042/000001.bin", MediaStorageKeys.Staging(42, 1));
        Assert.Equal("staging/0000000042", MediaStorageKeys.StagingJobRoot(42));
    }

    [Theory]
    [InlineData("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/orig.png", true)]
    [InlineData("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/thumb-pos.jpg", true)]
    [InlineData("staging/0000000042/000001.bin", true)]
    [InlineData("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/evil.png", false)]       // اسم نسخة غير معروف
    [InlineData("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/orig.exe", false)]      // امتداد غير صورة
    [InlineData("media/FF/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/orig.png", false)]      // مقطع بصمة كبير
    [InlineData("media/ff/fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/orig.png", false)]       // بصمة 63 محرفاً
    [InlineData("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/../../etc/passwd", false)]
    [InlineData("staging/0000000042/000001.txt", false)]
    [InlineData("media/ff/ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/orig.png ", false)]
    [InlineData("", false)]
    public void IsManagedKey_GuardsEveryPath(string key, bool expected)
    {
        Assert.Equal(expected, MediaStorageKeys.IsManagedKey(key));
    }

    [Fact]
    public void IsValidSha256_Only64LowercaseHex()
    {
        Assert.True(MediaStorageKeys.IsValidSha256(new string('0', 64)));
        Assert.False(MediaStorageKeys.IsValidSha256(new string('G', 64)));
        Assert.False(MediaStorageKeys.IsValidSha256(new string('0', 63)));
    }
}

public sealed class AverageHashTests
{
    private static byte[] Flat(byte value) => Enumerable.Repeat(value, 64).ToArray();

    [Fact]
    public void Compute_FlatImage_HashDependsOnMeanSplit()
    {
        // 32 قيمة 0 + 32 قيمة 255: المتوسط 127.6 — كل ما فوقه (255) يرفع بتاً
        var gray = new byte[64];
        for (var i = 0; i < 32; i++)
        {
            gray[i] = 0;
            gray[63 - i] = 255;
        }
        var hash = AverageHash.Compute(gray);
        Assert.Equal(32, System.Numerics.BitOperations.PopCount((ulong)hash));
    }

    [Fact]
    public void Hamming_SymmetricCount()
    {
        Assert.Equal(0, AverageHash.Hamming(0b1010, 0b1010));
        Assert.Equal(2, AverageHash.Hamming(0b1010, 0b0110));
        Assert.Equal(64, AverageHash.Hamming(0, -1L));
    }

    [Fact]
    public void VisualSuspect_WithinThreshold()
    {
        long a = 0b1010_1010_1010_1010L;
        long b = a ^ 0b0000_0000_0000_1111L; // مسافة 4
        Assert.True(AverageHash.IsVisualSuspect(a, b));
        Assert.False(AverageHash.IsVisualSuspect(0, -1L)); // مسافة 64
    }

    [Fact]
    public void FindSuspectedPairs_FindsClosePair_SkipsFarOnes_NoDuplicatesInOutput()
    {
        long baseHash = 0b1010_1010_1010_1010_1010_1010_1010_1010L;
        var items = new List<(long, long)>
        {
            (1, baseHash),
            (2, baseHash ^ 0b11),          // مسافة 2 → مشتبه
            (3, baseHash ^ 0b1111_1111L),  // مسافة 8 → مشتبه (بالحد)
            (4, unchecked((long)0xFFFF_FFFF_FFFF_FFFFL)), // بعيد جداً
            (5, baseHash ^ 0b11),          // مسافة 2 عن baseHash (مشتبه) — وبُعد 4 عن العنصر 2
        };

        var pairs = AverageHash.FindSuspectedPairs(items);
        var asSet = pairs.Select(p => (Math.Min(p.FirstId, p.SecondId), Math.Max(p.FirstId, p.SecondId))).ToHashSet();

        Assert.Contains((1L, 2L), asSet);
        Assert.Contains((1L, 3L), asSet);
        Assert.Contains((1L, 5L), asSet);
        Assert.Contains((2L, 5L), asSet);
        Assert.DoesNotContain((1L, 4L), asSet);
        Assert.DoesNotContain((4L, 5L), asSet);
        // لا تكرار لنفس الزوج
        Assert.Equal(pairs.Count, asSet.Count);
    }

    [Fact]
    public void FindSuspectedPairs_EmptyAndSingle_NoPairs()
    {
        Assert.Empty(AverageHash.FindSuspectedPairs([]));
        Assert.Empty(AverageHash.FindSuspectedPairs([(7, 123L)]));
    }
}

public sealed class DefaultImagePriorityTests
{
    private static DefaultImagePriority.Candidate C(long media, bool primary = false, int sort = 0, long link = 0) =>
        new(media, primary, sort, link, HasThumbPos: true);

    [Fact]
    public void SkuLevelPrimary_BeatsEverything()
    {
        var choice = DefaultImagePriority.Pick(
            skuLevel: [C(11, primary: true), C(12)],
            productLevel: [C(21, primary: true), C(22)]);
        Assert.Equal(DefaultImagePriority.Source.Sku, choice.SourceKind);
        Assert.Equal(11, choice.MediaId);
        Assert.True(choice.HasThumbPos);
    }

    [Fact]
    public void SkuLevelAny_BeatsProductPrimary()
    {
        var choice = DefaultImagePriority.Pick(
            skuLevel: [C(12, sort: 2), C(11, sort: 1)],
            productLevel: [C(21, primary: true)]);
        Assert.Equal(DefaultImagePriority.Source.Sku, choice.SourceKind);
        Assert.Equal(11, choice.MediaId); // أقدم ترتيباً (sort ثم link_id)
    }

    [Fact]
    public void ProductPrimary_WhenSkuHasNone()
    {
        var choice = DefaultImagePriority.Pick(
            skuLevel: [],
            productLevel: [C(22), C(21, primary: true)]);
        Assert.Equal(DefaultImagePriority.Source.Product, choice.SourceKind);
        Assert.Equal(21, choice.MediaId);
    }

    [Fact]
    public void ProductFirstBySort_WhenNoPrimaryAnywhere()
    {
        var choice = DefaultImagePriority.Pick(
            skuLevel: [],
            productLevel: [C(31, sort: 5), C(32, sort: 1)]);
        Assert.Equal(DefaultImagePriority.Source.Product, choice.SourceKind);
        Assert.Equal(32, choice.MediaId);
    }

    [Fact]
    public void None_WhenNoLinksAtAll_PlaceholderPath()
    {
        var choice = DefaultImagePriority.Pick([], []);
        Assert.Equal(DefaultImagePriority.Source.None, choice.SourceKind);
        Assert.Equal(DefaultImagePriority.Choice.Empty, choice);
        Assert.Equal(0, choice.MediaId);
        Assert.False(choice.HasThumbPos);
    }
}
