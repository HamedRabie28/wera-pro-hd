namespace WeraPro.Api.Endpoints;

using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Auth;
using WeraPro.Application.Settings;
using WeraPro.Application.Validation;
using WeraPro.Contracts.V1;

/// <summary>
/// نقاط نهاية المصادقة — /api/v1/auth/{login,refresh,logout,me}.
/// التحقق عبر FluentValidation (ADR-007) والأخطاء 400 ProblemDetails بالعربية.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/auth").WithTags("Auth");

        // حماية brute-force: نافذة ثابتة صارمة لكل IP على الدخول/التجديد (§11.2)
        group.MapPost("/login", async (
            [FromBody] LoginRequest request,
            [FromServices] LoginUseCase login,
            [FromServices] IValidator<LoginRequest> validator,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(
                    validation.Errors.GroupBy(e => e.PropertyName).ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray()),
                    title: "طلب غير صالح",
                    detail: "راجع الحقول المشار إليها",
                    extensions: new Dictionary<string, object?>
                    {
                        ["correlationId"] = http.GetCorrelationId(),
                    });
            }

            var result = await login.ExecuteAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(
                    new ApiProblem(
                        Type: "https://wera.example/errors/" + result.Error!.Code.ToLowerInvariant(),
                        Title: "فشل تسجيل الدخول",
                        Status: StatusCodes.Status401Unauthorized,
                        Detail: result.Error.MessageAr,
                        CorrelationId: http.GetCorrelationId(),
                        OccurredAtUtc: DateTimeOffset.UtcNow),
                    statusCode: StatusCodes.Status401Unauthorized,
                    contentType: "application/problem+json");
        }).RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/refresh", async (
            [FromBody] RefreshRequest request,
            [FromServices] RefreshUseCase refresh,
            [FromServices] IValidator<RefreshRequest> validator,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }
            var result = await refresh.ExecuteAsync(request, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(
                    new ApiProblem(
                        Type: "https://wera.example/errors/" + result.Error!.Code.ToLowerInvariant(),
                        Title: "فشل تجديد الجلسة",
                        Status: StatusCodes.Status401Unauthorized,
                        Detail: result.Error.MessageAr,
                        CorrelationId: http.GetCorrelationId(),
                        OccurredAtUtc: DateTimeOffset.UtcNow),
                    statusCode: StatusCodes.Status401Unauthorized,
                    contentType: "application/problem+json");
        }).RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/logout", async (
            [FromServices] LogoutUseCase logout,
            CancellationToken ct) =>
        {
            await logout.ExecuteAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", (
            [FromServices] ICurrentUser current) =>
        {
            if (!current.IsAuthenticated)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(new
            {
                current.UserId,
                current.Username,
                Roles = current.Roles,
                BranchId = current.BranchId,
                StationId = current.StationId,
            });
        }).RequireAuthorization();

        return app;
    }
}

/// <summary>نقاط الإعدادات والفروع — قراءة فقط بصلاحيات خادمية (G3).</summary>
public static class ReferenceEndpoints
{
    public static IEndpointRouteBuilder MapReferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix).WithTags("Reference")
            .RequireRateLimiting(RateLimitPolicies.Api);

        group.MapGet("/settings", async (
            string? category,
            [FromServices] GetSettingsUseCase useCase,
            CancellationToken ct) =>
        {
            var settings = await useCase.ExecuteAsync(category, ct);
            return Results.Ok(new { items = settings, count = settings.Count });
        }).RequireAuthorization(Domain.Security.PermissionCodes.SettingsRead);

        group.MapGet("/branches", async (
            [FromServices] IBranchRepository branches,
            CancellationToken ct) =>
        {
            var items = await branches.ListActiveBranchesAsync(ct);
            return Results.Ok(new { items, count = items.Count });
        }).RequireAuthorization(Domain.Security.PermissionCodes.BranchesRead);

        group.MapGet("/branches/{branchId:long}/stations", async (
            long branchId,
            [FromServices] IBranchRepository branches,
            CancellationToken ct) =>
        {
            var items = await branches.ListStationsAsync(branchId, activeOnly: true, ct);
            return Results.Ok(new { items, count = items.Count });
        }).RequireAuthorization(Domain.Security.PermissionCodes.BranchesRead);

        return app;
    }
}
