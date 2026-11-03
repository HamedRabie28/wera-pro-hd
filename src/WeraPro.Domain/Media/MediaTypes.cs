namespace WeraPro.Domain.Media;

using System.Security.Cryptography;

// ── فحص البنية الكامل (تطارد البتر حتى IEND/EOI بتخطي متدفّق — ذاكرة محدودة 12 بايت) ──

/// <summary>
/// فاحص بنية الصورة الكامل: يمشي قطع PNG حتى IEND وقطع JPEG حتى EOI
/// بتخطي المتدفقات (لا تحميل في الذاكرة) — يرفض الملفات المبتورة حتى لو
/// سمح المفك بفكها جزئياً (صون §6.1: تحقق من الملف التالف).
/// </summary>
public static class ImageStructureValidator
{
    /// <summary>PNG: توقيع ثم قطع (طول 4 + نوع 4 + بيانات + CRC 4) حتى IEND.</summary>
    public static bool IsPngStructurallyComplete(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 8)
        {
            return false;
        }
        var head = new byte[8];
        _ = stream.ReadAtLeast(head, 8, throwOnEndOfStream: false);
        if (!head.AsSpan().SequenceEqual(ImageHeaderSniffer.PngSignature))
        {
            return false;
        }

        var first = true;
        long position = 8;
        while (position + 12 <= stream.Length)
        {
            stream.Seek(position, SeekOrigin.Begin);
            var chunkHeader = new byte[8];
            _ = stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false);
            var length = (long)((uint)(chunkHeader[0] << 24 | chunkHeader[1] << 16 | chunkHeader[2] << 8 | chunkHeader[3]));
            var type = System.Text.Encoding.ASCII.GetString(chunkHeader, 4, 4);

            if (length is < 0 or > int.MaxValue)
            {
                return false;
            }
            if (first && type != "IHDR")
            {
                return false;
            }
            first = false;

            // تخطَّ البيانات + CRC (12 = طول+نوع+كرسيه بعد البيانات)
            position += 12 + length;
            if (position > stream.Length)
            {
                return false; // بتر: القطعة الأخيرة غير مكتملة
            }
            if (type == "IEND")
            {
                return true;
            }
        }
        return false; // لم نصل إلى IEND — الملف مبتور
    }

    /// <summary>JPEG: SOI ثم قطع بطولها حتى علامة EOI (FFD9).</summary>
    public static bool IsJpegStructurallyComplete(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Length < 4)
        {
            return false;
        }
        var soi = new byte[2];
        _ = stream.ReadAtLeast(soi, 2, throwOnEndOfStream: false);
        if (soi[0] != 0xFF || soi[1] != 0xD8)
        {
            return false;
        }

        long position = 2;
        while (position + 2 <= stream.Length)
        {
            stream.Seek(position, SeekOrigin.Begin);
            var markerBytes = new byte[2];
            _ = stream.ReadAtLeast(markerBytes, 2, throwOnEndOfStream: false);
            if (markerBytes[0] == 0xFF && markerBytes[1] == 0xD9)
            {
                return true; // EOI
            }
            if (markerBytes[0] != 0xFF)
            {
                position++;
                continue;
            }
            if (markerBytes[1] is 0x00 or 0xFF)
            {
                position++;
                continue;
            }

            // قطعة بطولها (البايتان يشملان نفسيهما)
            var lengthBytes = new byte[2];
            _ = stream.ReadAtLeast(lengthBytes, 2, throwOnEndOfStream: false);
            var segmentLength = (markerBytes[1] is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) ? 0 : (lengthBytes[0] << 8 | lengthBytes[1]);
            if (segmentLength < 2 && markerBytes[1] is not (0xD8 or 0x01 or >= 0xD0 and <= 0xD7))
            {
                return false;
            }
            position += markerBytes[1] is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7 ? 2 : 2 + segmentLength - 2;
        }
        return false; // بلا EOI — مبتور
    }
}

