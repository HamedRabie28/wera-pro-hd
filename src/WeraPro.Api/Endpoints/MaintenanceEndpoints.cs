namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Maintenance;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// النسخ الاحتياطي والصيانة (PAGE-51): القراءة ops.backup.read، التشغيل
/// ops.backup.run، الاستعادة ops.backup.restore (حرجة)، التنظيف
/// ops.system.cleanup، والحذف الكامل ops.system.wipe (أشد الصلاحيات).
/// </summary>
public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/maintenance")
            .WithTags("Maintenance")
            .RequireAuthorization(PermissionCodes.BackupRead)
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── النسخ الاحتياطية ──
        group.MapGet("/backups", async (
            [FromQuery] int limit,
            [FromServices] BackupUseCase useCase,
            CancellationToken ct) =>
        {
            var items = await useCase.ListAsync(limit, ct);
            return Results.Ok(new { items, count = items.Count });
        });

        group.MapPost("/backups/run", async (
            [FromBody] RunBackupRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] BackupUseCase useCase,
            CancellationToken ct) =>
        {
            var backup = await useCase.RunFullBackupAsync(request, current.UserId, current.Username, ct);
            return Results.Ok(backup);
        }).RequireAuthorization(PermissionCodes.BackupRun);

        group.MapPost("/backups/restore", async (
            [FromBody] RestoreBackupRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] BackupUseCase useCase,
            CancellationToken ct) =>
        {
            var ok = await useCase.RestoreAsync(request, current.UserId, ct);
            return Results.Ok(new { restored = ok });
        }).RequireAuthorization(PermissionCodes.BackupRestore);

        group.MapDelete("/backups/{backupId:long}", async (
            long backupId,
            [FromServices] ICurrentUser current,
            [FromServices] BackupUseCase useCase,
            CancellationToken ct) =>
        {
            var ok = await useCase.DeleteRegistryAsync(backupId, current.UserId, ct);
            return ok ? Results.NoContent() : Results.NotFound(new { error = "النسخة غير موجودة في السجل" });
        }).RequireAuthorization(PermissionCodes.BackupRun);

        // ── إحصاءات النظام (تغذي صفحة الصيانة قبل أي قرار) ──
        group.MapGet("/system/stats", async (
            [FromServices] SystemCleanupUseCase useCase,
            CancellationToken ct) =>
        {
            var stats = await useCase.GetStatsAsync(ct);
            return Results.Ok(stats);
        });

        // ── تنظيف النظام ──
        group.MapPost("/system/cleanup", async (
            [FromBody] SystemCleanupRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] SystemCleanupUseCase useCase,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, current.UserId, ct);
            return Results.Ok(result);
        }).RequireAuthorization(PermissionCodes.SystemCleanup);

        // ── الحذف الكامل (منطقة الخطر) ──
        group.MapPost("/system/wipe", async (
            [FromBody] WipeDataRequest request,
            [FromServices] ICurrentUser current,
            [FromServices] WipeDataUseCase useCase,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, current.UserId, ct);
            return Results.Ok(result);
        }).RequireAuthorization(PermissionCodes.SystemWipe);

        return app;
    }
}
