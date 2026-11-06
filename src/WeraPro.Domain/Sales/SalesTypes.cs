using WeraPro.Domain.SharedKernel;
using WeraPro.Domain.Weighing;

namespace WeraPro.Domain.Sales;

/// <summary>
/// قواعد مجال المبيعات ونقطة البيع (G7 — §9/§5/§15.4/§15.5/§11.3).
/// كل الحسابات عشرية دقيقة (decimal) — لا float مطلقاً (§0.1 ق5)، والضريبة
/// تُفصل من الأسعار الشاملة (D-01 أ) أو تُضاف فوقها وفق لقطة السياسة، والخصم
/// الموزَّع حتمي (§16 ق5)، والوزن بمصدر مسجل دائماً (§5.1).
/// </summary>
public static class SalesTaxMode
{
    public const string VatInclusive = "VAT_INCLUSIVE";
    public const string VatExclusive = "VAT_EXCLUSIVE";
    public const string ZeroTax = "ZERO_TAX";

    public static bool IsValid(string mode) =>
        mode is VatInclusive or VatExclusive or ZeroTax;
}

public static class SaleModes
{
    public const string Count = "COUNT";
    public const string Weight = "WEIGHT";
    public const string Pack = "PACK";
    public const string Serialized = "SERIALIZED";
}

public static class WeightSources
{
    public const string ScaleReading = "SCALE_READING";
    public const string QuickPick = "QUICK_PICK";
    public const string ManualWeight = "MANUAL_WEIGHT";
    public const string AmountDerived = "AMOUNT_DERIVED";
    public const string WeightLabel = "WEIGHT_LABEL";
    public const string PackPreweighed = "PACK_PREWEIGHED";

    public static bool IsValid(string source) =>
        source is ScaleReading or QuickPick or ManualWeight or AmountDerived or WeightLabel or PackPreweighed;

    /// <summary>المصادر المشتقة لا تُعرض أبداً كقراءة ميزان (§5.1).</summary>
    public static bool IsDerived(string source) => source is AmountDerived;
}

public static class PaymentMethods
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string SplitCash = "SPLIT_CASH";
    public const string SplitCard = "SPLIT_CARD";
    public const string Credit = "CREDIT";
    public const string Other = "OTHER";
}

public static class RefundMethods
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string CreditReversal = "CREDIT_REVERSAL";
}

// ═══════════════ آلات الحالة (V005 — نفس الانتقالات التي تفرضها مشغلات القاعدة) ═══

public static class InvoiceStateMachine
{
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        ("DRAFT", "POSTED") => true,          // الترحيل: تحقق مشغل V005 الكامل (سطور/مجاميع/تكلفة/وزن/مدفوع)
        ("DRAFT", "VOID") => true,            // إلغاء قبل الترحيل بسبب (§9.2)
        ("POSTED", "PARTIAL_RETURN") => true, // أثر مرتجع جزئي فقط — الأرقام لا تتغير (مشغل V005)
        ("POSTED", "FULL_RETURN") => true,
        _ => false
    };
}

public static class RefundStateMachine
{
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        ("DRAFT", "POSTED") => true,
        ("DRAFT", "CANCELLED") => true,
        _ => false
    };
}

// ═══════════════ حساب السطر والمجاميع (§16 ق4/ق5 · FINANCIAL_DEFINITIONS) ═════

/// <summary>مدخلات سطر بيع كما حسمها الخادم بعد إعادة التسعير (لا يُثق بأسعار العميل أبداً — §9.0 ق10).</summary>
public sealed record SaleLineComputationInput(
    decimal Qty,
    decimal UnitPrice,            // سعر وحدة البيع (شامل الضريبة عند VAT_INCLUSIVE)
    decimal TaxRate,              // 0.1400
    string TaxTreatment,          // TAXABLE | ZERO_RATED | EXEMPT
    decimal LineDiscount,         // مبلغ خصم السطر (قيمة لا نسبة)
    string TaxMode);

/// <summary>نتيجة حساب السطر — كل القيم بقرشين (Banker's) والمجموع محفوظ حتمياً.</summary>
public sealed record ComputedSaleLine(
    decimal Gross,                // qty × unit_price
    decimal TaxAmount,            // الضريبة المفصولة (أو المضافة) وفق السياسة
    decimal NetAmount,            // بعد الخصومات ودون الضريبة (تعريف «صافي المبيعات»)
    decimal DueShare);            // نصيب السطر من المستحق = بعد الخصومات شاملاً الضريبة

