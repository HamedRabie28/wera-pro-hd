namespace WeraPro.Domain.Purchasing;

/// <summary>
/// قواعد المشتريات الصرفة لبوابة G6 (§12.3 و§13): آلات حالة أوامر الشراء
/// والاستلامات والفواتير والسدادات والمرتجعات، رياضة WAC (D-02 أ)، قواعد
/// توزيع السداد، وأثر المرتجع على الخزينة والذمم — قواعد نقية قابلة للاختبار.
/// </summary>
public static class PurchaseOrderStates
{
    public const string Draft = "DRAFT";       // سلة/مسودة قابلة للاستعادة (لا حركة مخزون ولا مال)
    public const string Ordered = "ORDERED";   // أمر صادر للمورد
    public const string Partial = "PARTIAL";   // استُلم بعضه
    public const string Received = "RECEIVED"; // استُلم كاملاً
    public const string Closed = "CLOSED";     // أُقفل بلا استلام كامل (قصور مقصود موثق)
    public const string Cancelled = "CANCELLED";

    public static bool CanSubmit(string status) => status == Draft;
    public static bool CanCancel(string status) => status is Draft or Ordered; // لا إلغاء بعد أي استلام
    public static bool CanClose(string status) => status is Ordered or Partial; // لا إقفال ما لم يُستلم كله
    /// <summary>هل الحالة نهائية (لا عمليات بعدها)؟</summary>
    public static bool IsTerminal(string status) => status is Received or Closed or Cancelled;

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Draft, Ordered, Partial, Received, Closed, Cancelled };
}

public static class ReceiptStates
{
    public const string Draft = "DRAFT";
    public const string Posted = "POSTED";     // الاستلام المرحّل هو الذي زاد المخزون (§12.3 ق1)
    public const string Cancelled = "CANCELLED";

    public static bool CanPost(string status) => status == Draft;
    /// <summary>الإلغاء من DRAFT فقط — POSTED وقع في الدفتر (Append-only) وتصحيحه بمرتجع موثق.</summary>
    public static bool CanCancel(string status) => status == Draft;

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Draft, Posted, Cancelled };
}

public static class InvoiceStates
{
    public const string Draft = "DRAFT";
    public const string Posted = "POSTED";           // التزام مالي — لا حركة مخزون (§12.3 ق1)
    public const string PartiallyPaid = "PARTIALLY_PAID";
    public const string Paid = "PAID";
    public const string Cancelled = "CANCELLED";

    public static bool CanPost(string status) => status == Draft;
    public static bool CanCancel(string status) => status == Draft; // لا إلغاء التزام مرحّل

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Draft, Posted, PartiallyPaid, Paid, Cancelled };
}

public static class PaymentStates
{
    public const string Draft = "DRAFT";
    public const string Posted = "POSTED";     // أثر خزينة واحد — لا يُسجل مصروفاً (§12.3 ق3)
    public const string Cancelled = "CANCELLED";

    public static bool CanPost(string status) => status == Draft;
    public static bool CanCancel(string status) => status == Draft;

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Draft, Posted, Cancelled };
}

public static class ReturnStates
{
    public const string Draft = "DRAFT";
    public const string Posted = "POSTED";
    public const string Cancelled = "CANCELLED";

    public static bool CanPost(string status) => status == Draft;
    public static bool CanCancel(string status) => status == Draft;

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Draft, Posted, Cancelled };
}

public static class PartyStatuses
{
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Archived = "ARCHIVED";   // تعطيل لا حذف — الطرف له مستندات تاريخية (§13)

    /// <summary>التعطيل/الأرشفة من كل حالة معروفة؛ التنشيط من SUSPENDED فقط (الأرشيف نهائي).</summary>
    public static bool CanSet(string from, string to) =>
        All.Contains(from) && All.Contains(to) && from != to
        && (from, to) is not ((Archived, Active) or (Archived, Suspended));

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Active, Suspended, Archived };
}

/// <summary>طرق رد قيمة مرتجع المورد (V004 refund_method) وأثرها المالي.</summary>
public static class RefundMethods
{
    public const string Cash = "CASH";
    public const string CreditNote = "CREDIT_NOTE";
    public const string BankTransfer = "BANK_TRANSFER";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Cash, CreditNote, BankTransfer };

    /// <summary>هل الطريقة ترد نقداً (تأثير خزينة)؟ الإشعار الدائن يخفض الذمم بلا نقد.</summary>
    public static bool IsCashRefund(string method) => method is Cash or BankTransfer;
}

/// <summary>بادئات أرقام المستندات (تسلسل purch.g6_doc_seq من V017) — مصدر حقيقة واحد.</summary>
public static class DocNumberPrefixes
{
    public const string PurchaseOrder = "PO-";
    public const string Receipt = "RC-";
    public const string PaymentVoucher = "SP-";
    public const string SupplierReturn = "SR-";
}

/// <summary>نتيجة تحديث WAC لاستلام واحد (D-02 أ: متوسط مرجح متحرك).</summary>
public sealed record WacState(
    decimal QtyOnLayer,
    decimal AvgUnitCost);   // تكلفة الوحدة الأساسية — 4 منازل

