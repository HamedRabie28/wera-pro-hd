namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Security;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// إدارة المستخدمين والأدوار (PAGE-47): القراءة بصلاحية security.users.read
/// وكل تعديل بصلاحية security.users.manage (RBAC خادمي — لا يوثق بالعميل).
/// </summary>
public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/users")
            .WithTags("Users")
            .RequireAuthorization(PermissionCodes.SecurityUsersRead)
            .RequireRateLimiting(RateLimitPolicies.Api);

        group.MapGet("/", async (
            [FromQuery] string? search,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var users = await useCase.ListAsync(search, ct);
            return Results.Ok(new { items = users, count = users.Count });
        });

        group.MapPost("/", async (
            [FromBody] CreateUserRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var user = await useCase.CreateAsync(request, current.UserId, ct);
            return Results.Created(ApiRoutes.Users + "/" + user.UserId, user);
        }).RequireAuthorization(PermissionCodes.SecurityUsersManage);

        group.MapPut("/{userId:long}", async (
            long userId,
            [FromBody] UpdateUserRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var ok = await useCase.UpdateProfileAsync(userId, request, current.UserId, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "المستخدم غير موجود" });
        }).RequireAuthorization(PermissionCodes.SecurityUsersManage);

        group.MapPut("/{userId:long}/active", async (
            long userId,
            [FromBody] SetUserActiveRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var ok = await useCase.SetActiveAsync(userId, request, current.UserId, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "المستخدم غير موجود" });
        }).RequireAuthorization(PermissionCodes.SecurityUsersManage);

        group.MapPut("/{userId:long}/password", async (
            long userId,
            [FromBody] ResetPasswordRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var ok = await useCase.ResetPasswordAsync(userId, request, current.UserId, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "المستخدم غير موجود" });
        }).RequireAuthorization(PermissionCodes.SecurityUsersManage);

        group.MapGet("/{userId:long}/roles", async (
            long userId,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var roles = await useCase.ListUserRolesAsync(userId, ct);
            return Results.Ok(new { items = roles, count = roles.Count });
        });

        group.MapPost("/{userId:long}/roles", async (
            long userId,
            [FromBody] AssignRoleRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var assignment = await useCase.AssignRoleAsync(userId, request, current.UserId, ct);
            return Results.Created(ApiRoutes.Users + $"/{userId}/roles", assignment);
        }).RequireAuthorization(PermissionCodes.SecurityUsersManage);

        group.MapDelete("/{userId:long}/roles", async (
            long userId,
            [FromBody] RevokeRoleRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var ok = await useCase.RevokeRoleAsync(userId, request, current.UserId, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "الإسناد غير موجود" });
        }).RequireAuthorization(PermissionCodes.SecurityUsersManage);

        // الأدوار المرجعية وصلاحياتها — قراءة لكل من يرى الصفحة
        var roles = app.MapGroup("/" + ApiRoutes.Prefix + "/roles")
            .WithTags("Users")
            .RequireAuthorization(PermissionCodes.SecurityUsersRead)
            .RequireRateLimiting(RateLimitPolicies.Api);

        roles.MapGet("/", async (
            [FromServices] UsersAdminUseCase useCase,
            CancellationToken ct) =>
        {
            var items = await useCase.ListRolesAsync(ct);
            return Results.Ok(new { items, count = items.Count });
        });

        return app;
    }
}
