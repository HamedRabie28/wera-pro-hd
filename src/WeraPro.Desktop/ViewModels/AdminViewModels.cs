// ViewModels/AdminViewModels.cs — WERA PRO HD Desktop (G-ADMIN)
// المستخدمون والأدوار (PAGE-47) والإعدادات (PAGE-52) والنسخ الاحتياطي والصيانة
// (PAGE-51 + منطقة الخطر) — كل الأمر عبر الخادم بذرات صلاحيات؛ الواجهة تعرض
// الحقيقة (من يملك الصلاحية يرى الأزرار، ومن لا يملكها يرى سبب التعطيل المعلن).
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

// ═══ المستخدمون والأدوار (PAGE-47) ═══

/// <summary>سطر مستخدم للجدول — بلا أي hash أو أسرار (§18).</summary>
public sealed class UserRow
{
    public required UserAdminDto Source { get; init; }
    public long UserId => Source.UserId;
    public string Username => Source.Username;
    public string DisplayNameAr => Source.DisplayNameAr;
    public string EmailAr => Source.Email ?? "—";
    public string RolesAr => Source.RoleNamesAr.Count == 0 ? "بلا دور" : string.Join(" · ", Source.RoleNamesAr);
    public string StatusAr => Source.IsActive ? "نشط" : "معطّل";
    public bool IsActive => Source.IsActive;
    public string ToggleLabel => Source.IsActive ? "تعطيل" : "تفعيل";
    /// <summary>لون الحالة — من ألوان النظام الدلالية (نجاح/خطر).</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusBrush => Source.IsActive
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0x84, 0x5D))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x48, 0x50));
}

/// <summary>صلاحية ذرية للعرض — مجالها معرّب داخل صف الأدوار.</summary>
public sealed class PermissionRow
{
    public required PermissionDto Source { get; init; }
    public string Code => Source.Code;
    public string DomainAr => RoleRow.DomainLabel(Source.DomainArea);
    public string DescriptionAr => Source.DescriptionAr;
}

/// <summary>دور مرجعي مع مجموعات صلاحياته مجمّعة بالمجال — لعرض مصفوفة الصلاحيات.</summary>
public sealed class RoleRow
{
    public required RoleDetailDto Source { get; init; }
    public string Code => Source.Code;
    public string NameAr => Source.NameAr;
    public string DescriptionAr => Source.Description ?? "";
    public string PermissionsCountAr => Source.Permissions.Count + " صلاحية";
    /// <summary>الصلاحيات مجمّعة بالمجال: «مبيعات: 6 · مخزون: 4»</summary>
    public string DomainsAr => string.Join(" · ", Source.Permissions
        .GroupBy(p => p.DomainArea)
        .Select(g => DomainLabel(g.Key) + ": " + g.Count()));
    public IReadOnlyList<PermissionRow> PermissionRows =>
        [.. Source.Permissions.Select(p => new PermissionRow { Source = p })];

    public static string DomainLabel(string domain) => domain switch
    {
        "sales" => "مبيعات", "catalog" => "كتالوج", "inventory" => "مخزون",
        "purchasing" => "مشتريات", "finance" => "مالية", "printing" => "طباعة",
        "security" => "أمن", "ops" => "تشغيل", "reports" => "تقارير",
        _ => domain,
    };
}

