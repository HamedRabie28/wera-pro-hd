using WeraPro.Domain.SharedKernel;

namespace WeraPro.Domain.Weighing;

/// <summary>
/// إعدادات الوزن المعتمدة بحسم D-07 (أ) — تُقرأ من core.business_settings
/// (weighing.precision_g / weighing.step_g / weighing.amount_rounding /
/// weighing.weight_rounding) لا من ثوابت كود. المثال المرجعي الملزم:
/// 200 EGP/kg × مبلغ مستهدف 70 EGP → 350g (على الخطوة بلا تعديل).
/// </summary>
public sealed record WeighingSettings(
    int PrecisionGrams,
    int StepGrams,
    AmountRounding AmountRounding,
    WeightRounding WeightRounding)
{
    public static WeighingSettings Default() => new(
        PrecisionGrams: 1,
        StepGrams: 5,
        AmountRounding: AmountRounding.BankersToCent,
        WeightRounding: WeightRounding.NearestStep);
}

public enum AmountRounding { BankersToCent }
public enum WeightRounding { NearestStep }

/// <summary>
/// حاسبة الوزن/المبلغ المرجعية (D-07) — التنفيذ القانوني الوحيد الذي تستخدمه
/// نافذة الوزن في G7 والقوالب. كل المدخلات والمخرجات عشرية دقيقة (decimal).
/// </summary>
public static class WeightCalculator
{
    /// <summary>
    /// وزن ناتج من مبلغ مستهدف: وزن_خام = مبلغ ÷ (سعر_الكيلو ÷ 1000)
    /// ثم تقريب لأقرب خطوة معتمدة (NEAREST_STEP).
    /// المرجع: 200 EGP/kg × 70 EGP → 350g.
    /// </summary>
    public static WeightFromAmountResult WeightFromAmount(
        decimal pricePerKg, decimal targetAmount, WeighingSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pricePerKg);
        ArgumentOutOfRangeException.ThrowIfNegative(targetAmount);

        decimal pricePerGram = pricePerKg / 1000m;
        decimal rawWeightGrams = targetAmount / pricePerGram;
        decimal stepGrams = settings.StepGrams;
        decimal roundedGrams = MoneyRounding.ToStep(rawWeightGrams, stepGrams);

        // القيمة الدقيقة تُحفظ مع القيمة المعروضة معاً (حسم D-07).
        return new WeightFromAmountResult(
            ExactWeightGrams: decimal.Round(rawWeightGrams, 3, MidpointRounding.ToEven),
            DisplayWeightGrams: roundedGrams,
            EffectiveAmount: MoneyRounding.ToCent(roundedGrams * pricePerGram));
    }

    /// <summary>
    /// مبلغ ناتج من وزن: مبلغ_خام = وزن(جم) × سعر_الكيلو ÷ 1000
    /// ثم تقريب Banker's Rounding لأقرب قرش (ROUND_HALF_EVEN_0.01).
    /// </summary>
    public static decimal AmountFromWeight(decimal pricePerKg, decimal weightGrams, WeighingSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pricePerKg);
        ArgumentOutOfRangeException.ThrowIfNegative(weightGrams);

        decimal rawAmount = weightGrams * pricePerKg / 1000m;
        return settings.AmountRounding == AmountRounding.BankersToCent
            ? MoneyRounding.ToCent(rawAmount)
            : rawAmount;
    }
}

/// <summary>نتيجة حساب الوزن من المبلغ — الدقيقة والمعروضة معاً (D-07: تُحفظان معاً).</summary>
public sealed record WeightFromAmountResult(
    decimal ExactWeightGrams,
    decimal DisplayWeightGrams,
    decimal EffectiveAmount);
