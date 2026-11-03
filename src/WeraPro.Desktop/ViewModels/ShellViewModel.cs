// ViewModels/ShellViewModel.cs + PosViewModel.cs — WERA PRO HD Desktop (G3)
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>
/// Shell الإداري: هوية حقيقية من الجلسة + حالة اتصال حقيقية + خروج موثق.
/// مجموعات التنقل العشر كما في G2 — البوابات غير المبنية بعد تُعرض معطلة
/// بسبب معلن (لا وظيفة وهمية §0.1 ق10).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly SessionService _session;
    private readonly ConnectivityService _connectivity;
    private readonly WeraApiClient _api;

    public ShellViewModel(SessionService session, ConnectivityService connectivity, WeraApiClient api)
    {
        _session = session;
        _connectivity = connectivity;
        _api = api;
        _session.SessionChanged += RefreshIdentity;
        _connectivity.StateChanged += () =>
        {
            OnPropertyChanged(nameof(ConnectionDisplayAr));
            OnPropertyChanged(nameof(IsOnline));
        };
        RefreshIdentity();
    }

    public string DisplayNameAr => _session.User?.DisplayNameAr ?? "—";
    public string RolesDisplayAr => _session.User is { } u && u.Roles.Count > 0
        ? string.Join(" · ", u.Roles)
        : "بلا دور";
    public string ScopeDisplayAr =>
        (_session.BranchNameAr is { } b ? b : "كل الفروع")
        + (_session.StationNameAr is { } s ? " · " + s : "");
    public string ConnectionDisplayAr => _connectivity.DisplayAr;
    public bool IsOnline => _connectivity.State == ConnectionState.Online;

    public event Action? LoggedOut;

    /// <summary>نبضة دورية كل 30 ثانية — تُشغل من مؤقت النافذة (لا Timer داخل ViewModel).</summary>
    public Task HeartbeatAsync(CancellationToken ct) => _connectivity.ProbeAsync(ct);

    [RelayCommand]
    private async Task LogoutAsync(CancellationToken ct)
    {
        await _api.LogoutAsync(ct);
        _session.Clear();
        LoggedOut?.Invoke();
    }

    private void RefreshIdentity()
    {
        OnPropertyChanged(nameof(DisplayNameAr));
        OnPropertyChanged(nameof(RolesDisplayAr));
        OnPropertyChanged(nameof(ScopeDisplayAr));
    }
}

/// <summary>
/// POS المستقلة (قشرة G3): شريط جلسة حقيقي + إعدادات الوزن من الخادم (D-07)
/// والسلة بيانات DEV موسومة بصدق حتى G7 — لا مطالبة بيع حقيقية في هذه البوابة.
/// </summary>
public sealed partial class PosViewModel : ObservableObject
{
    private readonly SessionService _session;
    private readonly WeraApiClient _api;

    public PosViewModel(SessionService session, WeraApiClient api)
    {
        _session = session;
        _api = api;
    }

    public string CashierDisplayAr => _session.User?.DisplayNameAr ?? "—";
    public string StationDisplayAr => _session.StationNameAr ?? "محطة غير محددة";
    public string ScopeDisplayAr => _session.BranchNameAr ?? "كل الفروع";

    [ObservableProperty]
    private string _weighingInfoAr = "إعدادات الوزن تُجلب من الخادم…";

    /// <summary>سلة DEV موسومة — تُستبدل بسلة حقيقية في G7.</summary>
    public ObservableCollection<string> DevCartNotice { get; } =
        ["سلة عرض DEV — لا بيع فعلي في G3", "بيانات حقيقية من G7 (POS كاملة)"];

    public async Task LoadFoundationAsync(CancellationToken ct)
    {
        var settings = await _api.GetSettingsAsync("WEIGHT", ct);
        if (!settings.Ok || settings.Value is null)
        {
            WeighingInfoAr = "تعذر جلب إعدادات الوزن — " + (settings.ErrorAr ?? "سبب غير معروف");
            return;
        }

        var byKey = settings.Value.ToDictionary(s => s.SettingKey, s => s.ValueText);
        string Get(string key) => byKey.TryGetValue(key, out var v) ? v : "؟";
        WeighingInfoAr =
            $"الوزن: دقة {Get("weighing.precision_g")}g · خطوة {Get("weighing.step_g")}g · " +
            $"تقريب المبلغ {Get("weighing.amount_rounding")} · {Get("weighing.weight_rounding")}";
    }
}