public sealed partial class UsersViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public UsersViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public ObservableCollection<UserRow> Users { get; } = [];
    public ObservableCollection<RoleRow> Roles { get; } = [];

    [ObservableProperty] private string _statusAr = "جاهز";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorAr;

    public string Search { get; set; } = "";
    /// <summary>هل الجلسة تملك إدارة المستخدمين؟ (security.users.manage خادمياً)</summary>
    public bool CanManage => _session.User?.Permissions.Contains("security.users.manage") == true;
    /// <summary>هل الجلسة ترى الصفحة أصلاً؟ (security.users.read)</summary>
    public bool CanRead => _session.User?.Permissions.Contains("security.users.read") == true
        || _session.User?.Permissions.Contains("security.users.manage") == true;

    public long CurrentUserId => _session.User?.UserId ?? 0;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetUsersAsync(string.IsNullOrWhiteSpace(Search) ? null : Search, ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل المستخدمين";
            StatusAr = "فشل التحميل";
            return;
        }
        ErrorAr = null;
        Users.Clear();
        foreach (var u in result.Value!) Users.Add(new UserRow { Source = u });
        StatusAr = $"{Users.Count} مستخدماً — التعطيل لا الحذف؛ كلمات المرور hash فقط (§18)";
    }

    [RelayCommand]
    public async Task LoadRolesAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetRolesAsync(ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل الأدوار";
            return;
        }
        ErrorAr = null;
        Roles.Clear();
        foreach (var r in result.Value!) Roles.Add(new RoleRow { Source = r });
        StatusAr = $"{Roles.Count} أدوار مرجعية صلبة (V009) — الإسناد والفك فقط لا تحرير الأدوار";
    }

    public async Task<bool> CreateUserAsync(string username, string displayName, string? email,
        string password, string roleCode, long? branchId, CancellationToken ct)
    {
        var result = await _api.CreateUserAsync(
            new CreateUserRequest(username, displayName, email, password, roleCode, branchId), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        await LoadAsync(ct);
        return true;
    }

    public async Task<bool> UpdateUserAsync(long userId, string displayName, string? email, CancellationToken ct)
    {
        var result = await _api.UpdateUserAsync(userId, new UpdateUserRequest(displayName, email), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        await LoadAsync(ct);
        return true;
    }

    public async Task<bool> ToggleActiveAsync(UserRow row, string? reason, CancellationToken ct)
    {
        var result = await _api.SetUserActiveAsync(row.UserId,
            new SetUserActiveRequest(!row.IsActive, reason), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        await LoadAsync(ct);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(long userId, string newPassword, CancellationToken ct)
    {
        var result = await _api.ResetUserPasswordAsync(userId,
            new ResetPasswordRequest(newPassword, "إعادة تعيين من صفحة المستخدمين"), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        return true;
    }

    public async Task<bool> AssignRoleAsync(long userId, string roleCode, long? branchId, CancellationToken ct)
    {
        var result = await _api.AssignUserRoleAsync(userId,
            new AssignRoleRequest(roleCode, branchId, "إسناد من صفحة المستخدمين"), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        await LoadAsync(ct);
        return true;
    }

    public async Task<bool> RevokeRoleAsync(long userId, long userRoleId, CancellationToken ct)
    {
        var result = await _api.RevokeUserRoleAsync(userId,
            new RevokeRoleRequest(userRoleId, "سحب من صفحة المستخدمين"), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        await LoadAsync(ct);
        return true;
    }
}

// ═══ الإعدادات العامة (PAGE-52) ═══

/// <summary>سطر إعداد — يعرض القيمة وحالة الحسم الحوكمي بصدق (§0.1).</summary>
public sealed class SettingRow
{
    public required SettingDto Source { get; init; }
    public string Key => Source.SettingKey;
    public string ValueAr => Source.ValueText;
    public string ValueTypeAr => Source.ValueType switch
    {
        "string" => "نص", "integer" => "عدد صحيح", "decimal" => "عدد عشري",
        "boolean" => "نعم/لا", "enum" => "خيار", "json" => "بنية", _ => Source.ValueType,
    };
    public string DecisionAr => Source.DecisionRef ?? "—";
    public string ConfirmationAr => Source.ConfirmationStatus switch
    {
        "OWNER_CONFIRMED" => "محسوم بأمر مالك",
        "PENDING_OWNER_CONFIRMATION" => "توصية بانتظار حسم المالك",
        "APPROVED_WITH_SPEC" => "معتمد بالمواصفة",
        _ => Source.ConfirmationStatus,
    };
    public bool IsPending => Source.ConfirmationStatus == "PENDING_OWNER_CONFIRMATION";
    /// <summary>وسم القرار المرجعي: «D-01» أو «بدون قرار مرجعي».</summary>
    public string DecisionLabelAr => "قرار: " + (Source.DecisionRef ?? "—");
    public string DescriptionAr => Source.DescriptionAr;
    public string Category => Source.Category;
    public string CategoryAr => CategoryLabel(Source.Category);

    public static string CategoryLabel(string category) => category switch
    {
        "TAX" => "الضرائب", "INVENTORY" => "المخزون", "PRICING" => "التسعير",
        "WEIGHT" => "الوزن", "RETURNS" => "المرتجعات", "CREDIT" => "الآجل",
        "PAYMENTS" => "المدفوعات", "OFFLINE" => "العمل بلا اتصال",
        "RETENTION" => "الاحتفاظ", "ACCOUNTING" => "المحاسبة",
        "PACKAGING" => "التغليف", "PRINTING" => "الطباعة", "GENERAL" => "عام",
        _ => category,
    };
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public SettingsViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    /// <summary>كل الإعدادات — التجميع بالتصنيف يجري في العرض (قائمة صغيرة محكومة).</summary>
    public ObservableCollection<SettingRow> Settings { get; } = [];

    [ObservableProperty] private string _statusAr = "جاهز";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorAr;
    [ObservableProperty] private string _selectedCategory = "الكل";

    /// <summary>فئات العرض — الكل + التصنيفات المزروعة فعلياً.</summary>
    public List<string> Categories { get; } = ["الكل", "الضرائب", "المخزون", "التسعير", "الوزن",
        "المرتجعات", "الآجل", "المدفوعات", "العمل بلا اتصال", "الاحتفاظ", "المحاسبة", "التغليف", "الطباعة", "عام"];

    public bool CanManage => _session.User?.Permissions.Contains("ops.settings.manage") == true;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetSettingsAsync(category: null, ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل الإعدادات";
            StatusAr = "فشل التحميل";
            return;
        }
        ErrorAr = null;
        Settings.Clear();
        foreach (var s in result.Value!) Settings.Add(new SettingRow { Source = s });
        var pending = Settings.Count(s => s.IsPending);
        StatusAr = $"{Settings.Count} إعداداً — {pending} توصية بانتظار حسم المالك (تُعرض بصدق كما هي)";
    }

    /// <summary>الإعدادات بعد فلتر التصنيف المختار.</summary>
    public IEnumerable<SettingRow> Filtered =>
        SelectedCategory == "الكل" ? Settings : Settings.Where(s => s.CategoryAr == SelectedCategory);

    partial void OnSelectedCategoryChanged(string value) => OnPropertyChanged(nameof(Filtered));

    public async Task<bool> UpdateAsync(SettingRow row, string newValue, string? reason, CancellationToken ct)
    {
        var result = await _api.UpdateSettingAsync(row.Key, new UpdateSettingRequest(newValue, reason), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return false;
        }
        ErrorAr = null;
        await LoadAsync(ct);
        return true;
    }
}

// ═══ النسخ الاحتياطي والصيانة (PAGE-51 + منطقة الخطر) ═══

/// <summary>سطر نسخة احتياطية — سلسلة إسناد: من، متى، أي ملف، أي بصمة.</summary>
public sealed class BackupRow
{
    public required BackupDto Source { get; init; }
    public long BackupId => Source.BackupId;
    public string FileName => Source.FileName;
    public string KindAr => Source.Kind switch { "FULL" => "كاملة", "INCREMENTAL" => "تفاضلية", _ => Source.Kind };
    public string StatusAr => Source.Status switch
    {
        "VERIFIED" => "موثقة قابلة للقراءة", "SUCCEEDED" => "ناجحة",
        "RUNNING" => "جارية", "VERIFYING" => "قيد التحقق", "FAILED" => "فاشلة", _ => Source.Status,
    };
    public bool Verified => Source.Status == "VERIFIED";
    public string SizeAr => Source.SizeBytes <= 0 ? "—"
        : Source.SizeBytes > 1024 * 1024 ? (Source.SizeBytes / 1024.0 / 1024.0).ToString("N1") + " م.ب"
        : (Source.SizeBytes / 1024.0).ToString("N0") + " ك.ب";
    public string WhenAr => Source.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CreatedBy => Source.CreatedBy;
    public string ChecksumAr => string.IsNullOrWhiteSpace(Source.Checksum) ? "—" : Source.Checksum[..12] + "…";
    public string NoteAr => Source.NoteAr ?? "";
    /// <summary>لون الحالة — أخضر للموثقة، أحمر للفاشلة، رمادي لما عداها.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusBrush => Source.Status switch
    {
        "VERIFIED" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0x84, 0x5D)),
        "FAILED" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x48, 0x50)),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D)),
    };
}

