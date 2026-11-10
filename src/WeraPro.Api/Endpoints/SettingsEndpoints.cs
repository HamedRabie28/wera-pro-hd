namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Settings;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// تعديل إعدادات النشاط (PAGE-52): القراءة موجودة في ReferenceEndpoints —
/// هنا التعديل بالمفتاح بصلاحية ops.settings.manage (OWNER) مع توثيق القيمة
/// القديمة والجديدة في التدقيق.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/settings")
            .WithTags("Settings")
            .RequireRateLimiting(RateLimitPolicies.Api);

        group.MapPut("/{settingKey}", async (
            string settingKey,
            [FromBody] UpdateSettingRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UpdateSettingUseCase useCase,
            CancellationToken ct) =>
        {
            var updated = await useCase.ExecuteAsync(settingKey, request, current.UserId, ct);
            return Results.Ok(updated);
        }).RequireAuthorization(PermissionCodes.SettingsManage);

        return app;
    }
}
