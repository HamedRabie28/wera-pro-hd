namespace WeraPro.Contracts.V1;

/// <summary>مسارات التحليلات (G9 · HOME-01).</summary>
public static class AnalyticsApi
{
    public const string Prefix = ApiRoutes.Prefix + "/analytics";
}

/// <summary>
/// لوحة الرئيسية التنفيذية HOME-01 — البيانات تأتي من الخادم فقط (§7.5):
/// لا حساب داخل الواجهة ولا أرقام تجريبية. كل مؤشر موسوم بتعريفه الدقيق.
/// </summary>
public sealed record DashboardDto(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long? BranchId,
    string? BranchNameAr,

    // <summary>مؤشرات الإدارة — تُرجع فارغة إن لم يملك المستخدم صلاحية إدارية/مالية.</summary>
    KpiBlockDto? Kpis,

    // <summary>سلسلة صافي المبيعات اليومية داخل الفترة (+ مقارنة الفترة السابقة).</summary>
    IReadOnlyList<SalesPointDto> Series,

    // <summary>صافي مبيعات الفترة السابقة المكافئة — للمقارنة (null إن لا بيانات).</summary>
    decimal? PreviousPeriodNetSales,

    // <summary>حالة الورديات: مفتوحة الآن + مغلقة بفروق غير مسوّاة.</summary>
    ShiftsBlockDto Shifts,

    // <summary>ملخص التحصيل داخل الفترة: نقدي مقابل بطاقة (مفصولان دائماً §13).</summary>
    TreasuryBlockDto Treasury,

    // <summary>الأصناف النافدة/القريبة من النفاد (حسب حد إعادة الطلب) — حتى 10.</summary>
    IReadOnlyList<CriticalItemDto> CriticalStock,

    // <summary>عدد أوامر الشراء المعلقة (ORDERED/PARTIAL) والمتأخرة عن تاريخ التسليم.</summary>
    int PendingPurchaseOrders,
    int OverduePurchaseOrders,

    // <summary>أهم الأصناف بيعاً بصافي المبيعات داخل الفترة — حتى 5 (للإدارة فقط).</summary>
    IReadOnlyList<TopProductDto> TopProducts,

    // <summary>قائمة «يتطلب الانتباه» مرتبة حسب الشدة — من سجلات المصدر لا توقعات.</summary>
    IReadOnlyList<AlertDto> Alerts,

    // <summary>تذييل البيانات الموثوقة: وقت آخر تجميع + حالة الاتصال (§7.5 ق10).</summary>
    DateTimeOffset GeneratedAtUtc,
    string DataFreshness);

public sealed record KpiBlockDto(
    // <summary>صافي المبيعات دون الضريبة: Σ net_total − returned_total للفواتير المرحّلة (ق16 ق4).</summary>
    decimal NetSalesExclVat,
    // <summary>مجمل الربح (WAC): صافي المبيعات − Σ cost_snapshot×base_qty. null = تكلفة غير مكتملة «غير متاح».</summary>
    decimal? GrossProfit,
    // <summary>سبب عدم توفر الربح إن كان null.</summary>
    string? GrossProfitUnavailableReasonAr,
    // <summary>التحصيل النقدي/البطاقة المؤكد: مدفوعات CAPTURED/SETTLED بلا آجل (§15.4).</summary>
    decimal ConfirmedCollection,
    // <summary>عدد الفواتير المكتملة (POSTED/PARTIAL_RETURN/FULL_RETURN) داخل الفترة.</summary>
    long CompletedInvoices);

public sealed record SalesPointDto(DateOnly DateUtc, decimal NetSales, long Invoices);

public sealed record ShiftsBlockDto(
    int OpenNow,
    int ClosedWithUnsettledVariance,
    decimal TotalUnsettledVariance,
    IReadOnlyList<OpenShiftDto> LongOpenShifts);

/// <summary>وردية مفتوحة تجاوزت 12 ساعة — تنبيه تشغيلي صادق من سجل المصدر.</summary>
public sealed record OpenShiftDto(long ShiftId, long StationId, string OpenedBy, DateTimeOffset OpenedAtUtc);

public sealed record TreasuryBlockDto(decimal CashCollected, decimal CardCollected, decimal CreditSold);

public sealed record CriticalItemDto(
    long SkuId, string SkuCode, string? NameAr, decimal AvailableQty, decimal? ReorderLevel, string? UnitAr);

public sealed record TopProductDto(long SkuId, string SkuCode, string? NameAr, decimal NetSales, decimal QtySold);

public sealed record AlertDto(
    // <summary>شدة التنبيه: 1 حرج · 2 مرتفع · 3 متوسط · 4 منخفض.</summary>
    int Severity,
    string TitleAr,
    string? DetailAr,
    DateTimeOffset AtUtc,
    // <summary>الصفحة الهدف داخل التطبيق (sales/inventory/purchasing/maintenance).</summary>
    string TargetPage);
