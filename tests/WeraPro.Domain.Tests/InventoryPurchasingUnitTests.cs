namespace WeraPro.Domain.Tests;

using WeraPro.Domain.Catalog;
using WeraPro.Domain.Inventory;
using WeraPro.Domain.Purchasing;
using Xunit;

/// <summary>
/// اختبارات قواعد مجال G6 الصرفة (§12.1/§12.2/§12.3): آلات الحالة ورياضة
/// التسوية وWAC وقواعد التوزيع والحجز وإعادة الطلب — بلا أي اعتماد خارجي.
/// </summary>
public sealed class InventoryDomainRuleTests
{
    // ── آلات حالة التحويل والجرد ────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT", true)]
    [InlineData("SENT", false)]
    [InlineData("RECEIVED", false)]
    [InlineData("CANCELLED", false)]
    public void Transfer_CanSend_OnlyFromDraft(string status, bool expected)
        => Assert.Equal(expected, TransferStates.CanSend(status));

    [Theory]
    [InlineData("DRAFT", false)]
    [InlineData("SENT", true)]
    [InlineData("RECEIVED", false)]
    public void Transfer_CanReceive_OnlyFromSent(string status, bool expected)
        => Assert.Equal(expected, TransferStates.CanReceive(status));

    [Fact]
    public void Transfer_CancelOnlyFromDraft_AfterSentTheLedgerAlreadyMoved()
    {
        Assert.True(TransferStates.CanCancel("DRAFT"));
        Assert.False(TransferStates.CanCancel("SENT"));   // الخروج وقع في الدفتر — التصحيح موثق
    }

    [Theory]
    [InlineData("OPEN", true, false, false)]
    [InlineData("COUNTED", false, true, false)]
    [InlineData("RECONCILED", false, false, true)]
    [InlineData("APPROVED", false, false, false)]
    [InlineData("CANCELLED", false, false, false)]
    public void Count_Transitions(string status, bool canRecord, bool canReconcile, bool canApprove)
    {
        Assert.Equal(canRecord, CountStates.CanRecord(status));
        Assert.Equal(canRecord, CountStates.CanClose(status));   // الإغلاق من OPEN مثل التسجيل
        Assert.Equal(canReconcile, CountStates.CanReconcile(status));
        Assert.Equal(canApprove, CountStates.CanApprove(status));
    }

    [Fact]
    public void Count_CancelFromOpenOrCounted_Only()
    {
        Assert.True(CountStates.CanCancel("OPEN"));
        Assert.True(CountStates.CanCancel("COUNTED"));
        Assert.False(CountStates.CanCancel("RECONCILED"));
        Assert.False(CountStates.CanCancel("APPROVED"));
    }

    // ── رياضة التسوية ──────────────────────────────────────────────────

    [Fact]
    public void Adjustment_StocktakePositiveVariance_AdjustmentIn()
    {
        var outcome = AdjustmentMath.Resolve(AdjustmentKinds.Stocktake, currentBaseQty: 10, approvedNewQty: 14);
        Assert.Equal(StockMovementTypes.AdjustmentIn, outcome.MovementType);
        Assert.Equal(4, outcome.BaseQty);
        Assert.Equal(1, outcome.Direction);
    }

    [Fact]
    public void Adjustment_StocktakeNegativeVariance_AdjustmentOut()
    {
        var outcome = AdjustmentMath.Resolve(AdjustmentKinds.Stocktake, 10, 6);
        Assert.Equal(StockMovementTypes.AdjustmentOut, outcome.MovementType);
        Assert.Equal(4, outcome.BaseQty);
        Assert.Equal(-1, outcome.Direction);
    }

    [Fact]
    public void Adjustment_ZeroVariance_NoMovementAtAll()
    {
        var outcome = AdjustmentMath.Resolve(AdjustmentKinds.Stocktake, 10, 10);
        Assert.Null(outcome.MovementType);
        Assert.Equal(0, outcome.BaseQty);
        Assert.Equal(0, outcome.Direction);   // لا حركة — لا ضجيج في الدفتر
    }

