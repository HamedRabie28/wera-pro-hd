namespace WeraPro.Application.Settings;

using WeraPro.Application.Abstractions;
using WeraPro.Contracts.V1;

/// <summary>
/// قراءة إعدادات النشاط (قراءة فقط في G3 — التعديل شاشة إعدادات لاحقة بصلاحية).
/// كل قيمة تُعرض بحالة حسمها الحوكمي الصادقة (OWNER_CONFIRMED/PENDING/...).
/// </summary>
public sealed class GetSettingsUseCase
{
    private readonly ISettingsRepository _settings;

    public GetSettingsUseCase(ISettingsRepository settings) => _settings = settings;

    public async Task<IReadOnlyList<SettingDto>> ExecuteAsync(string? category, CancellationToken ct = default)
    {
        var rows = await _settings.ListAsync(category, ct);
        return [.. rows.Select(s => new SettingDto(
            s.SettingKey, s.ValueText, s.ValueType, s.Category,
            s.DecisionRef, s.Confirmation.ToString().ToSnakeUpper(), s.DescriptionAr))];
    }
}

internal static class EnumExtensions
{
    public static string ToSnakeUpper(this string text) =>
        string.Concat(text.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();
}