// ── رموز أخطاء الوسائط (رسائل عربية مفهومة — لا كشف داخلي) ──────────────────

/// <summary>رموز أخطاء الوسائط الموحدة — تُترجم إلى HTTP 400/409 بلا 500.</summary>
public static class MediaErrorCodes
{
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string TooLarge = "FILE_TOO_LARGE";
    public const string DimensionsExceedLimit = "DIMENSIONS_EXCEED_LIMIT";
    public const string CorruptImage = "CORRUPT_IMAGE";
    public const string ExactDuplicate = "EXACT_DUPLICATE";
    public const string DiskSpace = "INSUFFICIENT_DISK_SPACE";
    public const string MediaNotFound = "MEDIA_NOT_FOUND";
    public const string LinkNotFound = "LINK_NOT_FOUND";
    public const string LinkExists = "LINK_ALREADY_EXISTS";
    public const string TargetNotFound = "TARGET_NOT_FOUND";
    public const string BothTargets = "ONE_TARGET_ONLY";
    public const string MediaInUse = "MEDIA_IN_USE";
    public const string VariantNotReady = "VARIANT_NOT_READY";
    public const string VariantInvalid = "VARIANT_INVALID";
    public const string InvalidJobState = "INVALID_JOB_STATE";
    public const string JobNotFound = "JOB_NOT_FOUND";

    public static string MessageAr(string code) => code switch
    {
        UnsupportedMediaType => "نوع الملف غير مدعوم — المكتبة تدعم JPEG وPNG فقط",
        TooLarge => "حجم الملف يتجاوز الحد المسموح (10MB)",
        DimensionsExceedLimit => "أبعاد الصورة تتجاوز الحد المسموح (24 مليون بكسل)",
        CorruptImage => "ملف الصورة تالف أو غير قابل للفك",
        ExactDuplicate => "صورة مطابقة تماماً موجودة مسبقاً في المكتبة (نفس البصمة SHA-256)",
        DiskSpace => "المساحة الحرة في مخزن الوسائط غير كافية",
        MediaNotFound => "الأصل غير موجود في المكتبة",
        LinkNotFound => "ربط الصورة غير موجود",
        LinkExists => "الصورة مربوطة بهذا الهدف مسبقاً",
        TargetNotFound => "الهدف (منتج/صيغة بيع) غير موجود",
        BothTargets => "حدد هدفاً واحداً فقط: منتج أو صيغة بيع — لا الاثنين معاً",
        MediaInUse => "لا يمكن حذف الأصل: مربوط بمنتجات/صيغ مستخدمة — أزل الربط أولاً بعد مراجعة",
        VariantNotReady => "هذه النسخة غير جاهزة بعد لهذا الأصل",
        VariantInvalid => "النسخة المطلوبة غير معروفة (original/thumb-catalog/thumb-pos/preview)",
        InvalidJobState => "حالة المهمة لا تسمح بهذه العملية",
        JobNotFound => "مهمة الاستيراد غير موجودة",
        _ => "خطأ غير معروف في الوسائط",
    };
}

/// <summary>
/// متطفل ترويسات الصور (§6.1): يقرأ النوع والأبعاد من ترويسة الملف نفسه —
/// PNG (توقيع + IHDR) وJPEG (SOI + SOF0/SOF2) — قبل أي فك ترميز، فيرفض
/// القنابل (ترويسة تدّعي 30000×30000) وغير المدعوم والتالف الترويسياً.
/// قراءة واحدة ≤ 64KB — لا تحميل الملف كاملاً في الذاكرة أبداً.
/// </summary>
public static class ImageHeaderSniffer
{
    private const int ProbeLimit = 64 * 1024;

    internal static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSoi = [0xFF, 0xD8];