public static class SalesMath
{
    /// <summary>ضريبة سطر شامل: tax = round(dueAfterDiscounts × r ÷ (1+r), Banker's) — D-01 أ.</summary>
    public static decimal ExtractTaxInclusive(decimal amountIncl, decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        if (rate == 0) return 0m;
        return MoneyRounding.ToCent(amountIncl * rate / (1m + rate));
    }

    /// <summary>ضريبة سطر فاصل: tax = round(base × r, Banker's) — D-01 ب (تُضاف فوق السعر).</summary>
    public static decimal ComputeTaxExclusive(decimal amountExcl, decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        return MoneyRounding.ToCent(amountExcl * rate);
    }

    /// <summary>حساب سطر كامل (§16 ق4/ق5): الإجمالي ثم خصم السطر ثم الضريبة وفق لقطة السياسة.</summary>
    public static ComputedSaleLine ComputeLine(in SaleLineComputationInput line)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line.Qty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line.UnitPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(line.LineDiscount);

        decimal gross = MoneyRounding.ToCent(line.Qty * line.UnitPrice);
        decimal afterLineDiscount = gross - line.LineDiscount;
        if (afterLineDiscount < 0)
            throw new InvalidOperationException("LINE_DISCOUNT_EXCEEDS_GROSS: خصم السطر يتجاوز قيمة السطر");

        decimal tax = line.TaxTreatment switch
        {
            "TAXABLE" => line.TaxMode == SalesTaxMode.VatInclusive
                ? ExtractTaxInclusive(afterLineDiscount, line.TaxRate)
                : line.TaxMode == SalesTaxMode.VatExclusive
                    ? ComputeTaxExclusive(afterLineDiscount, line.TaxRate)
                    : 0m,
            "ZERO_RATED" or "EXEMPT" => 0m,
            _ => throw new InvalidOperationException($"UNKNOWN_TAX_TREATMENT: {line.TaxTreatment}")
        };

        decimal dueShare = line.TaxMode == SalesTaxMode.VatExclusive ? afterLineDiscount + tax : afterLineDiscount;
        decimal netAmount = line.TaxMode == SalesTaxMode.VatExclusive
            ? MoneyRounding.ToCent(afterLineDiscount)
            : MoneyRounding.ToCent(afterLineDiscount - tax);

