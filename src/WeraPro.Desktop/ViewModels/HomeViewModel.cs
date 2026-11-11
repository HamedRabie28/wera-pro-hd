// ViewModels/HomeViewModel.cs — WERA PRO HD Desktop (G9 · HOME-01 الرئيسية التنفيذية)
// لوحة القيادة التنفيذية: كل الأرقام من GET /analytics/dashboard على الخادم —
// لا حساب مؤشرات داخل الواجهة (§7.5: مصدر التعريف واحد موثق)، ولا بيانات تجريبية.
// الفترات: اليوم/أمس/7 أيام/30 يوماً + تحديث يدوي وتلقائي كل 60 ثانية.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>عرض تنبيه «يتطلب الانتباه» — شدة ملونة ووقت نسبي عربي.</summary>
public sealed class AlertRow
{
    public required AlertDto Source { get; init; }
    public string TitleAr => Source.TitleAr;
    public string? DetailAr => Source.DetailAr;
    public string WhenAr => Source.AtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string TargetPage => Source.TargetPage;
    /// <summary>1 حرج أحمر · 2 مرتفع برتقالي · 3 متوسط ذهبي · 4 منخفض رمادي.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush SeverityBrush => Source.Severity switch
    {
        1 => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x48, 0x50)),
        2 => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC2, 0x7A, 0x2E)),
        3 => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xB9, 0x95, 0x5C)),
        _ => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D)),
    };
    public string SeverityLabel => Source.Severity switch
    {
        1 => "حرج", 2 => "مرتفع", 3 => "متوسط", _ => "منخفض",
    };
}

/// <summary>صنف حرج في المخزون — متاح ≤ حد إعادة الطلب.</summary>
public sealed class CriticalRow
{
    public required CriticalItemDto Source { get; init; }
    public string SkuCode => Source.SkuCode;
    public string NameAr => Source.NameAr ?? "—";
    public string AvailableAr => $"{Source.AvailableQty:0.##} {Source.UnitAr ?? ""}".Trim();
    public string ReorderAr => Source.ReorderLevel?.ToString("0.##") ?? "—";
    public bool IsOut => Source.AvailableQty <= 0;
}

/// <summary>صنف من الأعلى بيعاً بصافي المبيعات داخل الفترة.</summary>
public sealed class TopProductRow
{
    public required TopProductDto Source { get; init; }
    public string SkuCode => Source.SkuCode;
    public string NameAr => Source.NameAr ?? "—";
    public string NetAr => Source.NetSales.ToString("N2") + " ج.م";
    public string QtyAr => Source.QtySold.ToString("0.##");
}

/// <summary>نقطة سلسلة المبيعات لرسم الخط (إحداثيات طبيعية 0..1 يحسبها العرض).</summary>
public sealed class SeriesPoint
{
    public required string Label { get; init; }
    public required decimal Value { get; init; }
    public string ValueAr => Value.ToString("N0");
}

