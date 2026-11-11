namespace WeraPro.Application.Analytics;

using WeraPro.Application.Abstractions;
using WeraPro.Contracts.V1;

/// <summary>
/// حالة استخدام لوحة الرئيسية HOME-01 (§7.5): تجميع خادمي موثوق لكل كتل البيانات
/// مع فلترة حسب الصلاحيات — لا تُرسل مؤشرات إدارية لمستخدم لا يملك صلاحيتها
/// (الحماية على الخادم §0.1 ق10 وليست إخفاء أزرار).
/// </summary>
public sealed class DashboardUseCase
{
    private readonly IDashboardRepository _repo;
    private readonly ICurrentUser _user;
    private readonly IUserRepository _users;

    public DashboardUseCase(IDashboardRepository repo, ICurrentUser user, IUserRepository users)
    {
        _repo = repo;
        _user = user;
        _users = users;
    }

    /// <summary>صلاحيات الإدارة/المالية التي تفتح كتلة المؤشرات والأصناف الأعلى بيعاً.
    /// ops.settings.read مستبعدة عمداً: قراءة عامة تمنح لكل الأدوار (حتى الكاشير لPOS)
    /// — الكاشير يرى وحدات التشغيل فقط لا مؤشرات النشاط (§7.5: حماية خادمية).</summary>
    private static readonly string[] ManagementPermissions =
    [
        "security.audit.view", "security.users.read", "finance.treasury.view",
    ];

    public async Task<DashboardDto> ExecuteAsync(DateOnly from, DateOnly to, long? branchId, CancellationToken ct = default)
    {
        if (to < from)
        {
            throw new InvalidOperationException("نهاية الفترة قبل بدايتها");
        }
        // الفترة [from 00:00, to+1 00:00) — يوم الشامل للنهاية بتوقيت القاهرة المحلي
        var cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
            from.ToDateTime(TimeOnly.MinValue), cairo)).ToUniversalTime();
        var toLocal = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var toUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(toLocal, cairo)).ToUniversalTime();
        var periodDays = Math.Max(1, (toUtc - fromUtc).Days);
        var prevFrom = fromUtc.AddDays(-periodDays);

        var permissions = await _users.GetPermissionCodesAsync(_user.UserId, branchId ?? _user.BranchId, ct);
        var isManagement = permissions.Any(ManagementPermissions.Contains);

        // DbContext واحد للنطاق: الاستعلامات متسلسلة (كل منها تجميع مفهرس بالميلي ثانية) —
        // التزامن على نفس السياق يرمي InvalidOperationException.
        KpiBlockDto? kpis = null;
        IReadOnlyList<SalesPointDto> series = [];
        decimal? previousNet = null;
        IReadOnlyList<TopProductDto> top = [];
        if (isManagement)
        {
            kpis = await _repo.GetKpisAsync(fromUtc, toUtc, branchId, ct);
            series = await _repo.GetSeriesAsync(fromUtc, toUtc, branchId, ct);
            previousNet = await _repo.GetNetSalesAsync(prevFrom, fromUtc, branchId, ct);
            top = await _repo.GetTopProductsAsync(fromUtc, toUtc, branchId, 5, ct);
        }
        var shifts = await _repo.GetShiftsBlockAsync(ct);
        var treasury = await _repo.GetTreasuryBlockAsync(fromUtc, toUtc, branchId, ct);
        var critical = await _repo.GetCriticalStockAsync(branchId, 10, ct);
        var poCounts = await _repo.GetPurchaseOrderCountsAsync(ct);
        var alerts = await _repo.GetAlertsAsync(fromUtc, toUtc, branchId, ct);

        var branchName = branchId is null ? null : await _repo.GetBranchNameAsync(branchId.Value, ct);

        return new DashboardDto(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            BranchId: branchId,
            BranchNameAr: branchName,
            Kpis: kpis,
            Series: series,
            PreviousPeriodNetSales: previousNet,
            Shifts: shifts,
            Treasury: treasury,
            CriticalStock: critical,
            PendingPurchaseOrders: poCounts.Pending,
            OverduePurchaseOrders: poCounts.Overdue,
            TopProducts: top,
            Alerts: alerts,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            DataFreshness: "مباشر من قاعدة البيانات عند الطلب");
    }
}

/// <summary>مصدر بيانات لوحة الرئيسية — استعلامات قراءة مجمعة على سجلات المصدر.</summary>
public interface IDashboardRepository
{
    Task<KpiBlockDto?> GetKpisAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default);
    Task<IReadOnlyList<SalesPointDto>> GetSeriesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default);
    Task<decimal?> GetNetSalesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default);
    Task<IReadOnlyList<TopProductDto>> GetTopProductsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, int limit, CancellationToken ct = default);
    Task<ShiftsBlockDto> GetShiftsBlockAsync(CancellationToken ct = default);
    Task<TreasuryBlockDto> GetTreasuryBlockAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default);
    Task<IReadOnlyList<CriticalItemDto>> GetCriticalStockAsync(long? branchId, int limit, CancellationToken ct = default);
    Task<(int Pending, int Overdue)> GetPurchaseOrderCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AlertDto>> GetAlertsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, long? branchId, CancellationToken ct = default);
    Task<string?> GetBranchNameAsync(long branchId, CancellationToken ct = default);
}
