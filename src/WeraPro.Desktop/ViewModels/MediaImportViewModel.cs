// ViewModels/MediaImportViewModel.cs — WERA PRO HD Desktop (G5)
// مركز استيراد الصور الدفعي (PAGE-17): إنشاء مهمة → ترحيل ملفات إلى
// staging → تشغيل (المعالجة عند العامل الخلفي) → إلغاء آمن/استئناف →
// تقرير لكل ملف (PENDING/PROCESSING/DONE/FAILED/SKIPPED) — حالة كل ملف
// من القاعدة لا من عداد (§6.1). الاستقصاء كل 2s أثناء RUNNING فقط.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>صف ملف في تقرير المهمة — حالته النهائية أو الجارية من القاعدة.</summary>
public sealed class ImportFileRow
{
    public required ImportFileDto Source { get; init; }
    public int Ordinal => Source.Ordinal;
    public string NameAr => Source.ClientName;
    public string SizeAr => (Source.SizeBytes / 1024.0).ToString("N0") + " KB";
    public string StateAr => Source.State switch
    {
        "PENDING" => "بالانتظار",
        "PROCESSING" => "قيد المعالجة",
        "DONE" => "مكتمل",
        "FAILED" => "فشل",
        "SKIPPED" => "مكرر (تخطٍ)",
        _ => Source.State,
    };
    public string DetailAr => Source.State switch
    {
        "DONE" => "أصل #" + Source.MediaId,
        "FAILED" => Source.ErrorCode ?? "سبب غير معروف",
        "SKIPPED" => Source.SkipReason ?? "تخطٍ",
        _ => "—",
    };
}

