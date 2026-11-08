// ViewModels/LoginViewModel.cs — WERA PRO HD Desktop (G3 · إصدار 2026-11-09)
// بوابة «الزجاج الكامل»: اسم مستخدم + كلمة مرور + زر دخول فقط (أمر المالك).
// مسار الدخول عامل طورين:
//   (1) فحص الخادم (نبضة كل 30 ثانية) — بلا تحميل فروع قبل الدخول: نقاط
//       الفروع/المحطات تتطلب مصادقة، فكان تحميلها قبل الدخول كوداً ميتاً
//       يفشل 401 دائماً (إصلاح 2026-11-09).
//   (2) الدخول بنطاق مركزي ثم إثراء الجلسة بالرمز الجديد: تُجلب الفروع
//       والمحطات ويُختار أول نشط منها تلقائياً (BranchId/StationId/الأسماء)
//       عبر SessionService.EnrichScope — فتفتح القشرة وPOS بنطاق كامل
//       دون أي محددات في واجهة الدخول.
// كل العمليات AsyncRelayCommand — لا تجميد UI إطلاقاً (بوابة G3).
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

    /// <summary>نطاق الجلسة بعد الإثراء (لا محددات في الواجهة — للاستهلاك الداخلي).</summary>
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
            StatusAr = ServerOnline
                ? "الخادم جاهز — أدخل اسم المستخدم وكلمة المرور"
                : "لا يمكن الوصول إلى الخادم الآن";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// إثراء النطاق بعد الدخول: أول فرع نشط ثم أول محطة له.
    /// اختياري تماماً — فشله لا يُفشل الدخول (يُكمل بنطاق مركزي).
    /// </summary>
    private async Task LoadScopeAsync(CancellationToken ct)
    {
        var branches = await _api.GetBranchesAsync(ct);
        if (!branches.Ok)
        {
            _logger.LogWarning("إثراء النطاق: تعذر جلب الفروع — {Error}", branches.ErrorAr);
            return;
        }
        SelectedBranch = branches.Value?.FirstOrDefault();
        if (SelectedBranch is null)
        {
            return;
        }
        var stations = await _api.GetStationsAsync(SelectedBranch.BranchId, ct);
        if (!stations.Ok)
        {
            _logger.LogWarning("إثراء النطاق: تعذر جلب محطات {Branch} — {Error}",
                SelectedBranch.NameAr, stations.ErrorAr);
            return;
        }
        SelectedStation = stations.Value?.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    public async Task LoginAsync(CancellationToken ct)
    {
        Busy = true;
        StatusAr = "جارٍ التحقق من البيانات…";
        try
        {
            // الطور 1: دخول بنطاق مركزي (null) — لا يمكن معرفة الفرع قبل امتلاك
            // الرمز لأن نقاط الفروع تتطلب مصادقة (إصلاح الكود الميت السابق)
            var request = new LoginRequest(Username.Trim(), Password, null, null);
            var result = await _api.LoginAsync(request, ct);
            if (!result.Ok || result.Value is null)
            {
                StatusAr = result.ErrorAr ?? "فشل تسجيل الدخول";
                Password = string.Empty;
                return;
            }

            // الجلسة حية الآن (الرمز يرفقه المعالج تلقائياً) — الطور 2: الإثراء
            // (LoadScopeAsync لا يرمي استثناءات: كل فشل يعود ApiCallResult.Fail)
            _session.Establish(result.Value, null, null, null, null);
            StatusAr = "جارٍ تهيئة نطاق العمل…";
            await LoadScopeAsync(ct);
            _session.EnrichScope(
                SelectedBranch?.BranchId, SelectedStation?.StationId,
                SelectedBranch?.NameAr, SelectedStation?.NameAr);

            _logger.LogInformation(
                "دخول ناجح للمستخدم {Username} — النطاق: {Branch} / {Station}",
                Username, SelectedBranch?.NameAr ?? "مركزي", SelectedStation?.NameAr ?? "بدون محطة");
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