        return new ComputedSaleLine(Gross: gross, TaxAmount: tax, NetAmount: netAmount, DueShare: dueShare);
    }

    /// <summary>
    /// توزيع خصم الفاتورة على السطور حتمياً (§16 ق5): حصص بمقياس dueShare لكل سطر
    /// بطريقة البواقي الكبرى (Largest Remainder) بحيث Σ الحصص = الخصم بالضبط
    /// (بلا فروق ضائعة) — نفس الحصص تُخزن invoice_discount_share في السطور.
    /// </summary>
    public static decimal[] DistributeInvoiceDiscount(IReadOnlyList<decimal> lineWeights, decimal invoiceDiscount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(invoiceDiscount);
        if (invoiceDiscount == 0) return new decimal[lineWeights.Count];

        decimal totalWeight = lineWeights.Sum();
        if (totalWeight <= 0)
            throw new InvalidOperationException("INVOICE_DISCOUNT_NO_BASE: لا أساس لتوزيع خصم الفاتورة");
        if (invoiceDiscount > totalWeight)
            throw new InvalidOperationException("INVOICE_DISCOUNT_EXCEEDS_TOTAL: خصم الفاتورة يتجاوز قيمة الفاتورة");

        var exact = lineWeights.Select(w => invoiceDiscount * w / totalWeight).ToArray();
        var shares = exact.Select(MoneyRounding.ToCent).ToArray();
        decimal remainder = invoiceDiscount - shares.Sum();

        // بواقي كبرى: وزّع فرق التقريب قرشاً قرشاً على أكبر الكسور المتجاهلة — حتمي ومستقل عن ترتيب السطور
        var order = Enumerable.Range(0, lineWeights.Count)
            .OrderByDescending(i => exact[i] - MoneyRounding.ToCent(exact[i]))
            .ThenBy(i => i)
            .ToList();
        int idx = 0;
        while (remainder > 0.001m)
        {
            shares[order[idx % order.Count]] += 0.01m;
            remainder -= 0.01m;
            idx++;
        }
        while (remainder < -0.001m)
        {
            shares[order[idx % order.Count]] -= 0.01m;
            remainder += 0.01m;
            idx++;
        }
        return shares;
    }

    /// <summary>
    /// تطبيق حصة سطر من خصم الفاتورة (§16 ق5): الضريبة تُعاد فصلها على القيمة
    /// بعد الخصم — الحصة محفوظة في السطر (invoice_discount_share) فيستقيم المجموع.
    /// </summary>
    public static ComputedSaleLine ApplyInvoiceDiscountShare(
        in ComputedSaleLine line, decimal invoiceDiscountShare,
        decimal taxRate, string taxTreatment, string taxMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(invoiceDiscountShare);
        if (taxTreatment != "TAXABLE")
            return new ComputedSaleLine(
                line.Gross, 0m,
                MoneyRounding.ToCent(line.DueShare - invoiceDiscountShare),
                MoneyRounding.ToCent(line.DueShare - invoiceDiscountShare));

        if (taxMode == SalesTaxMode.VatExclusive)
        {
            // الخصم مبلغ شامل الضريبة يُنزَّل من المستحق: نصيبه دون الضريبة = share ÷ (1+r)
            var shareExcl = invoiceDiscountShare / (1m + taxRate);
            var afterExcl = line.DueShare - shareExcl;
            var tax = ComputeTaxExclusive(afterExcl, taxRate);
            return new ComputedSaleLine(line.Gross, tax, MoneyRounding.ToCent(afterExcl), MoneyRounding.ToCent(afterExcl + tax));
        }

        var after = line.DueShare - invoiceDiscountShare;   // شامل الضريبة
        var taxIncl = ExtractTaxInclusive(after, taxRate);
        return new ComputedSaleLine(
            line.Gross,
            taxIncl,
            MoneyRounding.ToCent(after - taxIncl),
            MoneyRounding.ToCent(after));
    }

    /// <summary>مجاميع الفاتورة من السطور المحسوبة (تخضع لتحقق مشغل V005: net_total = Σ net_amount).</summary>
    public static SaleTotals ComputeTotals(
        IReadOnlyList<ComputedSaleLine> lines,
        decimal invoiceDiscount,
        decimal paidTotal)
    {
        decimal gross = MoneyRounding.ToCent(lines.Sum(l => l.Gross));
        decimal tax = MoneyRounding.ToCent(lines.Sum(l => l.TaxAmount));
        decimal net = MoneyRounding.ToCent(lines.Sum(l => l.NetAmount));
        decimal dueShareTotal = MoneyRounding.ToCent(lines.Sum(l => l.DueShare));
        if (invoiceDiscount < 0 || invoiceDiscount > dueShareTotal)
            throw new InvalidOperationException("INVOICE_DISCOUNT_EXCEEDS_TOTAL: خصم الفاتورة يتجاوز قيمة الفاتورة");
        decimal due = MoneyRounding.ToCent(dueShareTotal - invoiceDiscount);
        return new SaleTotals(
            GrossTotal: gross,
            LineDiscountTotal: MoneyRounding.ToCent(gross - dueShareTotal),
            InvoiceDiscount: invoiceDiscount,
            TaxTotal: tax,
            NetTotal: net,
            DueAmount: due,
            PaidTotal: paidTotal,
            ChangeDue: paidTotal > due ? MoneyRounding.ToCent(paidTotal - due) : 0m);
    }
}

public sealed record SaleTotals(
    decimal GrossTotal,
    decimal LineDiscountTotal,
    decimal InvoiceDiscount,
    decimal TaxTotal,
    decimal NetTotal,
    decimal DueAmount,   // المستحق على العميل (بعد الخصومات شاملاً الضريبة)
    decimal PaidTotal,
    decimal ChangeDue);  // الفكة للعميل نقداً (تُميَّز عن «مستحق على العميل» — §9.1F)

// ═══════════════ الدفع والقسمة (§9.6 · §15.4) ═══════════════════════════════

public sealed record PaymentPart(string Method, decimal Amount);

