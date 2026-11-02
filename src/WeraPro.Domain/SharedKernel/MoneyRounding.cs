namespace WeraPro.Domain.SharedKernel;

/// <summary>
/// تقريب المبلغ النقدي — قاعدة Banker's Rounding (ROUND_HALF_EVEN) الملزمة
/// بحسم D-07 (أ) لأقرب قرش 0.01 EGP. المصدر: docs/WEIGHT_AND_UNIT_RULES.md.
/// مثال مرجعي ملزم: 200 EGP/kg × مبلغ 70 EGP → وزن 350g (على الخطوة بلا تعديل).
/// </summary>
public static class MoneyRounding
{
    /// <summary>تقريب مبلغ لأقرب قرش (0.01) بقاعدة Banker's Rounding.</summary>
    public static decimal ToCent(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);

    /// <summary>تقريب لأقرب خطوة معطاة بقاعدة Banker's Rounding.</summary>
    public static decimal ToStep(decimal value, decimal step)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), "الخطوة يجب أن تكون موجبة");
        }
        return Math.Round(value / step, 0, MidpointRounding.ToEven) * step;
    }
}
