using WeraPro.Domain.Purchasing;
using WeraPro.Domain.Sales;
using WeraPro.Domain.SharedKernel;
using WeraPro.Domain.Weighing;
using Xunit;

namespace WeraPro.Domain.Tests;

/// <summary>
/// قواعد مجال المبيعات G7 (§9/§5/§15.4/§15.5) — أرقام مرجعية مستقلة معروفة
/// النتائج: لا يقارن الاختبار القيم بدالة الحساب نفسها التي يختبرها.
/// </summary>
public sealed class SalesPosDomainRuleTests
{
    private static readonly WeighingSettings Weighing = WeighingSettings.Default(); // 1g / خطوة 5g

    // ── ضريبة شاملة 14% (D-01 أ): الفصل من الأسعار المعروضة ─────────────────────

    [Theory]
    [InlineData(100.00, 12.28)]   // 100 × 0.14/1.14 = 12.2807 → Banker's 12.28
    [InlineData(70.00, 8.60)]     // 70 × 0.14/1.14 = 8.5965 → 8.60
    [InlineData(255.00, 31.32)]   // 255 × 0.14/1.14 = 31.3158 → 31.32
    public void ExtractTaxInclusive_Vat14_IndependentReference(decimal amountIncl, decimal expectedTax)
    {
        Assert.Equal(expectedTax, SalesMath.ExtractTaxInclusive(amountIncl, 0.1400m));
    }

    [Fact]
    public void ComputeLine_InclusiveTax_NetIsAfterDiscountExcludingTax()
    {
        // سطر 3 × 5.00 = 15.00 شامل، خصم سطر 1.00 → 14.00 شامل
        // ضريبة = 14 × 0.14/1.14 = 1.7193 → 1.72 · صافي دون ضريبة = 12.28
        var line = new SaleLineComputationInput(3m, 5.00m, 0.1400m, "TAXABLE", 1.00m, SalesTaxMode.VatInclusive);
        var calc = SalesMath.ComputeLine(line);
        Assert.Equal(15.00m, calc.Gross);
        Assert.Equal(1.72m, calc.TaxAmount);
        Assert.Equal(12.28m, calc.NetAmount);
        Assert.Equal(14.00m, calc.DueShare);
    }

    // ── توزيع خصم الفاتورة حتمياً: Σ الحصص = الخصم بالضبط (§16 ق5) ─────────────

    [Theory]
    [InlineData(0.01)]
    [InlineData(3.00)]
    [InlineData(13.57)]
    [InlineData(100.00)]
    public void DistributeInvoiceDiscount_SumIsExactOnAwkwardTotals(decimal discount)
    {
        var weights = new decimal[] { 14.00m, 51.30m, 8.77m, 132.00m, 3.10m }; // Σ = 209.17
        var shares = SalesMath.DistributeInvoiceDiscount(weights, discount);
        Assert.Equal(discount, MoneyRounding.ToCent(shares.Sum()));
        for (var i = 0; i < shares.Length; i++)
        {
            Assert.True(shares[i] <= weights[i] + 0.0001m, $"الحصة {i} تتجاوز وزن سطرها");
        }
    }