    /// <summary>يقرأ أول ProbeLimit بايت من الدفق ثم يعيد موضعه (لا يُستهلك الدفق).</summary>
    private static byte[] Probe(Stream stream)
    {
        var buffer = new byte[ProbeLimit];
        var offset = 0;
        while (offset < ProbeLimit)
        {
            var read = stream.Read(buffer, offset, ProbeLimit - offset);
            if (read <= 0)
            {
                break;
            }
            offset += read;
        }
        stream.Seek(0, SeekOrigin.Begin);
        return buffer[..offset];
    }

    public static MediaSniffResult Sniff(Stream stream)
    {
        var head = Probe(stream);
        if (head.Length < 8)
        {
            return Corrupt();
        }

        if (head.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            // PNG: IHDR ثابتة أول قطعة — العرض عند 16 والطول عند 20 من بداية الملف
            if (head.Length < 24)
            {
                return Corrupt();
            }
            var w = ReadU32Be(head, 16);
            var h = ReadU32Be(head, 20);
            var classified = Classify("image/png", (int)w, (int)h);
            if (classified.Kind is MediaSniffResultKind.Ok
                && !ImageStructureValidator.IsPngStructurallyComplete(stream))
            {
                stream.Seek(0, SeekOrigin.Begin);
                return Corrupt(); // مبتور: البنية لا تصل إلى IEND
            }
            stream.Seek(0, SeekOrigin.Begin);
            return classified;
        }

        if (head.AsSpan(0, 2).SequenceEqual(JpegSoi))
        {
            // JPEG: مسح العلامات حتى SOF0/SOF2 (الطول 2 + الدقة 1 ثم الطول فالعرض)
            var i = 2;
            while (i + 9 < head.Length)
            {
                if (head[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                var marker = head[i + 1];
                if (marker is 0xC0 or 0xC2)
                {
                    var h = ReadU16Be(head, i + 5);
                    var w = ReadU16Be(head, i + 7);
                    var classified = Classify("image/jpeg", w, h);
                    if (classified.Kind is MediaSniffResultKind.Ok
                        && !ImageStructureValidator.IsJpegStructurallyComplete(stream))
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        return Corrupt(); // مبتور: لا EOI
                    }
                    stream.Seek(0, SeekOrigin.Begin);
                    return classified;
                }
                if (marker is not (0xFF or 0x00) && i + 3 < head.Length)
                {
                    // تخطَّ القطعة بطولها (الطول يشمل البايتين نفسيهما)
                    var segLen = ReadU16Be(head, i + 2);
                    if (segLen < 2)
                    {
                        break;
                    }
                    i += 2 + segLen;
                }
                else
                {
                    i++;
                }
            }
            return Corrupt();
        }

        return new(MediaSniffResultKind.UnsupportedMediaType, null, null, null);
    }

    private static MediaSniffResult Corrupt() => new(MediaSniffResultKind.Corrupt, null, null, null);

    private static MediaSniffResult Classify(string mime, int w, int h) =>
        !MediaPolicy.DimensionsWithinLimits(w, h)
            ? new(MediaSniffResultKind.DimensionsExceedLimit, mime, w, h)
            : new(MediaSniffResultKind.Ok, mime, w, h);

    private static ushort ReadU16Be(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);

    private static uint ReadU32Be(byte[] b, int at) =>
        (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
}

/// <summary>
/// مفاتيح التخزين المُدارة (§6.1: لا مسارات محلية مطلقة كمصدر للحقائق —
/// معرفات تخزين نسبية فقط): تخزين موجَّه بالمحتوى media/{h2}/{h64}/…
/// لا يُشتق أي مفتاح من اسم ملف العميل (client_name للعرض فقط).
/// </summary>
public static class MediaStorageKeys
{
    private const string Hex = "0123456789abcdef";

    /// <summary>يجب أن يكون sha256 سداسياً عشرياً صغيراً بطول 64 محرفاً.</summary>
    public static bool IsValidSha256(string sha) =>
        sha.Length == 64 && sha.All(c => Hex.Contains(char.ToLowerInvariant(c)));

    public static string Original(string sha256, string storageExt) =>
        $"media/{sha256[..2]}/{sha256}/orig.{storageExt}";

    public static string ThumbCatalog(string sha256, string variantExt) =>
        $"media/{sha256[..2]}/{sha256}/thumb-catalog.{variantExt}";

    public static string ThumbPos(string sha256, string variantExt) =>
        $"media/{sha256[..2]}/{sha256}/thumb-pos.{variantExt}";

    public static string Preview(string sha256, string variantExt) =>
        $"media/{sha256[..2]}/{sha256}/preview.{variantExt}";

    /// <summary>مفتاح ملف مؤقت في مهمة استيراد — لا يعتمد على اسم العميل إطلاقاً.</summary>
    public static string Staging(long jobId, int ordinal) => $"staging/{jobId:D10}/{ordinal:D6}.bin";

    /// <summary>دليل التخزين المؤقت للمهمة كلها (للكنس عند إقفالها).</summary>
    public static string StagingJobRoot(long jobId) => $"staging/{jobId:D10}";

    /// <summary>
    /// تحقق إجباري: المفتاح يجب أن يطابق الأنماط المدارة فقط —
    /// رفض مطلق لأي اجتياز مسار (..) أو فاصل Windows أو مقطع بصمة غير
    /// سداسي عشري صغير أو فراغ (حارس §6.1: امنع path traversal — بالمفتاح،
    /// فلا يصل اسم العميل إلى نظام الملفات في أي مسار).
    /// </summary>
    public static bool IsManagedKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains("..") || key.Contains('\\')
            || key.Any(char.IsWhiteSpace))
        {
            return false;
        }
        var parts = key.Split('/');
        if (parts.Length < 3)
        {
            return false;
        }
        return parts[0] switch
        {
            "media" => parts.Length == 4
                        && parts[1].Length == 2 && parts[1].All(Hex.Contains)
                        && parts[2].Length == 64 && parts[2].All(Hex.Contains)
                        && parts[3] is "orig.png" or "orig.jpg"
                            or "thumb-catalog.png" or "thumb-catalog.jpg"
                            or "thumb-pos.png" or "thumb-pos.jpg"
                            or "preview.png" or "preview.jpg",
            "staging" => parts.Length == 3
                        && parts[1].Length == 10 && parts[1].All(char.IsDigit)
                        && parts[2].Length == 10 && parts[2].EndsWith(".bin", StringComparison.Ordinal)
                        && parts[2][..6].All(char.IsDigit),
            _ => false,
        };
    }
}