public static class PaymentMath
{
    /// <summary>
    /// خطة الدفع (نقد/بطاقة/مختلط/آجل): كل جزء موجب، Σ الأجزاء = المستحق (بتفاوت
    /// تقريب ≤ 0.005)، جزء آجل واحد على الأكثر، ولا تُعلن الفاتورة مدفوعة إن نقص المجموع.
    /// </summary>
    public static Result<PaymentPlan> PlanPayments(decimal due, IReadOnlyList<PaymentPart> parts)
    {
        if (parts.Count == 0)
            return Result.Fail<PaymentPlan>(new("PAYMENT_EMPTY", "لا مدفوعات في الفاتورة"));
        if (parts.Any(p => p.Amount <= 0))
            return Result.Fail<PaymentPlan>(new("PAYMENT_AMOUNT_INVALID", "مبلغ الدفع يجب أن يكون موجباً"));
        if (parts.Count(p => p.Method == PaymentMethods.Credit) > 1)
            return Result.Fail<PaymentPlan>(new("CREDIT_PART_DUPLICATED", "لا يجوز أكثر من جزء آجل واحد في الفاتورة"));
        if (parts.Any(p => p.Method is not (PaymentMethods.Cash or PaymentMethods.Card or PaymentMethods.Credit)))
            return Result.Fail<PaymentPlan>(new("PAYMENT_METHOD_UNKNOWN", "وسيلة دفع غير معروفة"));

        decimal sum = MoneyRounding.ToCent(parts.Sum(p => p.Amount));
        if (Math.Abs(sum - due) > 0.005m)
            return Result.Fail<PaymentPlan>(new("PAYMENT_TOTAL_MISMATCH",
                $"مجموع الدفعات ({sum:0.00}) لا يطابق المستحق ({due:0.00})"));

        return Result.Ok(new PaymentPlan(
            Parts: parts,
            TotalApplied: sum,
            HasCredit: parts.Any(p => p.Method == PaymentMethods.Credit),
            HasCash: parts.Any(p => p.Method == PaymentMethods.Cash)));
    }
}

public sealed record PaymentPlan(
    IReadOnlyList<PaymentPart> Parts,
    decimal TotalApplied,
    bool HasCredit,
    bool HasCash);

// ═══════════════ البيع الآجل وحد الائتمان (§9.4/§9.6 · D-05) ═════════════════

public static class CreditRules
{
    /// <summary>حد الائتمان: (المستحق القائم + الجديد) ≤ الحد — وإلا رفض صريح لا تخفيض صامت.</summary>
    public static Result<bool> CheckLimit(decimal outstandingCredit, decimal creditLimit, decimal additionalAmount)
    {
        if (additionalAmount <= 0)
            return Result.Fail<bool>(new("CREDIT_AMOUNT_INVALID", "مبلغ الآجل يجب أن يكون موجباً"));
        if (outstandingCredit + additionalAmount > creditLimit + 0.0001m)
            return Result.Fail<bool>(new("CREDIT_LIMIT_EXCEEDED",
                $"البيع الآجل يتجاوز حد ائتمان العميل (المستحق {outstandingCredit:0.00} + الجديد {additionalAmount:0.00} > الحد {creditLimit:0.00})"));
        return Result.Ok(true);
    }
}

// ═══════════════ قواعد البيع الوزني (§5.1/§5.2/§9.3 — الطرق الست) ═════════════

/// <summary>سياسة وزن الصنف كما عُرِّفت في الكتالوج (V002) وإعدادات الوزن (D-07).</summary>
public sealed record SkuWeightPolicy(
    decimal? MinWeightKg,
    decimal? MaxWeightKg,
    int StepGrams,
    int PrecisionGrams,
    bool AllowManualWeight,
    bool AllowAmountTarget,
    decimal DefaultTareKg);

/// <summary>قراءة ميزان خام بحالتها الصادقة (§9.3 ق1) — لا تُقبل قديمة/غير مستقرة/منقطعة.</summary>
public sealed record ScaleReading(
    decimal WeightKg,
    bool IsStable,
    bool IsConnected,
    DateTimeOffset ReadAtUtc,
    decimal? TareKg = null);

public static class WeightSaleRules
{
    /// <summary>أقصى عمر مقبول لقراءة الميزان (ثوانٍ) — بعده «قديمة» تُرفض (§5.2 ق5).</summary>
    public const int MaxReadingAgeSeconds = 10;

