namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Application.Audit;
using WeraPro.Domain.Security;

/// <summary>
/// سجل أحداث الدخول (§11.2/§18): أحدث أحداث LOGIN (نجاح/رفض) — عبر حالة استخدام
/// Application (البوابة الإلزامية) وصلاحية security.audit.view الذرية:
/// OWNER/AUDITOR يمرّان، الكاشير مرفوض 403 (RBAC خادمي مفروض بالاختبارات).
/// </summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + Contracts.V1.ApiRoutes.Prefix + "/audit")
            .WithTags("Audit")
            .RequireAuthorization(PermissionCodes.AuditView)
            .RequireRateLimiting(RateLimitPolicies.Api);

        group.MapGet("/logins", async (
            [FromQuery] int limit,
            [FromServices] GetLoginEventsUseCase useCase,
            CancellationToken ct) =>
        {
            var events = await useCase.ExecuteAsync(limit is 0 ? 50 : limit, ct);
            return Results.Ok(new { items = events, count = events.Count });
        });

        return app;
    }
}