    [Fact]
    public void DistributeInvoiceDiscount_IsDeterministicRegardlessOfCallOrder()
    {
        var weights = new decimal[] { 33.33m, 33.33m, 33.34m };
        var a = SalesMath.DistributeInvoiceDiscount(weights, 1.00m);
        var b = SalesMath.DistributeInvoiceDiscount(weights, 1.00m);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ApplyInvoiceDiscountShare_TaxReflectedOnPostDiscountValue()
    {
        var line = SalesMath.ComputeLine(new SaleLineComputationInput(3m, 5.00m, 0.1400m, "TAXABLE", 0m, SalesTaxMode.VatInclusive));
        var after = SalesMath.ApplyInvoiceDiscountShare(line, 2.00m, 0.1400m, "TAXABLE", SalesTaxMode.VatInclusive);
        // 15.00 − 2.00 = 13.00 شامل → ضريبة 1.60 · صافي 11.40
        Assert.Equal(1.60m, after.TaxAmount);
        Assert.Equal(11.40m, after.NetAmount);
        Assert.Equal(13.00m, after.DueShare);
    }

    // ── المبلغ المستهدف (D-07 — اختبار البوابة 5): 200 ج.م/كجم × 70 = 350 جم ──

    [Fact]
    public void AmountTarget_200PerKg70Egp_Produces350GramsBeforeRoundingPolicy()
    {
        var policy = new SkuWeightPolicy(0.1m, 5m, 5, 1, true, true, 0m);
        var result = WeightSaleRules.AcceptAmountTarget(200m, 70m, policy, Weighing);
        Assert.True(result.IsSuccess);
        Assert.Equal(350m, result.Value!.DisplayWeightGrams);   // 350g على الخطوة بلا تعديل
        Assert.Equal(0.350m, result.Value.DisplayWeightGrams / 1000m);
        Assert.Equal(70.00m, result.Value.EffectiveAmount);      // القيمة المعاد حسابها Banker's
    }

    [Fact]
    public void Weight1275Kg_At200PerKg_Computes255Exactly()
    {
        // اختبار البوابة 6: وزن 1.275 كجم × 200 ج.م/كجم = 255.00 ج.م
        Assert.Equal(255.00m, WeightCalculator.AmountFromWeight(200m, 1275m, Weighing));
    }

    // ── قراءة الميزان: الحالات الأربع (اختبار البوابة 7) ────────────────────────

    [Fact]
    public void ScaleReading_FourStates_OnlyStableFreshConnectedAccepted()
    {
        var policy = new SkuWeightPolicy(0.1m, 5m, 5, 1, true, true, 0m);
        var now = DateTimeOffset.UtcNow;

        // 1) مستقرة + متصلة + حديثة → تُقبل
        var stable = WeightSaleRules.AcceptScaleReading(
            new ScaleReading(1.275m, IsStable: true, IsConnected: true, ReadAtUtc: now), policy, now);
        Assert.True(stable.IsSuccess);
        Assert.Equal(1.275m, stable.Value!);

        // 2) غير مستقرة → SCALE_UNSTABLE
        var unstable = WeightSaleRules.AcceptScaleReading(
            new ScaleReading(1.275m, IsStable: false, IsConnected: true, ReadAtUtc: now), policy, now);
        Assert.False(unstable.IsSuccess);
        Assert.Equal("SCALE_UNSTABLE", unstable.Error!.Code);

        // 3) قديمة (> 10 ثوانٍ) → SCALE_READING_STALE
        var stale = WeightSaleRules.AcceptScaleReading(
            new ScaleReading(1.275m, IsStable: true, IsConnected: true, ReadAtUtc: now.AddSeconds(-30)), policy, now);
        Assert.False(stale.IsSuccess);
        Assert.Equal("SCALE_READING_STALE", stale.Error!.Code);

        // 4) منقطعة → SCALE_DISCONNECTED
        var disconnected = WeightSaleRules.AcceptScaleReading(
            new ScaleReading(1.275m, IsStable: true, IsConnected: false, ReadAtUtc: now), policy, now);
        Assert.False(disconnected.IsSuccess);
        Assert.Equal("SCALE_DISCONNECTED", disconnected.Error!.Code);
    }

    [Fact]
    public void WeightBounds_MinMaxEnforced_NoZeroOrNegative()
    {
        var policy = new SkuWeightPolicy(0.1m, 5m, 5, 1, true, true, 0m);
        Assert.False(WeightSaleRules.AcceptWeightLabel(0.099m, policy).IsSuccess);   // تحت الحد
        Assert.False(WeightSaleRules.AcceptWeightLabel(5.001m, policy).IsSuccess);   // فوق الحد
        Assert.False(WeightSaleRules.AcceptWeightLabel(0m, policy).IsSuccess);       // صفر
        Assert.True(WeightSaleRules.AcceptWeightLabel(1.275m, policy).IsSuccess);
    }

    [Fact]
    public void QuickPick_ButtonsHiddenWhenOffItemStep()
    {
        var step5 = new SkuWeightPolicy(null, null, 5, 1, true, true, 0m);
        Assert.True(WeightSaleRules.QuickPickAllowed(350, step5));    // 350 % 5 == 0
        Assert.False(WeightSaleRules.QuickPickAllowed(333, step5));   // مخالف للخطوة — يخفى
        var step50 = new SkuWeightPolicy(null, null, 50, 1, true, true, 0m);
        Assert.True(WeightSaleRules.QuickPickAllowed(250, step50));   // 250 % 50 == 0
        Assert.False(WeightSaleRules.QuickPickAllowed(125, step50));  // 125 % 50 != 0 — يخفى
    }

    // ── الدفع والقسمة (§9.6) ────────────────────────────────────────────────────

    [Fact]
    public void PaymentPlan_SumMustEqualDue_SplitAllowed()
    {
        var ok = PaymentMath.PlanPayments(70.00m, new List<PaymentPart>
        {
            new(PaymentMethods.Cash, 40.00m),
            new(PaymentMethods.Card, 30.00m),
        });
        Assert.True(ok.IsSuccess);
        Assert.Equal(70.00m, ok.Value!.TotalApplied);
        Assert.True(ok.Value.HasCash);
    }

    [Fact]
    public void PaymentPlan_MismatchRejected_NoSilentShortfall()
    {
        var fail = PaymentMath.PlanPayments(70.00m, new List<PaymentPart> { new(PaymentMethods.Cash, 60.00m) });
        Assert.False(fail.IsSuccess);
        Assert.Equal("PAYMENT_TOTAL_MISMATCH", fail.Error!.Code);
    }

    [Fact]
    public void PaymentPlan_SingleCreditPartOnly()
    {
        var twoCredit = PaymentMath.PlanPayments(100m, new List<PaymentPart>
        {
            new(PaymentMethods.Credit, 60m),
            new(PaymentMethods.Credit, 40m),
        });
        Assert.False(twoCredit.IsSuccess);
        Assert.Equal("CREDIT_PART_DUPLICATED", twoCredit.Error!.Code);
    }

    // ── حد الائتمان (D-05 — اختبار البوابة 4) ─────────────────────────────────

    [Fact]
    public void CreditLimit_OutstandingPlusNewMustNotExceedLimit()
    {
        Assert.True(CreditRules.CheckLimit(300m, 500m, 200m).IsSuccess);      // 300+200 = 500 ≤ 500
        var over = CreditRules.CheckLimit(300m, 500m, 200.01m);
        Assert.False(over.IsSuccess);
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", over.Error!.Code);
    }

    // ── آلات الحالة (مرايا مشغلات V005) ────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT", "POSTED", true)]
    [InlineData("DRAFT", "VOID", true)]
    [InlineData("POSTED", "PARTIAL_RETURN", true)]
    [InlineData("POSTED", "FULL_RETURN", true)]
    [InlineData("POSTED", "VOID", false)]      // المرحلة لا تُلغى — تُعكس بمرتجع (§9.2)
    [InlineData("VOID", "POSTED", false)]
    public void InvoiceStateMachine_MirrorsV005Transitions(string from, string to, bool allowed)
        => Assert.Equal(allowed, InvoiceStateMachine.CanTransition(from, to));

    // ── WAC: الصرف والإرجاع (§15.3) ─────────────────────────────────────────────

    [Fact]
    public void WacIssue_DecreasesQtyKeepsAvg()
    {
        var after = WacMath.ApplyIssue(new WacState(120, 2.0000m), 60);
        Assert.Equal(60, after.QtyOnLayer);
        Assert.Equal(2.0000m, after.AvgUnitCost);
    }

    [Fact]
    public void WacReturn_IncreasesQtyKeepsAvg()
    {
        var after = WacMath.ApplyReturn(new WacState(60, 2.0000m), 12);
        Assert.Equal(72, after.QtyOnLayer);
        Assert.Equal(2.0000m, after.AvgUnitCost);
    }

    [Fact]
    public void WacIssue_ApprovedNegative_GoesNegativeWithSameAvg()
    {
        // البيع المعتمد بمخزون سالب (D-03): الطبقة تسجل السالب بالمتوسط نفسه —
        // والتقييم لا يحتسب إلا ما يُملك (نمط G6 الموثق).
        var after = WacMath.ApplyIssue(new WacState(10, 2m), 11);
        Assert.Equal(-1, after.QtyOnLayer);
        Assert.Equal(2.0000m, after.AvgUnitCost);
    }

    // ── توجيه الإيصال §29.4 — القرار الحرفي ─────────────────────────────────────

    [Fact]
    public void ReceiptRouting_Verified80_RoutesRoll80Template()
    {
        var decision = ReceiptRouting.Resolve(new StationReceiptPrinter(1, "ROLL_80", 72.0m, 72.0m, IsCalibrated: true, IsAutoRoutingEnabled: true));
        Assert.True(decision.IsSuccess);
        Assert.Equal("ROLL_80", decision.Value!.ResolvedMediaClass);
    }

    [Fact]
    public void ReceiptRouting_Verified58_RoutesRoll58Template()
    {
        var decision = ReceiptRouting.Resolve(new StationReceiptPrinter(2, "ROLL_58", 48.0m, 48.0m, IsCalibrated: true, IsAutoRoutingEnabled: true));
        Assert.True(decision.IsSuccess);
        Assert.Equal("ROLL_58", decision.Value!.ResolvedMediaClass);
    }

    [Fact]
    public void ReceiptRouting_UnverifiedMedia_HoldsNeverGuessesAndNeverA4()
    {
        var unverified = ReceiptRouting.Resolve(new StationReceiptPrinter(3, "ROLL_80", 72.0m, null, IsCalibrated: false, IsAutoRoutingEnabled: true));
        Assert.False(unverified.IsSuccess);
        Assert.Equal("HOLD_MEDIA_UNVERIFIED", unverified.Error!.Code);
        // ولا A4 تلقائياً في أي حال — القرار إما رول معتمد أو HOLD
        Assert.DoesNotContain("A4", unverified.Error.Code);
    }

    [Fact]
    public void ReceiptRouting_NoPrinterAssigned_Holds()
    {
        var none = ReceiptRouting.Resolve(null);
        Assert.False(none.IsSuccess);
        Assert.Equal("HOLD_PRINTER_NOT_ASSIGNED", none.Error!.Code);
    }

    [Fact]
    public void ReceiptRouting_AutoRoutingDisabled_HoldsUntilCalibrationApproval()
    {
        var disabled = ReceiptRouting.Resolve(new StationReceiptPrinter(4, "ROLL_80", 72.0m, 72.0m, IsCalibrated: true, IsAutoRoutingEnabled: false));
        Assert.False(disabled.IsSuccess);
        Assert.Equal("HOLD_ROUTING_DISABLED", disabled.Error!.Code);
    }

    // ── مصادر الوزن لا تختلط (§5.1) ────────────────────────────────────────────

    [Fact]
    public void DerivedWeight_NeverPosedAsScaleReading()
    {
        Assert.True(WeightSources.IsDerived(WeightSources.AmountDerived));
        Assert.False(WeightSources.IsDerived(WeightSources.ScaleReading));
        Assert.False(WeightSources.IsDerived(WeightSources.ManualWeight));
    }
}
