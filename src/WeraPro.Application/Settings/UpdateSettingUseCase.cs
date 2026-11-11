namespace WeraPro.Application.Settings;

using WeraPro.Application.Abstractions;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Settings;

/// <summary>
/// تعديل إعداد نشاط بالمفتاح (PAGE-52): القيمة إعداد لا ثابت كود (§2 ق9).
/// الإعدادات ذات القرار D-xx المعلق يبقى وسمها الحوكمي كما هو — التعديل
/// المحسوم بأمر المالك يوسم OWNER_CONFIRMED (مصداقية الحسم §0.1).
/// </summary>
public sealed class UpdateSettingUseCase
{
    private readonly ISettingsRepository _settings;
    private readonly IAuditWriter _audit;

    public UpdateSettingUseCase(ISettingsRepository settings, IAuditWriter audit)
    {
        _settings = settings;
        _audit = audit;
    }

    public async Task<UpdateSettingResponse> ExecuteAsync(string settingKey, UpdateSettingRequest request, long actorUserId, CancellationToken ct = default)
    {
        var current = await _settings.FindByKeyAsync(settingKey, ct)
            ?? throw new InvalidOperationException($"الإعداد «{settingKey}» غير موجود — المفاتيح المعرفة فقط قابلة للتعديل");

        var value = request.ValueText?.Trim() ?? "";
        if (value.Length == 0)
        {
            throw new InvalidOperationException("القيمة مطلوبة — لا قيم فارغة لإعدادات نشاط");
        }
        ValidateValueType(current.ValueType, value, settingKey);

        var updated = await _settings.UpdateValueAsync(settingKey, value, ct);

        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, null, "core.business_settings", updated.SettingId,
            "SETTING_UPDATE", "SUCCESS",
            $"{settingKey}: {current.ValueText} → {value}" + (request.ReasonAr is null ? "" : $" — {request.ReasonAr}")), ct);

        return new UpdateSettingResponse(
            updated.SettingKey, updated.ValueText,
            updated.Confirmation.ToString(), updated.UpdatedAt);
    }

    /// <summary>تحقق النمط حسب نوع القيمة — رسالة عربية واضحة للواجهة.</summary>
    private static void ValidateValueType(string valueType, string value, string key)
    {
        switch (valueType)
        {
            case "integer" when !int.TryParse(value, out _):
                throw new InvalidOperationException($"قيمة «{key}» يجب أن تكون عدداً صحيحاً");
            case "decimal" when !decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _):
                throw new InvalidOperationException($"قيمة «{key}» يجب أن تكون عدداً عشرياً (مثل 0.1400)");
            case "boolean" when value is not "true" and not "false":
                throw new InvalidOperationException($"قيمة «{key}» يجب أن تكون true أو false");
        }
    }
}