public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public MaintenanceViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public ObservableCollection<BackupRow> Backups { get; } = [];

    [ObservableProperty] private string _statusAr = "جاهز";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorAr;

    // إحصاءات حقيقية — تُعرض قبل أي قرار حذف (قاعدة الصدق)
    [ObservableProperty] private string _statsAr = "—";
    [ObservableProperty] private string _dbNameAr = "—";

    public bool CanRunBackup => _session.User?.Permissions.Contains("ops.backup.run") == true;
    public bool CanRestore => _session.User?.Permissions.Contains("ops.backup.restore") == true;
    public bool CanCleanup => _session.User?.Permissions.Contains("ops.system.cleanup") == true;
    public bool CanWipe => _session.User?.Permissions.Contains("ops.system.wipe") == true;

    [RelayCommand]
    public async Task LoadBackupsAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetBackupsAsync(limit: 50, ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل سجل النسخ";
            StatusAr = "فشل التحميل";
            return;
        }
        ErrorAr = null;
        Backups.Clear();
        foreach (var b in result.Value!) Backups.Add(new BackupRow { Source = b });
        StatusAr = $"{Backups.Count} نسخة في السجل — النجاح لا يعلن إلا بعد تحقق قابلية القراءة (§19 D-09)";
    }

    [RelayCommand]
    public async Task LoadStatsAsync(CancellationToken ct)
    {
        var result = await _api.GetSystemStatsAsync(ct);
        if (!result.Ok)
        {
            StatsAr = "تعذر جلب الإحصاءات: " + (result.ErrorAr ?? "");
            return;
        }
        var s = result.Value!;
        DbNameAr = "قاعدة البيانات: " + s.DatabaseName;
        StatsAr = $"منتجات {s.Products:N0} · صيغ {s.Skus:N0} · حركات مخزون {s.StockMovements:N0} · فواتير {s.Invoices:N0}\n" +
                  $"وسائط {s.MediaAssets:N0} · أحداث تدقيق {s.AuditEvents:N0} · جلسات مفتوحة {s.OpenSessions:N0} · مهام طباعة {s.PrintJobs:N0} · نسخ {s.BackupRecords:N0} · مستخدمون {s.Users:N0}";
    }

    public async Task<bool> RunBackupAsync(string? note, CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.RunBackupAsync(new RunBackupRequest(note), ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "فشل تشغيل النسخة";
            return false;
        }
        ErrorAr = null;
        await LoadBackupsAsync(ct);
        StatusAr = "اكتملت النسخة وتم التحقق من قابلية قراءتها — انسخها لوسائط خارجية (نسخة القرص ليست خطة استعادة §19)";
        return true;
    }

    public async Task<bool> RestoreAsync(BackupRow row, string confirmText, string? reason, CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.RestoreBackupAsync(
            new RestoreBackupRequest(row.BackupId, confirmText, reason), ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "فشلت الاستعادة";
            return false;
        }
        ErrorAr = null;
        StatusAr = "اكتملت الاستعادة — أعد تشغيل التطبيق ليعيد تحميل كل البيانات";
        return true;
    }

    public async Task<bool> DeleteBackupAsync(BackupRow row, CancellationToken ct)
    {
        var result = await _api.DeleteBackupAsync(row.BackupId, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "فشل حذف سجل النسخة";
            return false;
        }
        ErrorAr = null;
        await LoadBackupsAsync(ct);
        return true;
    }

    public async Task<bool> CleanupAsync(int retentionDays, CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.RunSystemCleanupAsync(new SystemCleanupRequest(retentionDays, "تنظيف من صفحة الصيانة"), ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "فشل التنظيف";
            return false;
        }
        var r = result.Value!;
        ErrorAr = null;
        StatusAr = $"اكتمل التنظيف: أحداث تدقيق محذوفة {r.PurgedAuditEvents:N0} · جلسات مغلقة {r.ClosedStaleSessions:N0} · وسائط يتيمة {r.RemovedOrphanMedia:N0} · مهام طباعة {r.ArchivedCompletedJobs:N0}";
        await LoadStatsAsync(ct);
        return true;
    }

    /// <summary>الحذف الكامل — لا يستدعى إلا بعد تحذير مزدوج وعبارة «حذف نهائي».</summary>
    public async Task<bool> WipeAllAsync(string confirmText, string? reason, CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.WipeAllDataAsync(new WipeDataRequest(confirmText, reason), ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "فُرض رفض الحذف";
            return false;
        }
        var w = result.Value!;
        ErrorAr = null;
        StatusAr = $"حُذف بيانات التشغيل من {w.TruncatedTables} جدولاً — بقي: {string.Join(" · ", w.PreservedSchemas)}. {w.WarningAr}";
        await LoadStatsAsync(ct);
        return true;
    }
}