/// <summary>
/// نموذج استيراد دفعي: لا يوازي العميل شيئاً — الترحيل فقط ثم الاستقصاء؛
/// الرفض عند الإضافة (نوع/حجم/تلف ترويسة) يظهر فوراً لكل ملف FAILED
/// قبل أي تشغيل. الصلاحية catalog.import.run (م، خ — PAGE-17).
/// </summary>
public sealed partial class MediaImportViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public const int PageSize = 50;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string? _successAr;

    [ObservableProperty]
    private string _labelText = string.Empty;

    [ObservableProperty]
    private long? _jobId;

    [ObservableProperty]
    private string _jobStateAr = "لا مهمة";

    [ObservableProperty]
    private string _countsAr = "—";

    [ObservableProperty]
    private string _errorsHistogramAr = "";

    [ObservableProperty]
    private string _scopeNoteAr = "";

    /// <summary>عداد ملفات مرحّلة محلياً قبل بدء التشغيل (للعرض فقط — الحقيقة في القاعدة).</summary>
    [ObservableProperty]
    private int _stagedCount;

    [ObservableProperty]
    private int _stateFilterIndex;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    public ObservableCollection<ImportFileRow> Files { get; } = [];

    public static readonly string[] StateFilters =
        ["الكل", "بالانتظار", "قيد المعالجة", "مكتمل", "فشل", "مكرر (تخطٍ)"];
    private static readonly string?[] StateCodes = [null, "PENDING", "PROCESSING", "DONE", "FAILED", "SKIPPED"];

    public string SummaryAr => Files.Count == 0
        ? "لا صفوف في هذه الصفحة"
        : $"الصفحة {Page} من {TotalPages} — تقرير لكل ملف من القاعدة (لا عداد تراكمي §6.1)";

    public bool CanImport => _session.User?.Permissions.Contains("catalog.import.run") == true;
    public bool HasJob => JobId is not null;
    public bool IsRunning => JobStateAr == "مهمة جارية";
    public bool CanStart => CanImport && JobId is not null && JobStateAr is "لا مهمة" or "مهمة مفتوحة" or "مهمة ملغاة";
    public bool CanCancel => CanImport && JobId is not null && JobStateAr is "مهمة جارية" or "مهمة مفتوحة";

    public event Action? ImportFinished;   // مهمة بلغت حالة نهائية (COMPLETED/CANCELLED)

    public MediaImportViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    [RelayCommand]
    public async Task CreateJobAsync(CancellationToken ct)
    {
        IsBusy = true;
        ErrorAr = null;
        SuccessAr = null;
        try
        {
            if (LabelText.Trim().Length is < 3 or > 120)
            {
                ErrorAr = "اسم المهمة بين 3 و120 محرفاً";
                return;
            }
            var result = await _api.CreateImportJobAsync(LabelText.Trim(), ct);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر إنشاء المهمة";
                return;
            }
            JobId = result.Value.JobId;
            JobStateAr = StateAr(result.Value.State);
            ScopeNoteAr = "نطاق التخزين: " + result.Value.StorageScope + " — لا يعالجها إلا مثيل يملك جذر بايتاتها";
            StagedCount = 0;
            Files.Clear();
            SuccessAr = "أُنشئت المهمة #" + result.Value.JobId + " (OPEN) — أضف الملفات ثم شغّلها";
            RefreshCommands();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>ترحيل ملفات إلى staging المهمة (قبل التشغيل فقط — RUNNING يرفض الإضافة 409).</summary>
    public async Task AddFilesAsync(IReadOnlyList<(string FileName, Stream Content)> files, CancellationToken ct)
    {
        if (JobId is not { } jobId)
        {
            ErrorAr = "أنشئ مهمة أولاً";
            return;
        }
        IsBusy = true;
        ErrorAr = null;
        try
        {
            var result = await _api.AddImportFilesAsync(jobId, files, ct);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر ترحيل الملفات";
                return;
            }
            StagedCount += result.Value.Rows.Count;
            var rejected = result.Value.Rows.Count(r => r.State == "FAILED");
            SuccessAr = result.Value.Rows.Count + " ملفاً رُحِّلت"
                + (rejected > 0 ? $" — منها {rejected} مرفوضة عند الإضافة (نوع/حجم/تلف)" : "");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task StartAsync(CancellationToken ct)
    {
        if (JobId is not { } jobId)
        {
            return;
        }
        IsBusy = true;
        ErrorAr = null;
        try
        {
            var result = await _api.StartImportJobAsync(jobId, ct);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر تشغيل المهمة";
                return;
            }
            ApplyJob(result.Value);
            SuccessAr = "المهمة جارية — المعالجة عند العامل الخلفي للخادم";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    [RelayCommand]
    public async Task CancelAsync(CancellationToken ct)
    {
        if (JobId is not { } jobId)
        {
            return;
        }
        IsBusy = true;
        ErrorAr = null;
        try
        {
            var result = await _api.CancelImportJobAsync(jobId, ct);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر الإلغاء";
                return;
            }
            ApplyJob(result.Value);
            SuccessAr = "أُلغيت بأمان — لا ملف قيد المعالجة يُلقى؛ الاستئناف يقرأ حالات الملفات";
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    /// <summary>استقصاء حالة المهمة + تحديث التقرير — يُستدعى من مؤقت النافذة كل 2s عند RUNNING.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        if (JobId is not { } jobId)
        {
            return;
        }
        var result = await _api.GetImportJobAsync(jobId, ct);
        if (!result.Ok || result.Value is null)
        {
            return;
        }
        var job = result.Value;
        var wasRunning = JobStateAr == "مهمة جارية";
        ApplyJob(job);
        if (wasRunning && job.State is "COMPLETED" or "CANCELLED")
        {
            ImportFinished?.Invoke();
        }
    }

    [RelayCommand]
    public async Task LoadFilesAsync(CancellationToken ct)
    {
        if (JobId is not { } jobId)
        {
            return;
        }
        try
        {
            var result = await _api.GetImportFilesAsync(jobId, StateCodes[StateFilterIndex], Page, PageSize, ct);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر تحميل التقرير";
                return;
            }
            ErrorAr = null;
            TotalPages = Math.Max(1, result.Value.TotalCount <= 0 ? 1 : (int)Math.Ceiling(result.Value.TotalCount / (double)PageSize));
            Files.Clear();
            foreach (var row in result.Value.Items)
            {
                Files.Add(new ImportFileRow { Source = row });
            }
            OnPropertyChanged(nameof(SummaryAr));
        }
        finally
        {
            OnPropertyChanged(nameof(SummaryAr));
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync(CancellationToken ct)
    {
        if (Page > 1)
        {
            Page--;
            await LoadFilesAsync(ct);
        }
    }

    [RelayCommand]
    public async Task NextPageAsync(CancellationToken ct)
    {
        if (Page < TotalPages)
        {
            Page++;
            await LoadFilesAsync(ct);
        }
    }

    /// <summary>تقرير المهمة: الأعداد + histogram الأخطاء (لكل ملف سبب مسمى).</summary>
    public async Task LoadReportAsync(CancellationToken ct)
    {
        if (JobId is not { } jobId)
        {
            return;
        }
        var result = await _api.GetImportReportAsync(jobId, ct);
        if (!result.Ok || result.Value is null)
        {
            return;
        }
        var c = result.Value.Job.Counts;
        CountsAr = $"انتظار {c.Pending:N0} · معالجة {c.Processing:N0} · مكتمل {c.Done:N0} · فشل {c.Failed:N0} · تخطٍ {c.Skipped:N0} (المجموع {c.Total:N0})";
        ErrorsHistogramAr = result.Value.ErrorsByCode.Count == 0
            ? "لا أخطاء مسجلة"
            : "الأخطاء: " + string.Join(" · ", result.Value.ErrorsByCode.Select(kv => kv.Key + " ×" + kv.Value));
    }

    public void ApplyStateFilter(int index)
    {
        StateFilterIndex = index;
        Page = 1;
    }

    private void ApplyJob(ImportJobDto job)
    {
        JobStateAr = StateAr(job.State);
        if (job.State is "COMPLETED" or "CANCELLED")
        {
            ImportFinished?.Invoke();
        }
        RefreshCommands();
    }

    private static string StateAr(string state) => state switch
    {
        "OPEN" => "مهمة مفتوحة",
        "RUNNING" => "مهمة جارية",
        "COMPLETED" => "مهمة مكتملة",
        "CANCELLED" => "مهمة ملغاة",
        _ => state,
    };

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(HasJob));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanImport));
    }
}