    private static Result<bool> ValidateBounds(decimal weightKg, in SkuWeightPolicy policy)
    {
        if (weightKg <= 0)
            return Result.Fail<bool>(new("WEIGHT_NOT_POSITIVE", "الوزن يجب أن يكون موجباً"));
        if (policy.MinWeightKg is { } min && weightKg < min - 0.0005m)
            return Result.Fail<bool>(new("WEIGHT_BELOW_MIN", $"الوزن {weightKg:0.###} كجم أقل من حد الصنف {min:0.###} كجم"));
        if (policy.MaxWeightKg is { } max && weightKg > max + 0.0005m)
            return Result.Fail<bool>(new("WEIGHT_ABOVE_MAX", $"الوزن {weightKg:0.###} كجم يتجاوز حد الصنف {max:0.###} كجم"));
        return Result.Ok(true);
    }

    /// <summary>
    /// الطريقة 1 — قراءة ميزان حية: مستقرة + متصلة + حديثة فقط؛ الرفض موثق برمز لكل حالة
    /// (اختبار البوابة 7: مستقرة/غير مستقرة/قديمة/منقطعة — الأربع مختبرة).
    /// </summary>
    public static Result<decimal> AcceptScaleReading(in ScaleReading reading, in SkuWeightPolicy policy, DateTimeOffset nowUtc)
    {
        if (!reading.IsConnected)
            return Result.Fail<decimal>(new("SCALE_DISCONNECTED", "الميزان غير متصل — لا تُقبل قراءة"));
        if (!reading.IsStable)
            return Result.Fail<decimal>(new("SCALE_UNSTABLE", "قراءة الميزان غير مستقرة — أعد القراءة"));
        if (nowUtc - reading.ReadAtUtc > TimeSpan.FromSeconds(MaxReadingAgeSeconds))
            return Result.Fail<decimal>(new("SCALE_READING_STALE", "قراءة الميزان قديمة — لا تُقبل"));
        var bounds = ValidateBounds(reading.WeightKg, policy);
        if (!bounds.IsSuccess)
            return Result.Fail<decimal>(bounds.Error!);
        return Result.Ok(decimal.Round(reading.WeightKg, 3, MidpointRounding.ToEven));
    }

    /// <summary>الطريقة 2 — إدخال يدوي مصرح: يسجل MANUAL_WEIGHT ولا يوصف كقراءة ميزان (§9.3 ق2).</summary>
    public static Result<decimal> AcceptManualWeight(decimal weightKg, in SkuWeightPolicy policy, bool allowedBySetting)
    {
        if (!allowedBySetting)
            return Result.Fail<decimal>(new("MANUAL_WEIGHT_NOT_ALLOWED", "الإدخال اليدوي للوزن غير مصرح لهذا الصنف"));
        var bounds = ValidateBounds(weightKg, policy);
        return bounds.IsSuccess
            ? Result.Ok(decimal.Round(weightKg, 3, MidpointRounding.ToEven))
            : Result.Fail<decimal>(bounds.Error!);
    }

    /// <summary>
    /// الطريقة 3 — مبلغ مستهدف: الوزن الحسابي من WeightCalculator (D-07: 200 ج.م/كجم × 70 = 350 جم)
    /// والمصدر AMOUNT_DERIVED لا يُعرض كقراءة ميزان (§5.1).
    /// </summary>
    public static Result<WeightFromAmountResult> AcceptAmountTarget(
        decimal pricePerKg, decimal targetAmount, in SkuWeightPolicy policy, in WeighingSettings settings)
    {
        if (!policy.AllowAmountTarget)
            return Result.Fail<WeightFromAmountResult>(new("AMOUNT_TARGET_NOT_ALLOWED", "الصنف لا يسمح بالبيع بمبلغ مستهدف"));
        if (targetAmount <= 0)
            return Result.Fail<WeightFromAmountResult>(new("AMOUNT_TARGET_INVALID", "المبلغ المستهدف يجب أن يكون موجباً"));

        var calc = WeightCalculator.WeightFromAmount(pricePerKg, targetAmount, settings);
        if (calc.DisplayWeightGrams <= 0)
            return Result.Fail<WeightFromAmountResult>(new("AMOUNT_TARGET_INVALID", "المبلغ المستهدف أصغر من أدنى وزن قابل للبيع"));
        decimal weightKg = calc.DisplayWeightGrams / 1000m;
        var bounds = ValidateBounds(weightKg, policy);
        return bounds.IsSuccess
            ? Result.Ok(calc)
            : Result.Fail<WeightFromAmountResult>(new("WEIGHT_OUT_OF_ITEM_BOUNDS",
                $"الوزن الحسابي {weightKg:0.###} كجم خارج حدود الصنف"));
    }

