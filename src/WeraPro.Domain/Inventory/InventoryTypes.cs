namespace WeraPro.Domain.Inventory;

/// <summary>
/// قواعد المخزون الصرفة لبوابة G6 (§12.1/§12.2): آلات حالة التحويل والجرد،
/// رياضة التسوية، قواعد الحجز والمتاح، وأنواع المخازن — قواعد نقية قابلة
/// للاختبار بلا WinUI/HTTP/EF Core (§1).
/// </summary>
public static class TransferStates
{
    public const string Draft = "DRAFT";
    public const string Sent = "SENT";
    public const string Received = "RECEIVED";
    public const string Cancelled = "CANCELLED";

    /// <summary>الانتقالات المسموحة (§12.2 ق7: إرسال/استلام بحالتين مستقلتين).</summary>
    public static bool CanSend(string status) => status == Draft;
    public static bool CanReceive(string status) => status == Sent;
    /// <summary>الإلغاء من DRAFT فقط — بعد الإرسال وقع خروج فعلي في الدفتر والتصحيح بمرتجع/تسوية موثقة.</summary>
    public static bool CanCancel(string status) => status == Draft;

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Draft, Sent, Received, Cancelled };
}

public static class CountStates
{
    public const string Open = "OPEN";
    public const string Counted = "COUNTED";
    public const string Reconciled = "RECONCILED";
    public const string Approved = "APPROVED";
    public const string Cancelled = "CANCELLED";

    /// <summary>تسجيل العد يحدث في OPEN فقط (قبل الإغلاق للمقارنة).</summary>
    public static bool CanRecord(string status) => status == Open;
    /// <summary>الإغلاق: OPEN→COUNTED بعد اكتمال العد — لا تسجيل بعده (اللقطة مجمّدة للمقارنة).</summary>
    public static bool CanClose(string status) => status == Open;
    /// <summary>المطابقة: COUNTED→RECONCILED (الفروق محسوبة وعرضها للاعتماد).</summary>
    public static bool CanReconcile(string status) => status == Counted;
    /// <summary>الاعتماد: RECONCILED→APPROVED — ينشئ مستند التسوية ويحرك الدفتر؛ المعتمد ≠ الجارّ (قيد V003).</summary>
    public static bool CanApprove(string status) => status == Reconciled;
    public static bool CanCancel(string status) => status is Open or Counted;

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Open, Counted, Reconciled, Approved, Cancelled };
}

/// <summary>نوع مستند التسوية (V017 adjustment_kind) — يميّز الجرد المعتمد من الهالك والتلف والتسوية السريعة.</summary>
public static class AdjustmentKinds
{
    public const string Stocktake = "STOCKTAKE";
    public const string Damage = "DAMAGE";
    public const string Expiry = "EXPIRY";
    public const string Quick = "QUICK";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Stocktake, Damage, Expiry, Quick };

    /// <summary>نوع حركة الدفتر الموافق لكل مستند تسوية (الهالك والتلف يخرجان من الدفتر بلا زيادة).</summary>
    public static string? LedgerMovementOf(string adjustmentKind) => adjustmentKind switch
    {
        Stocktake => null, // يتحدد بإشارة الفرق (دخولاً أو خروجاً) — AdjustmentMath
        Damage => WeraPro.Domain.Catalog.StockMovementTypes.Damage,
        Expiry => WeraPro.Domain.Catalog.StockMovementTypes.Expiry,
        Quick => null,
        _ => throw new ArgumentOutOfRangeException(nameof(adjustmentKind), adjustmentKind, "نوع تسوية غير معروف"),
    };
}

/// <summary>نتيجة حساب التسوية: الحركة المطلوبة للوصول إلى الرصيد الصحيح المعتمد.</summary>
public sealed record AdjustmentOutcome(
    string? MovementType,   // ADJUSTMENT_IN / ADJUSTMENT_OUT / DAMAGE / EXPIRY — null = لا حركة (الفرق صفر)
    decimal BaseQty,        // موجبة دائماً (الدفتر يخزن موجبة والاتجاه في direction)
    int Direction);         // 1 دخول / -1 خروج / 0 لا حركة

