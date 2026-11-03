namespace WeraPro.Domain.Settings;

/// <summary>حالة الحسم الحوكمي للإعداد — مصداقية العرض تعتمد عليها (V001 core.business_settings).</summary>
public enum ConfirmationStatus
{
    /// <summary>قيمة موسومة بمصدر مواصفة — ليست قرار مالك مستقلاً.</summary>
    ApprovedWithSpec,
    /// <summary>توصية بانتظار حسم المالك — البنية تعمل والقيمة قابلة للتغيير.</summary>
    PendingOwnerConfirmation,
    /// <summary>محسوم بأمر مالك موثق (مثل V011/V012).</summary>
    OwnerConfirmed,
}

/// <summary>إعداد نشاط واحد من core.business_settings — كل القرارات إعدادات لا ثوابت كود.</summary>
public sealed class BusinessSetting
{
    public long SettingId { get; init; }
    public required string SettingKey { get; init; }
    public required string ScopeType { get; init; }        // BUSINESS | BRANCH
    public long? BranchId { get; init; }
    public required string ValueText { get; init; }
    public required string ValueType { get; init; }        // string|integer|decimal|boolean|enum|json
    public required string Category { get; init; }
    public string? DecisionRef { get; init; }              // D-xx
    public ConfirmationStatus Confirmation { get; init; }
    public required string DescriptionAr { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
