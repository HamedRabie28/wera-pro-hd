namespace WeraPro.Infrastructure.Tests;

using Microsoft.Extensions.Options;
using SkiaSharp;
using WeraPro.Application.Media;
using WeraPro.Domain.Media;
using WeraPro.Infrastructure.Media;
using Xunit;

/// <summary>
/// اختبارات معالج الصور ومخزن الملفات المُدار (G5): فك/تصغير/ترميز على
/// صور حقيقية مولودة في الاختبار + حواجز المفاتيح (path traversal).
/// </summary>
public sealed class SkiaImageProcessorTests
{
    private readonly SkiaImageProcessor _processor = new();

    /// <summary>صورة PNG حقيقية 64×48 بأربع مناطق لونية.</summary>
    private static byte[] MakePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var surface = SKSurface.Create(bitmap.Info);
        var canvas = surface.Canvas;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = new SKColor(
                    (byte)(x * 255 / Math.Max(1, width - 1)),
                    (byte)(y * 255 / Math.Max(1, height - 1)),
                    128);
                canvas.DrawRect(x, y, 1, 1, new SKPaint { Color = color });
            }
        }
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void ValidPng_ProducesAllVariants_NoUpscale_ForTinySource()
    {
        var png = MakePng(64, 48);
        using var stream = new MemoryStream(png);
        var sniff = ImageHeaderSniffer.Sniff(stream);
        Assert.Equal(MediaSniffResultKind.Ok, sniff.Kind);

        var variants = _processor.Process(new MemoryStream(png), "image/png", sniff.Width!.Value, sniff.Height!.Value);

        // مصدر 64×48 أصغر من كل الصناديق — لا تكبير: النسخ بمقاس المصدر حرفياً
        using var decoded = SKBitmap.Decode(variants.ThumbCatalog);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(48, decoded.Height);
        Assert.Equal(64, variants.Gray8x8.Length);
        Assert.Equal("png", variants.ThumbCatalogExt);
        Assert.All(new[] { variants.ThumbCatalog, variants.ThumbPos, variants.Preview }, b => Assert.NotEmpty(b));
    }

    [Fact]
    public void LargeSource_DownscaledWithinBoxes()
    {
        // 1280×960 → الكتالوج ≤ 320، POS ≤ 128، المعاينة ≤ 1024
        var png = MakePng(1280, 960);
        var sniff = ImageHeaderSniffer.Sniff(new MemoryStream(png));
        Assert.Equal(MediaSniffResultKind.Ok, sniff.Kind);

        var variants = _processor.Process(new MemoryStream(png), "image/png", 1280, 960);

        using (var catalog = SKBitmap.Decode(variants.ThumbCatalog))
        {
            Assert.InRange(catalog.Width, 1, MediaPolicy.ThumbCatalogMaxPx);
            Assert.InRange(catalog.Height, 1, MediaPolicy.ThumbCatalogMaxPx);
        }
        using (var pos = SKBitmap.Decode(variants.ThumbPos))
        {
            Assert.InRange(pos.Width, 1, MediaPolicy.ThumbPosMaxPx);
            Assert.InRange(pos.Height, 1, MediaPolicy.ThumbPosMaxPx);
        }
        using (var preview = SKBitmap.Decode(variants.Preview))
        {
            Assert.InRange(preview.Width, 1, MediaPolicy.PreviewMaxPx);
            Assert.InRange(preview.Height, 1, MediaPolicy.PreviewMaxPx);
        }
    }

    [Fact]
    public void TruncatedPng_IsRejectedAsCorrupt()
    {
        var png = MakePng(32, 32);
        var truncated = png[..(png.Length / 2)]; // بتر 50%

        // البتر يُرفض عند التطفل (قبل أي فك) — الفاحص البنيوي هو الحكم
        var sniff = ImageHeaderSniffer.Sniff(new MemoryStream(truncated));
        Assert.Equal(MediaSniffResultKind.Corrupt, sniff.Kind);
        Assert.False(ImageStructureValidator.IsPngStructurallyComplete(new MemoryStream(truncated)));
        Assert.True(ImageStructureValidator.IsPngStructurallyComplete(new MemoryStream(png)));
    }

    /// <summary>
    /// يهترئ بيانات IDAT فقط (بلا مساس بطول القطع أو ترويساتها) — البنية تبقى
    /// سليمة فيمر الفاحص البنيوي، والرفض يقع عند فك zlib.
    /// </summary>
    public static byte[] CorruptIdatPayload(byte[] png)
    {
        var clone = (byte[])png.Clone();
        long position = 8;
        while (position + 12 <= clone.Length)
        {
            var length = (clone[position] << 24) | (clone[position + 1] << 16) | (clone[position + 2] << 8) | clone[position + 3];
            var type = System.Text.Encoding.ASCII.GetString(clone, (int)position + 4, 4);
            if (type == "IDAT")
            {
                var start = (int)position + 8;                     // بعد الطول+النوع
                var end = Math.Min((int)position + 8 + length, clone.Length - 4); // قبل CRC
                for (var i = start; i < end; i++)
                {
                    clone[i] = 0xAA;
                }
                return clone;
            }
            position += 12 + length;
        }
        throw new InvalidOperationException("لا توجد قطعة IDAT في PNG الاختباري");
    }

    [Fact]
    public void StructurallyCompleteButGarbageIdat_FailsAtDecode()
    {
        // بنية سليمة (أطوال القطع لم تتغير) وبيانات IDAT مهترئة — الرفض يقع عند الفك
        var png = MakePng(32, 32);
        var garbage = CorruptIdatPayload(png);

        var sniff = ImageHeaderSniffer.Sniff(new MemoryStream(garbage));
        Assert.Equal(MediaSniffResultKind.Ok, sniff.Kind);

        var ex = Assert.Throws<MediaProcessingException>(
            () => _processor.Process(new MemoryStream(garbage), "image/png", 32, 32));
        Assert.Equal(MediaErrorCodes.CorruptImage, ex.Code);
    }

    [Fact]
    public void HeaderOnlyBomb_ClaimedHugeDimensions_RejectedBySnifferBeforeDecode()
    {
        // ترويسة PNG سليمة تدّعي 30000×30000 مع جسم مبتور — يرفضها المتطفل قبل أي فك
        var sniff = ImageHeaderSniffer.Sniff(new MemoryStream(PngHeaderOnlyBomb()));
        Assert.Equal(MediaSniffResultKind.DimensionsExceedLimit, sniff.Kind);
    }

    /// <summary>PNG بترويسة IHDR تدّعي 30000×30000 وجسم شبه فارغ (قنبلة الفك).</summary>
    private static byte[] PngHeaderOnlyBomb()
    {
        var head = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        head.AddRange([0x00, 0x00, 0x00, 0x0D]);       // طول IHDR = 13
        head.AddRange("IHDR"u8.ToArray());
        head.AddRange([0x00, 0x00, 0x75, 0x30]);       // width = 30000
        head.AddRange([0x00, 0x00, 0x75, 0x30]);       // height = 30000
        head.AddRange([8, 6, 0, 0, 0]);               // bit depth + color + ...
        head.AddRange(new byte[8]);                    // زائفة بلا CRC
        head.AddRange("IEND"u8.ToArray());
        return [.. head];
    }

    [Fact]
    public void RealJpeg_ProducesJpegVariants()
    {
        // JPEG حقيقي من Skia
        using var bitmap = new SKBitmap(new SKImageInfo(200, 150, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var surface = SKSurface.Create(bitmap.Info);
        surface.Canvas.Clear(new SKColor(30, 144, 255));
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = encoded.ToArray();

        var sniff = ImageHeaderSniffer.Sniff(new MemoryStream(jpeg));
        Assert.Equal(MediaSniffResultKind.Ok, sniff.Kind);
        Assert.Equal("image/jpeg", sniff.MimeType);
        Assert.Equal(200, sniff.Width);
        Assert.Equal(150, sniff.Height);

        var variants = _processor.Process(new MemoryStream(jpeg), "image/jpeg", 200, 150);
        Assert.Equal("jpg", variants.ThumbCatalogExt);
        using var decoded = SKBitmap.Decode(variants.ThumbCatalog);
        Assert.Equal(200, decoded.Width); // أصغر من كل الصناديق — بلا تكبير
    }
}

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wera-g5-storage-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _storage = new LocalFileStorage(Options.Create(new MediaStorageOptions { StorageRoot = _root }));
    }

    [Fact]
    public async Task WriteReadDelete_RoundTrip_ManagedKeysOnly()
    {
        var sha = new string('a', 64);
        var key = MediaStorageKeys.Original(sha, "png");
        var payload = "test-image-bytes"u8.ToArray();

        await _storage.WriteAsync(key, new MemoryStream(payload));
        Assert.True(_storage.Exists(key));

        await using (var read = await _storage.OpenReadAsync(key))
        using (var ms = new MemoryStream())
        {
            await read.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        Assert.True(await _storage.DeleteAsync(key));
        Assert.False(_storage.Exists(key));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("media/xx/../escape.png")]
    [InlineData("media/ab/NOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNOT/orig.png")]
    [InlineData("staging/0000001234/aaaaaa.bin")]
    [InlineData("other/root/file.png")]
    [InlineData("media/ab/" + "66-char-but-not-hex!!" + "000000000000000000000000000000000000000/orig.png")]
    public async Task UnmanagedKeys_AreRejected_PathTraversalImpossible(string key)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.WriteAsync(key, new MemoryStream("x"u8.ToArray())));
        Assert.Throws<InvalidOperationException>(() => _storage.Exists(key));
    }

    [Fact]
    public async Task SweepStaging_RemovesOnlyThatJobTree()
    {
        var jobId = 42;
        var key = MediaStorageKeys.Staging(jobId, 1);
        await _storage.WriteAsync(key, new MemoryStream("staged"u8.ToArray()));
        var otherJob = MediaStorageKeys.Staging(43, 1);
        await _storage.WriteAsync(otherJob, new MemoryStream("other"u8.ToArray()));

        _storage.SweepStaging(jobId);

        Assert.False(_storage.Exists(key));
        Assert.True(_storage.Exists(otherJob));
    }

    [Fact]
    public void FreeBytes_IsKnownOrNull_NeverThrows()
    {
        // على قرص حقيقي: قيمة موجبة؛ العقدة هي العودة دون رمي
        var free = _storage.GetAvailableFreeBytes();
        Assert.True(free is null || free > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