/// <summary>
/// بصمة نطاق التخزين (G5 — سلامة المعالجة الموزعة): 16 محرفاً سداسياً
/// عشرياً صغيراً من SHA-256 للمسار المطلق المُطبَّع لجذر مخزن الوسائط.
/// المهمة تُختم ببصمة جذرها عند الإنشاء، ولا يعالج أي عامل إلا مهمات بصمته —
/// فلا يسرق مثيلٌ (بجذر مختلف) ملفات مهمةٍ بايتاتها المؤقتة خارج متناوله
/// (وإلا فشلت كلها بPROCESSING_FAILURE بلا سبب ظاهر). مثيلات تشترك في الجذر
/// (قرص مشترك/تثبيت متعدد سليم) تتشارك البصمة فتتعاون بطبيعتها مع SKIP LOCKED.
/// </summary>
public static class MediaStorageScope
{
    public const int Length = 16;

    /// <summary>البصمة من المسار المطلق المُطبَّع للجذر — أول 16 محرفاً سداسية صغيرة.</summary>
    public static string Compute(string absoluteRootPath)
    {
        var normalized = absoluteRootPath.TrimEnd('/', '\\').Replace('\\', '/');
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..Length].ToLowerInvariant();
    }

    /// <summary>فحص الصيغة الإجباري: 16 محرفاً سداسياً عشرياً صغيراً حصراً.</summary>
    public static bool IsValid(string? scope) =>
        scope is not null
        && scope.Length == Length
        && scope.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>حاسبة SHA-256 متدفقة — 64KB Buffer (لا تحميل الملف كاملاً §6.1).</summary>
