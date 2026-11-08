// ViewModels/LoginViewModel.cs — WERA PRO HD Desktop (G3)
// تسجيل الدخول: فحص خادم → قائمة فروع/محطات → دخول بجلسة موثقة.
// كل العمليات AsyncRelayCommand — لا تجميد UI إطلاقاً (بوابة G3).
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

public sealed partial class LoginViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;
    private readonly ConnectivityService _connectivity;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(WeraApiClient api, SessionService session, ConnectivityService connectivity, ILogger<LoginViewModel> logger)
    {
        _api = api;
        _session = session;
        _connectivity = connectivity;
        _logger = logger;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanLogin))]
    private bool _busy;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _statusAr = "أدخل بيانات الدخول";

    [ObservableProperty]
    private string _serverStatusAr = "لم يُتحقق من الخادم بعد";

    /// <summary>حالة الخادم الفعلية — تُحدّث نقطة الحالة في الواجهة (أخضر/كهرماني).</summary>
    [ObservableProperty]
    private bool _serverOnline;

    public ObservableCollection<BranchDto> Branches { get; } = [];
    public ObservableCollection<StationDto> Stations { get; } = [];

    [ObservableProperty]
    private BranchDto? _selectedBranch;

    [ObservableProperty]
    private StationDto? _selectedStation;

    public bool IsBusy => Busy;
    public bool CanLogin => !Busy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>حدث دخول ناجح — النافذة تفتح Shell وتغلق نفسها.</summary>
    public event Action? LoginSucceeded;

    [RelayCommand]
    public async Task CheckServerAsync(CancellationToken ct)
    {
        Busy = true;
        ServerStatusAr = "جارٍ التحقق من الخادم…";
        try
        {
            await _connectivity.ProbeAsync(ct);
            ServerOnline = _connectivity.State == ConnectionState.Online;
            ServerStatusAr = ServerOnline
                ? "الخادم جاهز"
                : "الخادم غير متاح — تحقق من العنوان";

            if (ServerOnline)
            {
                var branches = await _api.GetBranchesAsync(ct);
                Branches.Clear();
                if (branches.Ok)
                {
                    foreach (var branch in branches.Value!)
                    {
                        Branches.Add(branch);
                    }
                    StatusAr = Branches.Count > 0
                        ? "اختر الفرع والمحطة ثم أدخل بياناتك"
                        : "لا فروع نشطة معرّفة على الخادم";
                }
                else
                {
                    StatusAr = branches.ErrorAr ?? "تعذر تحميل الفروع";
                }
            }
        }
        finally
        {
            Busy = false;
        }
    }

    partial void OnSelectedBranchChanged(BranchDto? value)
    {
        Stations.Clear();
        SelectedStation = null;
        if (value is null)
        {
            return;
        }
        _ = LoadStationsAsync(value.BranchId);
    }

    private async Task LoadStationsAsync(long branchId)
    {
        var stations = await _api.GetStationsAsync(branchId);
        if (!stations.Ok)
        {
            StatusAr = stations.ErrorAr ?? "تعذر تحميل المحطات";
            return;
        }
        foreach (var station in stations.Value!)
        {
            Stations.Add(station);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    public async Task LoginAsync(CancellationToken ct)
    {
        Busy = true;
        StatusAr = "جارٍ التحقق من البيانات…";
        try
        {
            var request = new LoginRequest(
                Username.Trim(),
                Password,
                SelectedBranch?.BranchId,
                SelectedStation?.StationId);
            var result = await _api.LoginAsync(request, ct);
            if (!result.Ok || result.Value is null)
            {
                StatusAr = result.ErrorAr ?? "فشل تسجيل الدخول";
                Password = string.Empty;
                return;
            }

            _session.Establish(
                result.Value,
                SelectedBranch?.BranchId,
                SelectedStation?.StationId,
                SelectedBranch?.NameAr,
                SelectedStation?.NameAr);
            _logger.LogInformation("دخول ناجح للمستخدم {Username}", Username);
            LoginSucceeded?.Invoke();
        }
        finally
        {
            Busy = false;
        }
    }

    partial void OnBusyChanged(bool value) => LoginCommand.NotifyCanExecuteChanged();
    partial void OnUsernameChanged(string value) => LoginCommand.NotifyCanExecuteChanged();
    partial void OnPasswordChanged(string value) => LoginCommand.NotifyCanExecuteChanged();
}