/// <summary>
/// رياضة التكلفة WAC (§15.3 · D-02 أ محسوم بترحيل V012): تُحدَّث مع كل استلام
/// على مستوى الوحدة الأساسية للصنف في مخزنه — (الكمية القديمة × المتوسط القديم
/// + الكمية المستلمة × تكلفة وحدتها الأساسية) ÷ الكمية الجديدة.
/// تكلفة الوحدة الأساسية للسطر = unit_cost ÷ conversion_factor (لقطة السطر).
/// </summary>
public static class WacMath
{
    public static WacState ApplyReceipt(WacState current, decimal receivedBaseQty, decimal baseUnitCost)
    {
        if (receivedBaseQty <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(receivedBaseQty), receivedBaseQty, "كمية الاستلام يجب أن تكون موجبة");
        }
        if (baseUnitCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseUnitCost), baseUnitCost, "تكلفة الوحدة لا تكون سالبة");
        }
        var newQty = current.QtyOnLayer + receivedBaseQty;
        if (newQty <= 0)
        {
            throw new InvalidOperationException("WAC_QTY_INVALID: الكمية على الطبقة لا تكون سالبة");
        }
        var blended = current.QtyOnLayer * current.AvgUnitCost + receivedBaseQty * baseUnitCost;
        var newAvg = decimal.Round(blended / newQty, 4, MidpointRounding.AwayFromZero);
        return new WacState(decimal.Round(newQty, 6, MidpointRounding.AwayFromZero), newAvg);
    }

    /// <summary>تقييم المخزون بطبقات التكلفة الحالية (§16.2 تقييم وفق طريقة التكلفة المعتمدة).</summary>
    public static decimal Valuation(IEnumerable<(decimal Qty, decimal AvgUnitCost)> layers)
        => decimal.Round(layers.Sum(l => l.Qty * l.AvgUnitCost), 4, MidpointRounding.AwayFromZero);
}

/// <summary>قواعد توزيع السداد على الفواتير (§13) — مرآة حارسي V004/V017 بتقديم أوضح.</summary>
public static class PaymentAllocationRules
{
    /// <summary>Σ التخصيصات ≤ قيمة السداد (حارس V004).</summary>
    public static void ValidateAgainstPayment(decimal paymentAmount, decimal allocatedTotal)
    {
        if (allocatedTotal > paymentAmount)
        {
            throw new InvalidOperationException(
                $"PAYMENT_OVER_ALLOCATION: مجموع التخصيصات {allocatedTotal:0.####} يتجاوز قيمة السداد {paymentAmount:0.####}");
        }
    }

    /// <summary>Σ ما دُفع على الفاتورة ≤ قيمة الفاتورة (حارس V017) — لا تخصيص بأكثر من المتبقي.</summary>
    public static void ValidateAgainstInvoice(decimal invoiceTotal, decimal previouslyAllocated, decimal newAllocation)
    {
        if (previouslyAllocated + newAllocation > invoiceTotal)
        {
            throw new InvalidOperationException(
                $"INVOICE_OVER_PAYMENT: المخصص على الفاتورة {previouslyAllocated + newAllocation:0.####} يتجاوز قيمتها {invoiceTotal:0.####} (§13)");
        }
    }

    /// <summary>حالة الفاتورة بعد تخصيص: مدفوعة كاملة أو جزئياً.</summary>
    public static string InvoiceStatusAfter(decimal invoiceTotal, decimal allocatedTotal)
        => allocatedTotal <= 0
            ? InvoiceStates.Posted
            : allocatedTotal + 0.005m >= invoiceTotal ? InvoiceStates.Paid : InvoiceStates.PartiallyPaid;
}

/// <summary>أثر مرتجع المورد على ذمم المورد والخزينة (يُحسب مرة واحدة عند الترحيل).</summary>
public sealed record SupplierReturnEffect(
    bool ReducesPayable,        // الإشعار الدائن يخفض ما على المورد
    bool TreasuryInflow,        // رد نقدي/تحويل يرجع مالاً للخزينة
    decimal Amount);

/// <summary>
/// كشف حساب الطرف (§13): الرصيد الافتتاحي من كل المستندات قبل الفترة + حركات
/// الفترة + الرصيد الختامي — قابل للتصفية حسب الفترة ونوع المستند.
/// البنود موجبة = زيادة ما على المورد (فاتورة) وسالبة = تخفيض (سداد/إشعار دائن).
/// </summary>
public sealed record StatementEntry(
    long DocId,
    string DocType,          // purch.invoice / purch.payment / purch.return
    string DocNumber,
    DateTimeOffset Date,
    decimal Amount,          // موقعة: + على المورد، − تخفيض
    string? Detail);

public static class StatementMath
{
    /// <summary>الرصيد الافتتاحي = مجموع بنود ما قبل الفترة (موقعة) — لا رقم حر.</summary>
    public static decimal OpeningBalance(IEnumerable<decimal> amountsBeforePeriod)
        => decimal.Round(amountsBeforePeriod.Sum(), 4, MidpointRounding.AwayFromZero);

    /// <summary>الرصيد الختامي = الافتتاحي + حركات الفترة.</summary>
    public static decimal ClosingBalance(decimal opening, IEnumerable<decimal> periodAmounts)
        => decimal.Round(opening + periodAmounts.Sum(), 4, MidpointRounding.AwayFromZero);
}