public static class MediaHasher
{
    public static async Task<string> ComputeHexAsync(Stream stream, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// البصمة الإدراكية aHash (متوسط 8×8 رمادياً — 64 بت): تُحسب من مصفوفة
/// تدرجات 8×8 (0-255) يخرجها المعالج؛ المجال يحسب البصمة والمسافة —
/// «مطابق تماماً» تقرره SHA-256 وحدها، و«المشتبه بصرياً» مسافة هامنغ
/// ≤ العتبة (§6.2) — تقرير فقط، لا دمج ولا حذف تلقائي بلا مراجعة.
/// </summary>
public static class AverageHash
{
    public static long Compute(ReadOnlySpan<byte> gray8x8)
    {
        if (gray8x8.Length != 64)
        {
            throw new ArgumentException("متوقع 64 قيمة رمادية (8×8)", nameof(gray8x8));
        }
        long sum = 0;
        for (var i = 0; i < 64; i++)
        {
            sum += gray8x8[i];
        }
        var mean = (int)(sum / 64);
        long hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (gray8x8[i] > mean)
            {
                hash |= 1L << i;
            }
        }
        return hash;
    }

    public static int Hamming(long a, long b)
    {
        var diff = a ^ b;
        var count = 0;
        while (diff != 0)
        {
            diff &= diff - 1;
            count++;
        }
        return count;
    }

    public static bool IsVisualSuspect(long a, long b) =>
        Hamming(a, b) <= MediaPolicy.VisualSuspectHammingThreshold;

    /// <summary>
    /// استخراج أزواج «المشتبه بصرياً» من قائمة (id, ahash) — تجميع دلو
    /// لكل موقع بايت (8 مواقع × 256 دلو): المرشحان يشاركان قيمة بايت واحدة
    /// على الأقل في موقع ما، ثم فحص هامنغ الفعلي — تجنيب O(n²) الساذج.
    /// </summary>
    public static IReadOnlyList<(long FirstId, long SecondId, int Distance)> FindSuspectedPairs(
        IReadOnlyList<(long Id, long Ahash)> items)
    {
        var results = new List<(long, long, int)>();
        if (items.Count < 2)
        {
            return results;
        }

        for (var slot = 0; slot < 8; slot++)
        {
            var seen = new List<int>[256];
            for (var idx = 0; idx < items.Count; idx++)
            {
                var b = (int)((items[idx].Ahash >> (slot * 8)) & 0xFF);
                (seen[b] ??= []).Add(idx);
            }

            for (var bucket = 0; bucket < 256; bucket++)
            {
                if (seen[bucket] is not { Count: > 1 } members)
                {
                    continue;
                }
                for (var x = 0; x < members.Count; x++)
                {
                    for (var y = x + 1; y < members.Count; y++)
                    {
                        var first = items[members[x]];
                        var second = items[members[y]];
                        var distance = Hamming(first.Ahash, second.Ahash);
                        if (distance <= MediaPolicy.VisualSuspectHammingThreshold)
                        {
                            results.Add((first.Id, second.Id, distance));
                        }
                    }
                }
            }
        }

        // إزالة التكرار (الزوج نفسه قد يظهر من أكثر من موقع بايت) بترتيب ثابت
        return results
            .DistinctBy(p => (Math.Min(p.Item1, p.Item2), Math.Max(p.Item1, p.Item2)))
            .OrderBy(p => p.Item3).ThenBy(p => p.Item1).ThenBy(p => p.Item2)
            .ToList();
    }
}