    /// <summary>الطريقة 4 — وزن سريع: يخفى الزر المخالف لخطوة الصنف/دقته (QUICK_PICK مصدر مسجل).</summary>
    public static bool QuickPickAllowed(int quickGrams, in SkuWeightPolicy policy) =>
        quickGrams > 0 && quickGrams % Math.Max(1, policy.StepGrams) == 0;

    /// <summary>الطرق 5/6 — ملصق الوزن والعبوة الموزونة: حدود الصنف مفروضة بعد التحليل المعتمد.</summary>
    public static Result<decimal> AcceptWeightLabel(decimal parsedWeightKg, in SkuWeightPolicy policy)
    {
        var bounds = ValidateBounds(parsedWeightKg, policy);
        return bounds.IsSuccess
            ? Result.Ok(decimal.Round(parsedWeightKg, 3, MidpointRounding.ToEven))
            : Result.Fail<decimal>(bounds.Error!);
    }
}

// ═══════════════ توجيه إيصال العميل تلقائياً (§29.4/§29.9 — بند بوابة G7) ══════

/// <summary>تعيين الطابعة للمحطة لدور مستند إيصال العميل — من print.printer_document_roles/profiles.</summary>
public sealed record StationReceiptPrinter(
    long PrinterProfileId,
    string MediaClass,             // ROLL_80 | ROLL_58
    decimal PrintableWidthMm,
    decimal? VerifiedMediaWidthMm, // بعد المعايرة (G10) — NULL = غير معاير
    bool IsCalibrated,             // معايرة الوسيط شرط التوجيه التلقائي (§29.7)
    bool IsAutoRoutingEnabled);

/// <summary>قرار التوجيه: قالب الوسيط المعتمد أو HOLD — لا تخمين أبداً ولا A4 تلقائياً.</summary>
public sealed record ReceiptRoutingDecision(
    string ResolvedMediaClass,     // ROLL_80 | ROLL_58
    string? HoldReasonCode);

public static class ReceiptRouting
{
    /// <summary>
    /// قاعدة §29.4 حرفياً: وسيط معاير 80 → قالب 80 المعتمد؛ 58 → 58؛ مجهول/غير معاير/
    /// تعارض → HOLD (يطلب مسؤولاً يعاير مرة واحدة)؛ A4 لا يُختار تلقائياً أبداً.
    /// </summary>
    public static Result<ReceiptRoutingDecision> Resolve(StationReceiptPrinter? printer)
    {
        if (printer is null)
            return Result.Fail<ReceiptRoutingDecision>(new("HOLD_PRINTER_NOT_ASSIGNED",
                "لا طابعة معتمدة لدور إيصال العميل لهذه المحطة — الإيصال يبقى محفوظاً ولا تُنشأ مهمة حتى يعين مسؤول طابعة معايرة (HOLD §29.4)"));
        if (!printer.IsAutoRoutingEnabled)
            return Result.Fail<ReceiptRoutingDecision>(new("HOLD_ROUTING_DISABLED",
                "التوجيه التلقائي معطل لهذا التعيين حتى المعايرة والاعتماد (HOLD §29.4)"));
        if (!printer.IsCalibrated || printer.VerifiedMediaWidthMm is null)
            return Result.Fail<ReceiptRoutingDecision>(new("HOLD_MEDIA_UNVERIFIED",
                "وسيط الطابعة غير معاير — التوجيه موقوف حتى يعايره مسؤول مرة واحدة (HOLD §29.4 — لا تخمين لمقاس)"));

        // وسيط معاير: قالب المستند المستقل لوسيطه
        if (printer.VerifiedMediaWidthMm > 70m)   // رول 80mm فعلي (~72mm قابل للطباعة)
            return Result.Ok(new ReceiptRoutingDecision("ROLL_80", null));
        if (printer.VerifiedMediaWidthMm >= 40m)  // رول 58mm فعلي (~48mm قابل للطباعة)
            return Result.Ok(new ReceiptRoutingDecision("ROLL_58", null));
        return Result.Fail<ReceiptRoutingDecision>(new("HOLD_MEDIA_UNVERIFIED",
            "عرض الوسيط المعاير لا يطابق رولاً معتمداً (80/58) — تعارض يتطلب مسؤولاً (HOLD §29.4)"));
    }
}
