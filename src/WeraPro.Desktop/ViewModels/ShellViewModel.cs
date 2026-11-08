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
    public async Task LogoutAsync(CancellationToken ct)
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
/// POS الكاملة انتقلت إلى PosViewModel.cs المستقل في G7 (§9 كاملة) — هذه القشرة أزيلت.