/// <summary>
/// الرئيسية التنفيذية HOME-01 — تجميع خادمي بفلاتر فترة، مقارنة بالفترة السابقة،
/// وحدات تشغيل (ورديات/خزينة/مخزون حرج/مشتريات)، تنبيهات، وأهم الأصناف.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _refreshTimer;

    public HomeViewModel(WeraApiClient api, SessionService session, ILoggerFactory loggerFactory)
    {
        _api = api;
        _session = session;
        _logger = loggerFactory.CreateLogger<HomeViewModel>();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _refreshTimer.Tick += async (_, _) => await LoadAsync();
        _refreshTimer.Start();
    }

    // ── الفترة المحددة ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodTodayChecked))]
    [NotifyPropertyChangedFor(nameof(PeriodYesterdayChecked))]
    [NotifyPropertyChangedFor(nameof(PeriodWeekChecked))]
    [NotifyPropertyChangedFor(nameof(PeriodMonthChecked))]
    private int _periodIndex; // 0 اليوم · 1 أمس · 2 سبعة أيام · 3 ثلاثون يوماً

    public bool PeriodTodayChecked => PeriodIndex == 0;
    public bool PeriodYesterdayChecked => PeriodIndex == 1;
    public bool PeriodWeekChecked => PeriodIndex == 2;
    public bool PeriodMonthChecked => PeriodIndex == 3;

    // ── الحالة ──
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _errorAr;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _lastUpdatedAr = "—";

    // ── الرأس السياقي ──
    public string WelcomeAr => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "صباح الخير",
        >= 12 and < 17 => "طاب يومك",
        >= 17 and < 21 => "مساء الخير",
        _ => "مساء الخير",
    };
    public string UserAr => _session.User?.DisplayNameAr ?? "—";
    public string ScopeAr => _session.BranchNameAr is null
        ? "نطاق مركزي"
        : $"{_session.BranchNameAr}" + (_session.StationNameAr is null ? "" : $" · {_session.StationNameAr}");
    public string DateAr => DateTime.Now.ToString("dddd، d MMMM yyyy", new System.Globalization.CultureInfo("ar-EG"));

    // ── المؤشرات (من الخادم — kpis = null يعني صلاحية غير إدارية: تُخفى البطاقات) ──
    [ObservableProperty] private string? _netSalesAr;
    [ObservableProperty] private string? _grossProfitAr;
    [ObservableProperty] private string? _grossProfitHintAr;
    [ObservableProperty] private string? _collectionAr;
    [ObservableProperty] private string? _invoicesAr;
    [ObservableProperty] private string? _deltaAr;               // مقارنة الفترة السابقة
    [ObservableProperty] private Microsoft.UI.Xaml.Media.SolidColorBrush? _deltaBrush;
    public bool KpiVisibility => _kpisLoaded;
    public Microsoft.UI.Xaml.Visibility ErrorVisibility => ErrorAr is null
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility NoAlertsVisibility => HasAlerts
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    [ObservableProperty] private bool _kpisLoaded;

    // ── وحدات التشغيل ──
    [ObservableProperty] private string _shiftsAr = "—";
    [ObservableProperty] private string _treasuryAr = "—";
    [ObservableProperty] private string _cashAr = "—";
    [ObservableProperty] private string _cardAr = "—";
    [ObservableProperty] private string _creditAr = "—";
    [ObservableProperty] private string _posAr = "—";
    [ObservableProperty] private string _posHintAr = "";

    // ── القوائم ──
    public ObservableCollection<AlertRow> Alerts { get; } = [];
    public ObservableCollection<CriticalRow> CriticalStock { get; } = [];
    public ObservableCollection<TopProductRow> TopProducts { get; } = [];
    public ObservableCollection<SeriesPoint> Series { get; } = [];
    [ObservableProperty] private bool _hasAlerts;
    [ObservableProperty] private bool _hasCritical;
    [ObservableProperty] private bool _hasTopProducts;
    [ObservableProperty] private bool _hasSeries;

    /// <summary>يفتحها ShellWindow عند تحميل الصفحة أو تبديل الفترة.</summary>
    public event Action? RequestOpenPos;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        ErrorAr = null;
        OnPropertyChanged(nameof(ErrorVisibility));
        try
        {
            var (from, to) = PeriodIndex switch
            {
                1 => (DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), DateOnly.FromDateTime(DateTime.Today.AddDays(-1))),
                2 => (DateOnly.FromDateTime(DateTime.Today.AddDays(-6)), DateOnly.FromDateTime(DateTime.Today)),
                3 => (DateOnly.FromDateTime(DateTime.Today.AddDays(-29)), DateOnly.FromDateTime(DateTime.Today)),
                _ => (DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today)),
            };

            var result = await _api.GetDashboardAsync(from, to, _session.BranchId);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر تحميل لوحة الرئيسية";
                OnPropertyChanged(nameof(ErrorVisibility));
                HasData = false;
                return;
            }
            var d = result.Value;
            HasData = true;
            LastUpdatedAr = d.GeneratedAtUtc.ToLocalTime().ToString("HH:mm:ss");

            // المؤشرات — null = مستخدم غير إداري (البطاقات تُخفى)
            if (d.Kpis is { } k)
            {
                KpisLoaded = true;
                NetSalesAr = k.NetSalesExclVat.ToString("N2") + " ج.م";
                GrossProfitAr = k.GrossProfit?.ToString("N2") + (k.GrossProfit is null ? "" : " ج.م");
                GrossProfitHintAr = k.GrossProfit is null ? k.GrossProfitUnavailableReasonAr ?? "غير متاح — بيانات تكلفة غير مكتملة" : "مجمل الربح (WAC) بعد تكلفة البضاعة المباعة";
                CollectionAr = k.ConfirmedCollection.ToString("N2") + " ج.م";
                InvoicesAr = k.CompletedInvoices.ToString("N0");

                if (d.PreviousPeriodNetSales is { } prev && prev > 0 && k.NetSalesExclVat > 0)
                {
                    var delta = (k.NetSalesExclVat - prev) / prev * 100m;
                    DeltaAr = (delta >= 0 ? "▲ +" : "▼ ") + delta.ToString("0.0") + "٪ عن الفترة السابقة";
                    DeltaBrush = new(Microsoft.UI.ColorHelper.FromArgb(
                        0xFF, delta >= 0 ? (byte)0x16 : (byte)0xC4, delta >= 0 ? (byte)0x84 : (byte)0x48, delta >= 0 ? (byte)0x5D : (byte)0x50));
                }
                else if (k.NetSalesExclVat == 0)
                {
                    DeltaAr = "لا مبيعات في الفترة المحددة";
                    DeltaBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D));
                }
                else
                {
                    DeltaAr = "لا مقارنة متاحة (فترة سابقة بلا مبيعات)";
                    DeltaBrush = new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D));
                }
            }
            else
            {
                KpisLoaded = false;
            }

            // السلسلة
            Series.Clear();
            foreach (var p in d.Series)
            {
                Series.Add(new SeriesPoint { Label = p.DateUtc.ToString("MM/dd"), Value = p.NetSales });
            }
            HasSeries = Series.Count > 1;

            // الورديات
            var s = d.Shifts;
            ShiftsAr = s.OpenNow == 0 && s.ClosedWithUnsettledVariance == 0
                ? "لا ورديات مفتوحة"
                : $"{s.OpenNow} مفتوحة" + (s.ClosedWithUnsettledVariance > 0 ? $" · {s.ClosedWithUnsettledVariance} تحتاج تسوية" : "");

            // الخزينة (نقدي/بطاقة مفصولان دائماً §13)
            CashAr = d.Treasury.CashCollected.ToString("N2");
            CardAr = d.Treasury.CardCollected.ToString("N2");
            CreditAr = d.Treasury.CreditSold.ToString("N2");
            TreasuryAr = (d.Treasury.CashCollected + d.Treasury.CardCollected).ToString("N2") + " ج.م";

            // المشتريات
            PosAr = d.PendingPurchaseOrders.ToString("N0");
            PosHintAr = d.OverduePurchaseOrders > 0 ? $"{d.OverduePurchaseOrders} متأخر عن موعد التسليم" : "لا أوامر متأخرة";

            // المخزون الحرج
            CriticalStock.Clear();
            foreach (var c in d.CriticalStock)
            {
                CriticalStock.Add(new CriticalRow { Source = c });
            }
            HasCritical = CriticalStock.Count > 0;

            // الأعلى بيعاً
            TopProducts.Clear();
            foreach (var t in d.TopProducts)
            {
                TopProducts.Add(new TopProductRow { Source = t });
            }
            HasTopProducts = TopProducts.Count > 0;

            // التنبيهات
            Alerts.Clear();
            foreach (var a in d.Alerts)
            {
                Alerts.Add(new AlertRow { Source = a });
            }
            HasAlerts = Alerts.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "فشل تحميل لوحة الرئيسية");
            ErrorAr = "تعذر تحميل لوحة الرئيسية — تحقق من اتصال الخادم";
            OnPropertyChanged(nameof(ErrorVisibility));
            HasData = false;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void SelectToday() { PeriodIndex = 0; _ = LoadAsync(); }

    [RelayCommand]
    private void SelectYesterday() { PeriodIndex = 1; _ = LoadAsync(); }

    [RelayCommand]
    private void SelectWeek() { PeriodIndex = 2; _ = LoadAsync(); }

    [RelayCommand]
    private void SelectMonth() { PeriodIndex = 3; _ = LoadAsync(); }

    [RelayCommand]
    private void OpenPos() => RequestOpenPos?.Invoke();

    public void StopAutoRefresh() => _refreshTimer.Stop();
}
