namespace WeraPro.Infrastructure.Media;

using SkiaSharp;
using WeraPro.Application.Media;
using WeraPro.Domain.Media;

/// <summary>
/// معالج صور SkiaSharp (G5): فك كامل (يرفض التالف برمز CORRUPT_IMAGE —
/// لا 500)، تصغير بلا تكبير (fit box من <see cref="MediaPolicy"/>)،
/// ترميز النسخ بامتداد المصدر (PNG→PNG · JPEG→q85)، ومصفوفة 8×8
/// رمادية للبصمة الإدراكية من الأصل مباشرة. معالجة ملف واحد في اللحظة —
/// الذاكرة محدودة بسياسة الأبعاد (24MP) قبل أي فك.
/// </summary>
public sealed class SkiaImageProcessor : IMediaImageProcessor
{
    /// <summary>تصغير عالي الجودة (Mitchell cubic) — ثابت واحد لكل المكتبة.</summary>
    private static readonly SKSamplingOptions Downscale = new(SKCubicResampler.Mitchell);

    public ProcessedImageVariants Process(Stream original, string mimeType, int widthPx, int heightPx)
    {
        // نلتقط نسخة خاصة قبل الفك: SkiaSharp قد يلتقط ملكية دفق الفك ويغلقه
        // عند اكتماله (SKManagedStream) — النسخة الخاصة تعزل تيار المالك عن ذلك.
        original.Seek(0, SeekOrigin.Begin);
        using var capture = new MemoryStream();
        original.CopyTo(capture);
        var bytes = capture.ToArray();

        using var bitmap = SKBitmap.Decode(new MemoryStream(bytes));
        if (bitmap is null)
        {
            throw new MediaProcessingException(
                MediaErrorCodes.CorruptImage, MediaErrorCodes.MessageAr(MediaErrorCodes.CorruptImage));
        }
        using var image = SKImage.FromBitmap(bitmap);

        // الأبعاد الفعلية من الفك — إن خالفت الترويسة فالملف غير سليم
        var actualW = bitmap.Width;
        var actualH = bitmap.Height;
        if (actualW != widthPx || actualH != heightPx)
        {
            throw new MediaProcessingException(
                MediaErrorCodes.CorruptImage, "أبعاد الفك تخالف الترويسة — الملف غير سليم");
        }

        var isJpeg = mimeType is "image/jpeg";
        var variantExt = isJpeg ? "jpg" : "png";

        var (cw, ch) = MediaPolicy.ThumbnailTarget(actualW, actualH, MediaPolicy.ThumbCatalogMaxPx);
        var (pw, ph) = MediaPolicy.ThumbnailTarget(actualW, actualH, MediaPolicy.ThumbPosMaxPx);
        var (vw, vh) = MediaPolicy.ThumbnailTarget(actualW, actualH, MediaPolicy.PreviewMaxPx);

        var thumbCatalog = RenderVariant(image, cw, ch, isJpeg);
        var thumbPos = RenderVariant(image, pw, ph, isJpeg);
        var preview = RenderVariant(image, vw, vh, isJpeg);

        var gray = RenderGray8x8(image);
        return new ProcessedImageVariants(
            thumbCatalog, variantExt,
            thumbPos, variantExt,
            preview, variantExt,
            gray);
    }

    private static byte[] RenderVariant(SKImage source, int width, int height, bool asJpeg)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(source, new SKRect(0, 0, width, height), Downscale);

        using var rendered = surface.Snapshot();
        using var encoded = rendered.Encode(
            asJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png,
            asJpeg ? 85 : 100);
        return encoded.ToArray();
    }

    private static byte[] RenderGray8x8(SKImage source)
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        canvas.DrawImage(source, new SKRect(0, 0, 8, 8), Downscale);

        using var rendered = surface.Snapshot();
        using var pixels = rendered.PeekPixels();
        var gray = new byte[64];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var c = pixels.GetPixelColor(x, y);
                // إدراكي (ITU-R BT.601) — ثابت واحد لكل المكتبة
                var luma = (int)Math.Round(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
                gray[y * 8 + x] = (byte)Math.Clamp(luma, 0, 255);
            }
        }
        return gray;
    }
}
