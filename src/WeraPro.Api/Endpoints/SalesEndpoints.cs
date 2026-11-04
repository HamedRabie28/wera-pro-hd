using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Sales;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

namespace WeraPro.Api.Endpoints;

/// <summary>
/// نقاط نهاية نقطة البيع (G7 · §9): نافذة الكاشير كاملة من الالتقاط إلى الإيصال —
/// كل الأوامر عبر حالات استخدام وAPI (§9.0 ق10)، والصلاحيات ذرية خادمية،
/// وPOS لا يعرض أي قائمة إدارية (بوابة القبول).
/// </summary>
public static class SalesEndpoints
{
    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + SalesApi.Prefix)
            .WithTags("POS")
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── سياق المحطة (§9.1A — شريط الجلسة عند فتح POS) ──
        group.MapGet("/context", async (
            [FromServices] GetPosContextUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status400BadRequest, "تعذر قراءة سياق المحطة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        // ── الالتقاط والبحث (§9.2) ──
        group.MapPost("/lookup", async (
            [FromBody] PosLookupRequest request,
            [FromServices] LookupPosItemUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request.Code, ct);
            return Results.Ok(result.Value);
        }).RequireAuthorization(PermissionCodes.PosUse);

        group.MapGet("/search", async (
            [FromQuery] string? query, [FromQuery] int page, [FromQuery] int pageSize,
            [FromServices] SearchPosItemsUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(query, page, pageSize == 0 ? 20 : pageSize, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status400BadRequest, "تعذر البحث", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        // ── معاينة التسعير الخادمية (§9.0 ق10 — المجاميع من الخادم فقط) ──
        group.MapPost("/preview", async (
            [FromBody] PosPreviewRequest request,
            [FromServices] PreviewSaleUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر حساب معاينة الفاتورة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        // ── نافذة الوزن خادمياً (§9.3 — الطرق الست) ──
        group.MapPost("/weight/preview", async (
            [FromBody] WeightPreviewRequest request,
            [FromServices] WeightPreviewUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Ok(result.Value); // الرفض الموثق يعود بنفس العقد (Accepted=false + رمز)
        }).RequireAuthorization(PermissionCodes.PosUse);

        // ── إتمام البيع (§9.7 — Idempotency §9.5/§15.4) ──
        group.MapPost("/sales", async (
            [FromBody] PostSaleRequest request,
            [FromServices] PostSaleUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{SalesApi.Prefix}/sales/{result.Value!.InvoiceId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل البيع", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        group.MapGet("/sales/{invoiceId:long}", async (
            long invoiceId, [FromServices] GetSaleUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(invoiceId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "الفاتورة غير موجودة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        group.MapGet("/sales", async (
            [FromQuery] int limit, [FromQuery] long? shiftId,
            [FromServices] GetSaleUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListRecentAsync(limit == 0 ? 10 : limit, shiftId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status400BadRequest, "تعذر عرض الفواتير", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        group.MapPost("/sales/{invoiceId:long}/void", async (
            long invoiceId, [FromBody] VoidSaleRequest request,
            [FromServices] VoidSaleUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(invoiceId, request.ReasonAr ?? string.Empty, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { invoiceId = result.Value })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء الفاتورة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        // ── السلال المعلقة (§9.5) ──
        group.MapPost("/carts/hold", async (
            [FromBody] HoldCartRequest request,
            [FromServices] ManageHeldCartsUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.HoldAsync(request.CartJson, request.NoteAr, request.CustomerId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{SalesApi.Prefix}/carts/{result.Value}", new { cartId = result.Value })
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر تعليق السلة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesHeldCartUse);

        group.MapGet("/carts/held", async (
            [FromServices] ManageHeldCartsUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListAsync(true, ct);
            return Results.Ok(result.Value);
        }).RequireAuthorization(PermissionCodes.SalesHeldCartUse);

        group.MapPost("/carts/{cartId:long}/restore", async (
            long cartId, [FromServices] ManageHeldCartsUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.RestoreAsync(cartId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر استعادة السلة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesHeldCartUse);

        // ── المرتجعات والاستبدال (§9.8 · D-04 — اعتماد مشرف إلزامي) ──
        group.MapPost("/refunds", async (
            [FromBody] PostRefundRequest request,
            [FromServices] PostRefundUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{SalesApi.Prefix}/refunds/{result.Value!.RefundId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل المرتجع", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesReturnApprove);

        // ── الورديات (§9.9 — الفتح والملخص؛ الإغلاق التام في G8) ──
        group.MapPost("/shifts", async (
            [FromBody] OpenShiftRequest request,
            [FromServices] ManageShiftsUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.OpenAsync(request.OpeningFloat, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{SalesApi.Prefix}/shifts/{result.Value!.ShiftId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر فتح الوردية", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        group.MapGet("/shifts/current", async (
            [FromServices] ManageShiftsUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.GetCurrentAsync(ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Ok(new { open = false, result.Error?.MessageAr });
        }).RequireAuthorization(PermissionCodes.PosUse);

        group.MapGet("/shifts/{shiftId:long}/summary", async (
            long shiftId, [FromServices] ManageShiftsUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.GetSummaryAsync(shiftId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "الوردية غير موجودة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PosUse);

        // ── درج النقد (§9.9 — فتح بصلاحية وسبب وأزمنة صادقة) ──
        group.MapPost("/drawer/open", async (
            [FromBody] DrawerOpenRequest request,
            [FromServices] OpenDrawerUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(request.ReasonAr ?? string.Empty, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر تسجيل فتح الدرج", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesDrawerOpen);

        // ── الطباعة (§29.4/§29.9 — مهمة مستقلة، فشلها لا يعيد البيع) ──
        group.MapPost("/sales/{invoiceId:long}/print", async (
            long invoiceId, [FromBody] PrintReceiptRequest request,
            [FromServices] ManageReceiptPrintingUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var copyType = string.IsNullOrWhiteSpace(request.CopyType) ? "CUSTOMER" : request.CopyType;
            var result = await useCase.PrintAsync(invoiceId, copyType, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إنشاء مهمة الإيصال", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PrintingReprint);

        group.MapGet("/sales/{invoiceId:long}/print-jobs", async (
            long invoiceId, [FromServices] ManageReceiptPrintingUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListAsync(invoiceId, ct);
            return Results.Ok(result.Value);
        }).RequireAuthorization(PermissionCodes.PosUse);

        return app;
    }

    private static IResult Problem(
        HttpContext http, int status, string title, string detail, string? code = null) =>
        Results.Json(
            new ApiProblem(
                Type: "https://wera.example/errors/" + (code ?? "pos").ToLowerInvariant(),
                Title: title, Status: status, Detail: detail,
                CorrelationId: http?.GetCorrelationId(), OccurredAtUtc: DateTimeOffset.UtcNow,
                Code: code),
            statusCode: status, contentType: "application/problem+json");
}

/// <summary>طلب إلغاء مسودة الفاتورة (§9.2 — بسبب موثق).</summary>
public sealed record VoidSaleRequest(string? ReasonAr);