    [Fact]
    public void Adjustment_Damage_IsAlwaysOutByLostQuantity_NeverIn()
    {
        var outcome = AdjustmentMath.Resolve(AdjustmentKinds.Damage, 10, 3);
        Assert.Equal(StockMovementTypes.Damage, outcome.MovementType);
        Assert.Equal(7, outcome.BaseQty);      // ما هلك = الحالي − الباقي
        Assert.Equal(-1, outcome.Direction);

        var zero = AdjustmentMath.Resolve(AdjustmentKinds.Damage, 3, 3);
        Assert.Null(zero.MovementType);        // لم يهلك شيء — لا حركة
    }

    [Fact]
    public void Adjustment_Expiry_OutByExpiredQuantity()
    {
        var outcome = AdjustmentMath.Resolve(AdjustmentKinds.Expiry, 50, 20);
        Assert.Equal(StockMovementTypes.Expiry, outcome.MovementType);
        Assert.Equal(30, outcome.BaseQty);
        Assert.Equal(-1, outcome.Direction);
    }

    [Fact]
    public void Adjustment_NegativeApprovedQty_Rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => AdjustmentMath.Resolve(AdjustmentKinds.Quick, 10, -1));

    // ── الحجز والمتاح (§12.1) ───────────────────────────────────────────

    [Fact]
    public void Reservation_AvailableIsBalanceMinusReserved_FlooredAtZero()
    {
        Assert.Equal(6, ReservationRules.AvailableOf(10, 4));
        Assert.Equal(0, ReservationRules.AvailableOf(3, 5));    // لا متاح سالب
    }

    [Fact]
    public void Reservation_OverAvailable_Rejected()
        => Assert.Throws<InvalidOperationException>(() => ReservationRules.Validate(7, 6))
            .Message.Contains("RESERVATION_EXCEEDS_AVAILABLE");

    [Fact]
    public void Reservation_NonPositive_Rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ReservationRules.Validate(0, 6));

    // ── إعادة الطلب (PAGE-22) ───────────────────────────────────────────

    [Fact]
    public void Reorder_SuggestedBringsAvailableBackToLevel()
    {
        Assert.Equal(8, ReorderRules.SuggestedQty(reorderLevel: 10, rawAvailableQty: 2));   // يطلب ما يرفع التوافر للمستوى
        Assert.Equal(0, ReorderRules.SuggestedQty(10, 12));    // فوق المستوى — لا طلب
        Assert.Equal(165, ReorderRules.SuggestedQty(10, -155));  // عجز تحت الصفر (D-03) — الوصيفة تغطيه كاملاً
        Assert.True(ReorderRules.IsBelow(10, 2));
        Assert.True(ReorderRules.IsBelow(10, -155));            // العجز نقص حقيقي
        Assert.False(ReorderRules.IsBelow(10, 12));
        Assert.False(ReorderRules.IsBelow(0, 2));              // بلا مستوى محدد لا يوصف بنقص
    }

    // ── مطابقة الدفتر (بوابة القبول: الرصيد ناتج عن حركات) ─────────────

    [Fact]
    public void BalanceFromMovements_SumsDirectionedByBaseQty()
    {
        var movements = new (int Direction, decimal BaseQty)[]
        {
            (1, 60m), (1, 12m), (-1, 5m), (-1, 4m),
        };
        Assert.Equal(63, LedgerReconciliation.BalanceFromMovements(movements));
    }

    // ── WAC (D-02 أ) ────────────────────────────────────────────────────

    [Fact]
    public void Wac_BlendsOldAndNewCost_WeightedByQuantity()
    {
        var current = new WacState(QtyOnLayer: 100, AvgUnitCost: 2.0000m);
        // استلام 100 وحدة بتكلفة أساس 4.0000 → (100×2 + 100×4) / 200 = 3.0000
        var updated = WacMath.ApplyReceipt(current, receivedBaseQty: 100, baseUnitCost: 4.0000m);
        Assert.Equal(200, updated.QtyOnLayer);
        Assert.Equal(3.0000m, updated.AvgUnitCost);
    }

    [Fact]
    public void Wac_FirstReceipt_CostIsUnitCost()
    {
        var updated = WacMath.ApplyReceipt(new WacState(0, 0), 60, 2.5000m);
        Assert.Equal(60, updated.QtyOnLayer);
        Assert.Equal(2.5000m, updated.AvgUnitCost);
    }

    [Fact]
    public void Wac_NegativeInputs_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WacMath.ApplyReceipt(new WacState(10, 2), 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WacMath.ApplyReceipt(new WacState(10, 2), 5, -1));
    }

    [Fact]
    public void Valuation_SumsQtyTimesAvg()
        => Assert.Equal(220.0000m, WacMath.Valuation(new (decimal, decimal)[] { (100, 2.0000m), (40, 0.5000m) }));

    // ── توزيع السداد (§13) ──────────────────────────────────────────────

    [Fact]
    public void Allocation_OverPayment_Rejected()
        => Assert.Throws<InvalidOperationException>(
            () => PaymentAllocationRules.ValidateAgainstPayment(paymentAmount: 100, allocatedTotal: 150))
            .Message.Contains("PAYMENT_OVER_ALLOCATION");

    [Fact]
    public void Allocation_OverInvoiceRemaining_Rejected()
        => Assert.Throws<InvalidOperationException>(
            () => PaymentAllocationRules.ValidateAgainstInvoice(invoiceTotal: 100, previouslyAllocated: 80, newAllocation: 30))
            .Message.Contains("INVOICE_OVER_PAYMENT");

    [Theory]
    [InlineData(100, 0, "POSTED")]
    [InlineData(100, 40, "PARTIALLY_PAID")]
    [InlineData(100, 100, "PAID")]
    [InlineData(100, 99.99, "PARTIALLY_PAID")]
    public void InvoiceStatusAfter_Boundary(decimal total, decimal paid, string expected)
        => Assert.Equal(expected, PaymentAllocationRules.InvoiceStatusAfter(total, paid));

    // ── كشوف الحساب (§13) ───────────────────────────────────────────────

    [Fact]
    public void Statement_OpeningPlusPeriod_Closing()
    {
        var before = new decimal[] { 100, 50 };          // ما قبل الفترة
        var within = new decimal[] { 30, -80, 10 };      // حركة الفترة
        var opening = StatementMath.OpeningBalance(before);
        Assert.Equal(150, opening);
        var closing = StatementMath.ClosingBalance(opening, within);
        Assert.Equal(110, closing);                       // 150 + 30 − 80 + 10
    }

    // ── مرتجع المورد (§12.3 ق3: أثر مرة واحدة) ─────────────────────────

    [Theory]
    [InlineData("CASH", true, false)]         // رد نقدي: خزينة تدخل، الذمم لا تنخفض
    [InlineData("BANK_TRANSFER", true, false)]
    [InlineData("CREDIT_NOTE", false, true)]  // إشعار دائن: الذمم تنخفض، لا نقد
    public void RefundMethod_SingleTreasuryOrPayableEffect(string method, bool cashInflow, bool reducesPayable)
    {
        Assert.Equal(cashInflow, RefundMethods.IsCashRefund(method));
        Assert.NotEqual(cashInflow, reducesPayable);   // أثر واحد لا اثنان
    }

    // ── الأطراف (§13) ──────────────────────────────────────────────────

    [Fact]
    public void PartyStatus_ArchiveIsTerminal()
    {
        Assert.True(PartyStatuses.CanSet("ACTIVE", "SUSPENDED"));
        Assert.True(PartyStatuses.CanSet("SUSPENDED", "ACTIVE"));
        Assert.False(PartyStatuses.CanSet("NOT_A_STATUS", "ARCHIVED"));   // حالة مجهولة المصدر مرفوضة
        Assert.False(PartyStatuses.CanSet("ARCHIVED", "ACTIVE"));    // الأرشيف نهائي — تعطيل لا حذف
    }

    // ── أوامر الشراء (السلة) ───────────────────────────────────────────

    [Fact]
    public void Po_CancelBeforeAnyReceipt_Only()
    {
        Assert.True(PurchaseOrderStates.CanCancel("DRAFT"));
        Assert.True(PurchaseOrderStates.CanCancel("ORDERED"));
        Assert.False(PurchaseOrderStates.CanCancel("PARTIAL"));    // استُلم بعضه — لا إلغاء
        Assert.False(PurchaseOrderStates.CanCancel("RECEIVED"));
    }

    [Fact]
    public void Po_CloseOnlyWithShortfall()
    {
        Assert.True(PurchaseOrderStates.CanClose("ORDERED"));
        Assert.True(PurchaseOrderStates.CanClose("PARTIAL"));
        Assert.False(PurchaseOrderStates.CanClose("RECEIVED"));
        Assert.False(PurchaseOrderStates.CanClose("DRAFT"));       // السلة تُلغى أو تُصدر
    }
}