/// <summary>
/// رياضة التسوية (§12.2 ق9): من الرصيد الدفتري الحالي إلى الرصيد الصحيح المعتمد —
/// الفرق يتحول لحركة واحدة في الدفتر، وإشارة صفر لا تنشئ حركة إطلاقاً (لا ضجيج).
/// مستند الهالك/التلف: خروج بكمية ما هلك من الرصيد الحالي — لا «تسوية دخول» بهالك.
/// </summary>
public static class AdjustmentMath
{
    public static AdjustmentOutcome Resolve(string adjustmentKind, decimal currentBaseQty, decimal approvedNewQty)
    {
        if (approvedNewQty < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedNewQty), approvedNewQty, "الرصيد المعتمد لا يكون سالباً (V017)");
        }

        var forcedType = AdjustmentKinds.LedgerMovementOf(adjustmentKind);
        if (forcedType is not null)
        {
            // هالك/تلف: approvedNewQty هو الرصيد الباقي بعد الهالك — الخروج = الحالي − الباقي
            var outQty = decimal.Round(currentBaseQty - approvedNewQty, 6, MidpointRounding.AwayFromZero);
            if (outQty <= 0)
            {
                return new AdjustmentOutcome(null, 0, 0);
            }
            return new AdjustmentOutcome(forcedType, outQty, -1);
        }

        // جرد/سريع: الفرق موقّع
        var delta = decimal.Round(approvedNewQty - currentBaseQty, 6, MidpointRounding.AwayFromZero);
        if (delta == 0)
        {
            return new AdjustmentOutcome(null, 0, 0);
        }
        return delta > 0
            ? new AdjustmentOutcome(WeraPro.Domain.Catalog.StockMovementTypes.AdjustmentIn, delta, 1)
            : new AdjustmentOutcome(WeraPro.Domain.Catalog.StockMovementTypes.AdjustmentOut, -delta, -1);
    }
}

/// <summary>قواعد الحجز والمتاح (§12.1): المتاح = الرصيد − المحجوز؛ لا حجز فوق المتاح.</summary>
public static class ReservationRules
{
    /// <summary>التحقق قبل الحجز — لا حجز فوق المتاح ولا كمية غير موجبة.</summary>
    public static void Validate(decimal baseQty, decimal availableQty)
    {
        if (baseQty <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseQty), baseQty, "كمية الحجز يجب أن تكون موجبة");
        }
        if (baseQty > availableQty)
        {
            throw new InvalidOperationException(
                $"RESERVATION_EXCEEDS_AVAILABLE: الحجز {baseQty:0.######} يتجاوز المتاح {availableQty:0.######} (الرصيد − المحجوز) — لا حجز فوق المتاح §12.1");
        }
    }

    public static decimal AvailableOf(decimal baseQty, decimal reservedQty)
    {
        var available = baseQty - reservedQty;
        return available > 0 ? decimal.Round(available, 6, MidpointRounding.AwayFromZero) : 0;
    }
}

/// <summary>أنواع المخازن (V003 wh_type) — الرئيسي/الفرعي بالمخزن/الفرعي الخارجي (§12.2 ق1).</summary>
public static class WarehouseTypes
{
    public const string Main = "MAIN";
    public const string Branch = "BRANCH";
    public const string Sub = "SUB";
    public const string External = "EXTERNAL";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Main, Branch, Sub, External };
}

/// <summary>
/// قاعدة توصية إعادة الطلب (PAGE-22): الوصيفة كمية تُعيد التوافر إلى مستوى
/// إعادة الطلب المحدد للصنف — قاعدة حتمية موثقة (لا تنبؤ؛ التنبؤ في G9):
/// suggested = max(reorder_level − available, 0) — والتوافر هنا الخام (رصيد − محجوز)
/// فقد يشمل العجز تحت الصفر (D-03) فتوصي الوصيفة بتغطيته كاملاً.
/// </summary>
public static class ReorderRules
{
    public static decimal SuggestedQty(decimal reorderLevel, decimal rawAvailableQty)
        => reorderLevel > rawAvailableQty ? decimal.Round(reorderLevel - rawAvailableQty, 6, MidpointRounding.AwayFromZero) : 0;

    /// <summary>النقص = التوافر الخام دون مستوى إعادة الطلب المحدد للصنف.</summary>
    public static bool IsBelow(decimal reorderLevel, decimal rawAvailableQty)
        => reorderLevel > 0 && rawAvailableQty < reorderLevel;
}

/// <summary>
/// مطابقة التقارير مع الدفتر (بوابة القبول): الرصيد المعروض في كل تقرير =
/// مجموع حركات الدفتر للوحدة الأساسية حتى لحظة التقرير — دالة واحدة لا
/// حسابات مختلفة بين الصفحات (§16.5).
/// </summary>
public static class LedgerReconciliation
{
    public static decimal BalanceFromMovements(IEnumerable<(int Direction, decimal BaseQty)> movements)
        => decimal.Round(
            movements.Sum(m => m.Direction * m.BaseQty),
            6, MidpointRounding.AwayFromZero);
}
