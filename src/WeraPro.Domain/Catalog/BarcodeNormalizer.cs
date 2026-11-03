namespace WeraPro.Domain.Catalog;

using WeraPro.Domain.SharedKernel;

/// <summary>
/// تنميط والتحقق من الباركود (§3.1 Exact Match): القيمة المنمّطة هي مفتاح
/// المطابقة الدقيقة — للأنواع الرقمية القياسية (EAN/UPC) أرقام فقط بعد trim،
/// ولها رقم تحقق MOD10 موزون؛ الباقي يُنمّط trim + أحرف كبيرة.
/// فهرس القاعدة الفريد (uq_barcodes_normalized_active) يفرض نطاق التفرد على النشط.
/// </summary>
public static class BarcodeNormalizer
{
    /// <summary>التنميط المطابق لعمود barcode_normalized (V002).</summary>
    public static Result<string> Normalize(string? raw, string barcodeType)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Fail<string>(new("BARCODE_EMPTY", "قيمة الباركود مطلوبة"));
        }

        var trimmed = raw.Trim();
        if (BarcodeTypes.NumericRetail.Contains(barcodeType))
        {
            var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length != trimmed.Length)
            {
                return Result.Fail<string>(new(
                    "BARCODE_INVALID_CHARACTERS",
                    $"باركود {barcodeType} يجب أن يكون أرقاماً فقط"));
            }
            return Result.Ok(digits);
        }

        return Result.Ok(trimmed.ToUpperInvariant());
    }

    /// <summary>الطول المتوقع لكل نوع رقمي قياسي.</summary>
    public static int ExpectedLength(string barcodeType) => barcodeType switch
    {
        BarcodeTypes.Ean13 => 13,
        BarcodeTypes.Ean8 => 8,
        BarcodeTypes.UpcA => 12,
        BarcodeTypes.UpcE => 8,
        _ => 0,
    };

    /// <summary>التحقق الكامل للباركود: تنميط + طول + رقم تحقق للأنواع القياسية.</summary>
    public static Result<string> Validate(string? raw, string barcodeType)
    {
        var normalized = Normalize(raw, barcodeType);
        if (!normalized.IsSuccess)
        {
            return normalized;
        }

        var value = normalized.Value!;
        var expected = ExpectedLength(barcodeType);
        if (expected > 0)
        {
            if (value.Length != expected)
            {
                return Result.Fail<string>(new(
                    "BARCODE_INVALID_LENGTH",
                    $"باركود {barcodeType} طوله {expected} أرقاماً — القيمة المرسلة {value.Length}"));
            }
            if (!HasValidCheckDigit(value))
            {
                return Result.Fail<string>(new(
                    "BARCODE_CHECK_DIGIT_INVALID",
                    "رقم تحقق الباركود غير صحيح (MOD10 موزون)"));
            }
        }
        else
        {
            if (value.Length is < 2 or > 43)
            {
                return Result.Fail<string>(new(
                    "BARCODE_INVALID_LENGTH",
                    "طول الباركود غير مقبول (2–43 محرفاً)"));
            }
        }

        return Result.Ok(value);
    }

    /// <summary>
    /// رقم التحقق MOD10 الموزون (EAN/UPC): من اليمين، الوزن 3 ثم 1 بالتناوب على
    /// الأرقام السابقة، والرقم الأخير يجعل المجموع مضاعفاً للعشرة.
    /// </summary>
    public static bool HasValidCheckDigit(ReadOnlySpan<char> digits)
    {
        if (digits.Length < 2 || !digits[..^1].ToArray().All(char.IsAsciiDigit) || !char.IsAsciiDigit(digits[^1]))
        {
            return false;
        }

        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        var check = (10 - sum % 10) % 10;
        return check == digits[^1] - '0';
    }

    /// <summary>
    /// تنميط قيمة بحث مجهولة النوع (ماسح POS يرسل قيمة خام): أرقام فقط إن كانت
    /// رقمية بالكامل (تطابق منمّط EAN/UPC)، وإلا trim + أحرف كبيرة.
    /// </summary>
    public static string NormalizeForLookup(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        var trimmed = raw.Trim();
        return trimmed.All(char.IsAsciiDigit) ? trimmed : trimmed.ToUpperInvariant();
    }

    /// <summary>حساب رقم تحقق EAN13 من 12 رقماً (لتوليد باركود داخلي مطابق).</summary>
    public static Result<string> ComposeEan13(string twelveDigits)
    {
        if (twelveDigits.Length != 12 || !twelveDigits.All(char.IsAsciiDigit))
        {
            return Result.Fail<string>(new("EAN13_SOURCE_INVALID", "المدخل يجب أن يكون 12 رقماً"));
        }
        Span<char> full = stackalloc char[13];
        twelveDigits.AsSpan().CopyTo(full);
        full[12] = (char)('0' + CheckDigitFor(twelveDigits));
        return Result.Ok(new string(full));
    }

    /// <summary>وزان EAN: آخر خانة في الجسم وزنها 3 (الموضع 12 زوجي) ثم 1/3 بالتناوب.</summary>
    private static int CheckDigitFor(string digits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        return (10 - sum % 10) % 10;
    }
}
