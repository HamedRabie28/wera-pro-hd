namespace WeraPro.Domain.Catalog;

using WeraPro.Domain.SharedKernel;

/// <summary>نتيجة تحليل باركود الوزن/السعر المتغير.</summary>
public sealed record VariableWeightBarcodeResult(
    long ProfileId,
    string ProfileName,
    string PluCode,
    decimal Value,
    string ValueType,       // WEIGHT | PRICE
    long? ValueUnitId);     // وحدة القيمة المشفرة (بُعد MASS عند WEIGHT)

/// <summary>تعريف ملف باركود الوزن المتغير (V002 catalog.vw_barcode_profiles).</summary>
public sealed record VariableWeightProfile(
    long ProfileId,
    string Name,
    string Prefix,
    int TotalLength,
    int PluStartPos,        // 1-based من اليسار
    int PluLength,
    string ValueType,       // WEIGHT | PRICE
    int ValueStartPos,      // 1-based من اليسار
    int ValueLength,
    int ValueDecimals,
    long? ValueUnitId,
    string CheckDigitRule); // NONE | MOD10 | MOD10_WEIGHTED

/// <summary>
/// محلل باركود الوزن/السعر المتغير (§5.3 ق6): الحل من الملفات المعرفة فقط —
/// بادئة أو طول أو رقم تحقق مجهول = رفض صريح برسالة مفهومة، لا تخمين أبداً.
/// </summary>
public static class VariableWeightBarcodeParser
{
    /// <summary>
    /// تحليل قيمة خام ضد الملفات النشطة: أول ملف يطابق البادئة والطول وينجو من
    /// رقم التحقق هو المرجع؛ لا مطابقة = خطأ صريح UNKNOWN_WEIGHT_BARCODE_FORMAT.
    /// </summary>
    public static Result<VariableWeightBarcodeResult> Parse(
        string? raw,
        IReadOnlyList<VariableWeightProfile> activeProfiles)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Result.Fail<VariableWeightBarcodeResult>(new("BARCODE_EMPTY", "قيمة الباركود مطلوبة"));
        }

        var value = raw.Trim();
        if (!value.All(char.IsAsciiDigit))
        {
            return Result.Fail<VariableWeightBarcodeResult>(new(
                "WEIGHT_BARCODE_NOT_NUMERIC",
                "باركود الوزن المتغير أرقام فقط"));
        }

        var candidates = activeProfiles
            .Where(p => value.Length == p.TotalLength && value.StartsWith(p.Prefix, StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0)
        {
            return Result.Fail<VariableWeightBarcodeResult>(new(
                "UNKNOWN_WEIGHT_BARCODE_FORMAT",
                $"باركود الوزن غير معروف البادئة/الطول ({value.Length} خانة) — لا ملف مطابق ولا تخمين"));
        }

        foreach (var profile in candidates)
        {
            var checkResult = VerifyCheckDigit(value, profile);
            if (!checkResult.IsSuccess)
            {
                return Result.Fail<VariableWeightBarcodeResult>(checkResult.Error!);
            }

            var pluSegment = value.Substring(profile.PluStartPos - 1, profile.PluLength);
            var valueSegment = value.Substring(profile.ValueStartPos - 1, profile.ValueLength);
            if (!long.TryParse(pluSegment, System.Globalization.CultureInfo.InvariantCulture, out var pluNumber)
                || !long.TryParse(valueSegment, System.Globalization.CultureInfo.InvariantCulture, out var valueNumber))
            {
                return Result.Fail<VariableWeightBarcodeResult>(new(
                    "WEIGHT_BARCODE_SEGMENT_INVALID",
                    "مقاطع PLU/القيمة داخل الباركود غير رقمية"));
            }

            var magnitude = decimal.One;
            for (var i = 0; i < profile.ValueDecimals; i++)
            {
                magnitude *= 10m;
            }
            var decoded = valueNumber / magnitude;

            return Result.Ok(new VariableWeightBarcodeResult(
                profile.ProfileId,
                profile.Name,
                pluNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                decoded,
                profile.ValueType,
                profile.ValueUnitId));
        }

        return Result.Fail<VariableWeightBarcodeResult>(new(
            "UNKNOWN_WEIGHT_BARCODE_FORMAT",
            "لا ملف نشط يطابق الباركود"));
    }

    /// <summary>
    /// رقم التحقق حسب قاعدة الملف — آخر خانة من الباركود:
    /// NONE بلا فحص · MOD10 مجموع الأرقام السابقة ÷ 10 · MOD10_WEIGHTED أوزان 3/1 (EAN).
    /// </summary>
    private static Result<bool> VerifyCheckDigit(string value, VariableWeightProfile profile)
    {
        if (profile.CheckDigitRule == "NONE")
        {
            return Result.Ok(true);
        }

        var body = value[..^1];
        var check = value[^1] - '0';
        var sum = 0;
        if (profile.CheckDigitRule == "MOD10")
        {
            foreach (var c in body)
            {
                sum += c - '0';
            }
        }
        else if (profile.CheckDigitRule == "MOD10_WEIGHTED")
        {
            var weight = 3;
            for (var i = body.Length - 1; i >= 0; i--)
            {
                sum += (body[i] - '0') * weight;
                weight = weight == 3 ? 1 : 3;
            }
        }
        else
        {
            return Result.Fail<bool>(new("WEIGHT_BARCODE_CHECK_RULE_UNKNOWN", "قاعدة رقم تحقق غير معروفة في الملف"));
        }

        var expected = (10 - sum % 10) % 10;
        if (expected != check)
        {
            return Result.Fail<bool>(new(
                "WEIGHT_BARCODE_CHECK_DIGIT_INVALID",
                $"رقم تحقق باركود الوزن غير صحيح (متوقع {expected})"));
        }
        return Result.Ok(true);
    }
}
