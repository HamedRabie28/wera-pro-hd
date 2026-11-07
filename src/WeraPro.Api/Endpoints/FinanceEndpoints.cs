using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Finance;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

namespace WeraPro.Api.Endpoints;

/// <summary>طلب تسوية فارق وردية بسند مرحّل موثق (§14.1).</summary>
public sealed record SettleVarianceRequest(long? VoucherId);

/// <summary>
/// نقاط نهاية G8 (§14/§13/§9.9): الخزائن والسندات والمصروفات ورأس المال وإغلاق الوردية —
/// صلاحيات ذرية خادمية، والطباعة بخصوصية الرواتب (§31/§35.2)، وA4 تفصيلي بإجراء صريح فقط.
/// </summary>
public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder app)
    {
        // ═══ المجموعة المالية (/api/v1/finance) ═════════════════════════════
        var group = app.MapGroup("/" + FinanceApi.Prefix)
            .WithTags("Finance-G8")
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── الخزائن والأدراج (§14.2) ──
        group.MapGet("/treasury/accounts", async (
            [FromQuery] bool? includeInactive,
            [FromServices] ManageTreasuryUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListAsync(includeInactive ?? false, ct);
            return Results.Ok(result.Value);
        }).RequireAuthorization(PermissionCodes.FinanceTreasuryView);

        group.MapPost("/treasury/accounts", async (
            [FromBody] CreateTreasuryAccountRequest request,
            [FromServices] ManageTreasuryUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(
                request.Code, request.NameAr, request.AccountKind, request.BranchId, request.StationId,
                http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{FinanceApi.Prefix}/treasury/accounts/{result.Value!.TreasuryAccountId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إنشاء حساب الخزينة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceTreasuryManage);

        group.MapGet("/treasury/transactions", async (
            [FromQuery] long? treasuryAccountId, [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] int? page, [FromQuery] int? pageSize,
            [FromServices] ManageTreasuryUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListTransactionsAsync(
                treasuryAccountId, fromUtc, toUtc, page ?? 1, pageSize ?? 50, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status400BadRequest, "تعذر عرض حركات الخزينة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceTreasuryView);

        // ── السندات: قبض/صرف/تحويل (§14.2) + التوزيع على الفواتير (§13) ──
        group.MapPost("/vouchers", async (
            [FromBody] CreateVoucherRequest request,
            [FromServices] ManageVouchersUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{FinanceApi.Prefix}/vouchers/{result.Value!.VoucherId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إنشاء السند", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceVoucherCreate);

        group.MapGet("/vouchers", async (
            [FromQuery] string? type, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
            [FromServices] ManageVouchersUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(type, status, page ?? 1, pageSize ?? 20, ct)))
            .RequireAuthorization(PermissionCodes.FinanceTreasuryView);

        group.MapGet("/vouchers/{voucherId:long}", async (
            long voucherId, [FromServices] ManageVouchersUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(voucherId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "السند غير موجود", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceTreasuryView);

        group.MapPost("/vouchers/{voucherId:long}/post", async (
            long voucherId, [FromServices] ManageVouchersUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(voucherId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل السند", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceVoucherPost);

        group.MapPost("/vouchers/{voucherId:long}/cancel", async (
            long voucherId, [FromBody] CancelRequest request,
            [FromServices] ManageVouchersUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(voucherId, request.ReasonAr, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { voucherId = result.Value })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء السند", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceVoucherCreate);

        group.MapGet("/customers/{customerId:long}/receivables", async (
            long customerId, [FromServices] ManageVouchersUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.GetReceivablesAsync(customerId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "تعذر عرض مديونية العميل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceVoucherCreate);

        // ── المصروفات (§14.4) ──
        group.MapPost("/expenses", async (
            [FromBody] CreateExpenseRequest request,
            [FromServices] ManageExpensesUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{FinanceApi.Prefix}/expenses/{result.Value!.ExpenseId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تسجيل المصروف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceExpenseCreate);

        group.MapGet("/expenses", async (
            [FromQuery] string? status, [FromQuery] long? shiftId, [FromQuery] int? page, [FromQuery] int? pageSize,
            [FromServices] ManageExpensesUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, shiftId, page ?? 1, pageSize ?? 20, ct)))
            .RequireAuthorization(PermissionCodes.FinanceTreasuryView);

        group.MapPost("/expenses/{expenseId:long}/approve", async (
            long expenseId, [FromBody] CancelRequest request,
            [FromServices] ManageExpensesUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.ApproveAsync(expenseId, request.ReasonAr, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر اعتماد المصروف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceExpenseApprove);

        group.MapPost("/expenses/{expenseId:long}/post", async (
            long expenseId, [FromServices] ManageExpensesUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(expenseId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل المصروف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceExpenseCreate);

        group.MapPost("/expenses/{expenseId:long}/cancel", async (
            long expenseId, [FromBody] CancelRequest request,
            [FromServices] ManageExpensesUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(expenseId, request.ReasonAr, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء المصروف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceExpenseApprove);

        group.MapGet("/expense-categories", async (
            [FromServices] ManageExpensesUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ListCategoriesAsync(ct)))
            .RequireAuthorization(PermissionCodes.FinanceExpenseCreate);

        group.MapPost("/expense-categories", async (
            [FromBody] CreateExpenseCategoryRequest request,
            [FromServices] ManageExpensesUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CreateCategoryAsync(request.Code, request.NameAr, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{FinanceApi.Prefix}/expense-categories/{result.Value!.CategoryId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إنشاء فئة المصروف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceTreasuryManage);

        // ── رأس المال والأرصدة الافتتاحية (§14.3/D-14) ──
        group.MapPost("/capital/events", async (
            [FromBody] CreateCapitalEventRequest request,
            [FromServices] ManageCapitalUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/{FinanceApi.Prefix}/capital/events/{result.Value!.CapitalId}", result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تسجيل حدث رأس المال", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceCapitalManage);

        group.MapGet("/capital/events", async (
            [FromQuery] string? eventType, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
            [FromServices] ManageCapitalUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(eventType, status, page ?? 1, pageSize ?? 20, ct)))
            .RequireAuthorization(PermissionCodes.FinanceCapitalManage);

        group.MapPost("/capital/events/{capitalId:long}/post", async (
            long capitalId, [FromServices] ManageCapitalUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(capitalId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل حدث رأس المال", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceCapitalManage);

        group.MapPost("/capital/events/{capitalId:long}/cancel", async (
            long capitalId, [FromBody] CancelRequest request,
            [FromServices] ManageCapitalUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(capitalId, request.ReasonAr, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { capitalId = result.Value })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء الحدث", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceCapitalManage);

        // ── طباعة مستندات G8 (§29.10/§31/§35.2 — خصوصية الرواتب) ──
        group.MapPost("/print", async (
            [FromBody] PrintFinanceDocumentRequest request,
            [FromServices] PrintFinanceDocumentsUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.PrintAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إنشاء مهمة الطباعة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.FinanceTreasuryView);

        // ═══ إغلاق الوردية ومراجعتها (/api/v1/pos/shifts — §14.1 استكمال G7) ═══
        var pos = app.MapGroup("/" + SalesApi.Prefix + "/shifts")
            .WithTags("Shifts-G8")
            .RequireRateLimiting(RateLimitPolicies.Api);

        pos.MapPost("/{shiftId:long}/close", async (
            long shiftId, [FromBody] CloseShiftRequest request,
            [FromServices] CloseShiftUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.CloseAsync(shiftId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http,
                    result.Error!.Code == "SHIFT_VARIANCE_APPROVAL_REQUIRED" ? StatusCodes.Status403Forbidden : StatusCodes.Status409Conflict,
                    "تعذر إغلاق الوردية", result.Error.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesShiftClose);

        pos.MapGet("/{shiftId:long}/reconciliation", async (
            long shiftId, [FromServices] CloseShiftUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.GetReconciliationAsync(shiftId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "تعذر عرض مطابقة الوردية", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesShiftClose);

        pos.MapGet("/{shiftId:long}/cash-movements", async (
            long shiftId, [FromServices] CloseShiftUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.GetCashMovementsAsync(shiftId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "تعذر عرض حركات نقد الوردية", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesShiftClose);

        pos.MapGet("/{shiftId:long}/drawer-events", async (
            long shiftId, [FromServices] CloseShiftUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListDrawerEventsAsync(shiftId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "تعذر عرض أحداث الدرج", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesShiftClose);

        pos.MapGet("/{shiftId:long}/cash-counts", async (
            long shiftId, [FromServices] CloseShiftUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ListCashCountsAsync(shiftId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(null!, StatusCodes.Status404NotFound, "تعذر عرض عدّات النقدية", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesShiftClose);

        pos.MapGet("/", async (
            [FromQuery] long? stationId, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
            [FromServices] CloseShiftUseCase useCase, CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(stationId, status, page ?? 1, pageSize ?? 20, ct)))
            .RequireAuthorization(PermissionCodes.SalesShiftClose);

        // ── تسوية الفارق بسند موثق (بوابة القبول: الفارق يبقى ظاهراً حتى التسوية) ──
        pos.MapPost("/{shiftId:long}/settle-variance", async (
            long shiftId, [FromBody] SettleVarianceRequest request,
            [FromServices] CloseShiftUseCase useCase,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await useCase.SettleVarianceAsync(shiftId, request.VoucherId ?? 0, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { shiftId = result.Value, settled = true })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تسوية الفارق", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SalesShiftApproveVariance);

        return app;
    }

    private static IResult Problem(
        HttpContext http, int status, string title, string detail, string? code = null) =>
        Results.Json(
            new ApiProblem(
                Type: "https://wera.example/errors/" + (code ?? "finance").ToLowerInvariant(),
                Title: title, Status: status, Detail: detail,
                CorrelationId: http?.GetCorrelationId(), OccurredAtUtc: DateTimeOffset.UtcNow,
                Code: code),
            statusCode: status, contentType: "application/problem+json");
}
