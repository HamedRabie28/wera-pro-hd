namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Inventory;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// نقاط نهاية المخزون (G6 — §12.1/§12.2): المخازن والأرصدة وكرت الحركة
/// والتقييم والحجز والجرد والتسويات والتحويلات والنواقص والدفعات + الاعتمادات (D-03).
/// الصلاحيات ذرية خادمية (§18): القراءة لكل الأدوار (V017)، وكل كتابة بصلاحيتها.
/// </summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/inventory").WithTags("Inventory")
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── المخازن (PAGE-28) ───────────────────────────────────────────
        group.MapGet("/warehouses", async (
            bool? includeInactive,
            [FromServices] ManageWarehousesUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(includeInactive ?? false, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapPost("/warehouses", async (
            [FromBody] CreateWarehouseRequest request,
            [FromServices] ManageWarehousesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/inventory/warehouses/{result.Value!.WarehouseId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء المخزن", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryWarehouseManage);

        group.MapPost("/warehouses/{warehouseId:long}/status", async (
            long warehouseId,
            [FromBody] SetWarehouseStatusRequest request,
            [FromServices] ManageWarehousesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.SetStatusAsync(warehouseId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { warehouseId, request.IsActive })
                : Problem(http, StatusCodes.Status404NotFound, "تعذر تغيير حالة المخزن", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryWarehouseManage);

        // ── الأرصدة والتقييم (PAGE-18 · §16.2) ────────────────────────────
        group.MapGet("/balances", async (
            string? query, long? warehouseId, bool? belowReorder, int? page, int? pageSize,
            [FromServices] GetStockBalancesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.SearchAsync(query, warehouseId, belowReorder, page, pageSize, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر تحميل الأرصدة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapGet("/valuation", async (
            long? warehouseId,
            [FromServices] GetInventoryReportsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ValuationAsync(warehouseId, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        // ── كرت الحركة (PAGE-19) ─────────────────────────────────────────
        group.MapGet("/skus/{skuId:long}/movements", async (
            long skuId, long? warehouseId, int? limit,
            [FromServices] GetInventoryReportsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.MovementCardAsync(skuId, warehouseId, limit, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Movements)
                : Problem(http, StatusCodes.Status404NotFound, "تعذر تحميل كرت الحركة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryRead);

        // ── الحجز والمتاح (§12.1) ────────────────────────────────────────
        group.MapGet("/reservations", async (
            bool? activeOnly,
            [FromServices] ManageReservationsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(activeOnly ?? true, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapPost("/reservations", async (
            [FromBody] ReserveStockRequest request,
            [FromServices] ManageReservationsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ReserveAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/inventory/reservations/{result.Value!.ReservationId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر الحجز", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryReservationManage);

        group.MapPost("/reservations/{reservationId:long}/release", async (
            long reservationId,
            [FromBody] ReleaseReservationRequest? request,
            [FromServices] ManageReservationsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ReleaseAsync(reservationId, request ?? new ReleaseReservationRequest(null), http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { reservationId, released = true })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تحرير الحجز", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryReservationManage);

        // ── الجرد والتسويات (PAGE-20) ────────────────────────────────────
        group.MapGet("/stocktakes", async (
            int? page, int? pageSize,
            [FromServices] ManageStocktakesUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapGet("/stocktakes/{countId:long}", async (
            long countId,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(countId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "جلسة الجرد غير موجودة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapPost("/stocktakes", async (
            [FromBody] OpenStocktakeRequest request,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.OpenAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/inventory/stocktakes/{result.Value!.CountId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر فتح الجرد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryStocktakeRun);

        group.MapPost("/stocktakes/{countId:long}/lines", async (
            long countId,
            [FromBody] RecordCountLinesRequest request,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.RecordAsync(countId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر تسجيل العد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryStocktakeRun);

        group.MapPost("/stocktakes/{countId:long}/close", async (
            long countId,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CloseAsync(countId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إغلاق الجرد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryStocktakeRun);

        group.MapPost("/stocktakes/{countId:long}/reconcile", async (
            long countId,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ReconcileAsync(countId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر مطابقة الجرد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryStocktakeRun);

        group.MapPost("/stocktakes/{countId:long}/approve", async (
            long countId,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ApproveAsync(countId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر اعتماد التسوية", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryStocktakeApprove);

        group.MapPost("/stocktakes/{countId:long}/cancel", async (
            long countId,
            [FromServices] ManageStocktakesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(countId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { countId, cancelled = true })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء الجرد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryStocktakeRun);

        // ── التسوية السريعة/الهالك/التلف (PAGE-23) ──────────────────────
        group.MapPost("/adjustments", async (
            [FromBody] PostAdjustmentRequest request,
            [FromServices] PostQuickAdjustmentUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/inventory/adjustments/{result.Value!.AdjustmentId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل التسوية", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryMovementPost);

        // ── التحويلات (PAGE-21) ──────────────────────────────────────────
        group.MapGet("/transfers", async (
            string? status, int? page, int? pageSize,
            [FromServices] ManageTransfersUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapGet("/transfers/{transferId:long}", async (
            long transferId,
            [FromServices] ManageTransfersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(transferId, ct);
            return result is null
                ? Problem(http, StatusCodes.Status404NotFound, "التحويل غير موجود", "التحويل غير موجود", "TRANSFER_NOT_FOUND")
                : Results.Ok(result);
        }).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapPost("/transfers", async (
            [FromBody] CreateTransferRequest request,
            [FromServices] ManageTransfersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/inventory/transfers/{result.Value!.TransferId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء التحويل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryMovementPost);

        group.MapPost("/transfers/{transferId:long}/send", async (
            long transferId,
            [FromBody] SendTransferRequest request,
            [FromServices] ManageTransfersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.SendAsync(transferId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إرسال التحويل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryMovementPost);

        group.MapPost("/transfers/{transferId:long}/receive", async (
            long transferId,
            [FromServices] ManageTransfersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ReceiveAsync(transferId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر استلام التحويل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryMovementPost);

        group.MapPost("/transfers/{transferId:long}/cancel", async (
            long transferId,
            [FromServices] ManageTransfersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(transferId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { transferId, cancelled = true })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء التحويل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.InventoryMovementPost);

        // ── النواقص والدفعات (PAGE-22/23) ────────────────────────────────
        group.MapGet("/reorder", async (
            long? warehouseId,
            [FromServices] GetInventoryAdvisoriesUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ReorderAsync(warehouseId, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        group.MapGet("/lots", async (
            int? expiringInDays,
            [FromServices] GetInventoryAdvisoriesUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.LotsAsync(expiringInDays, ct))
        ).RequireAuthorization(PermissionCodes.InventoryRead);

        MapApprovals(app);
        return app;
    }

    /// <summary>الاعتمادات (D-03 ب) — تحت /approvals مباشرة (قسم أمني مشترك).</summary>
    private static void MapApprovals(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/approvals").WithTags("Approvals")
            .RequireRateLimiting(RateLimitPolicies.Api);

        group.MapGet("/", async (
            string? status,
            [FromServices] ManageApprovalsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, ct))
        ).RequireAuthorization(PermissionCodes.SecurityApprovalDecide);

        group.MapPost("/", async (
            [FromBody] CreateApprovalRequest request,
            [FromServices] ManageApprovalsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/approvals/{result.Value!.ApprovalId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء طلب الاعتماد", result.Error!.MessageAr, result.Error.Code);
        });

        group.MapPost("/{approvalId:long}/decision", async (
            long approvalId,
            [FromBody] DecideApprovalRequest request,
            [FromServices] ManageApprovalsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.DecideAsync(approvalId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر البت في الاعتماد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SecurityApprovalDecide);
    }

    private static IResult Problem(
        HttpContext http, int status, string title, string detail, string? code = null) =>
        Results.Json(
            new ApiProblem(
                Type: "https://wera.example/errors/" + (code ?? "inventory").ToLowerInvariant(),
                Title: title,
                Status: status,
                Detail: detail,
                CorrelationId: http.GetCorrelationId(),
                OccurredAtUtc: DateTimeOffset.UtcNow),
            statusCode: status,
            contentType: "application/problem+json");
}
