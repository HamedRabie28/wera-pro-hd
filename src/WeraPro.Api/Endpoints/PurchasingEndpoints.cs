namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Purchasing;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// نقاط نهاية المشتريات (G6 — §12.3): الموردون والسلة/أوامر الشراء والاستلامات
/// وفواتير الموردين والسداد والمرتجعات + حسابات الخزينة (قراءة — G8 تديرها).
/// الاستلام هو الذي يزيد المخزون (لا الفاتورة) والسداد أثر خزينة واحد.
/// </summary>
public static class PurchasingEndpoints
{
    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/purchasing").WithTags("Purchasing")
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── الموردون (PAGE-31/32) ────────────────────────────────────────
        group.MapGet("/suppliers", async (
            string? query, int? page, int? pageSize,
            [FromServices] ManageSuppliersUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(query, page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.PartiesRead);

        group.MapGet("/suppliers/{supplierId:long}", async (
            long supplierId,
            [FromServices] ManageSuppliersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(supplierId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "المورد غير موجود", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PartiesRead);

        group.MapGet("/suppliers/{supplierId:long}/statement", async (
            long supplierId, DateOnly? from, DateOnly? to, string? docType,
            [FromServices] ManageSuppliersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.StatementAsync(supplierId, from, to, docType, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "تعذر تحميل الكشف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PartiesRead);

        group.MapPost("/suppliers", async (
            [FromBody] CreateSupplierRequest request,
            [FromServices] ManageSuppliersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/purchasing/suppliers/{result.Value!.SupplierId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء المورد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SuppliersManage);

        group.MapPost("/suppliers/{supplierId:long}/status", async (
            long supplierId,
            [FromBody] SetPartyStatusRequest request,
            [FromServices] ManageSuppliersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.SetStatusAsync(supplierId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { supplierId, request.AccountStatus })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تغيير حالة المورد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.SuppliersManage);

        // ── السلة وأوامر الشراء (PAGE-24) ───────────────────────────────
        group.MapGet("/purchase-orders", async (
            string? status, int? page, int? pageSize,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapGet("/purchase-orders/{poId:long}", async (
            long poId,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(poId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "أمر الشراء غير موجود", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapPost("/purchase-orders", async (
            [FromBody] CreatePurchaseOrderRequest request,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/purchasing/purchase-orders/{result.Value!.PoId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء السلة/الأمر", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingManage);

        group.MapPut("/purchase-orders/{poId:long}/lines", async (
            long poId,
            [FromBody] ReplacePurchaseOrderLinesRequest request,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ReplaceLinesAsync(poId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تعديل أسطر السلة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingManage);

        group.MapPost("/purchase-orders/{poId:long}/submit", async (
            long poId,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.SubmitAsync(poId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إصدار الأمر", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingManage);

        group.MapPost("/purchase-orders/{poId:long}/close", async (
            long poId,
            [FromBody] ClosePoRequest request,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CloseAsync(poId, request.ReasonAr ?? string.Empty, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إقفال الأمر", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingManage);

        group.MapPost("/purchase-orders/{poId:long}/cancel", async (
            long poId,
            [FromServices] ManagePurchaseOrdersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(poId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء الأمر", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingManage);

        // ── الاستلامات (PAGE-25) ─────────────────────────────────────────
        group.MapGet("/purchase-receipts", async (
            string? status, int? page, int? pageSize,
            [FromServices] ManageReceiptsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapGet("/purchase-receipts/{receiptId:long}", async (
            long receiptId,
            [FromServices] ManageReceiptsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(receiptId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "الاستلام غير موجود", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapPost("/purchase-receipts", async (
            [FromBody] CreateReceiptRequest request,
            [FromServices] ManageReceiptsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            if (!result.IsSuccess)
            {
                return Problem(http,
                    result.Error!.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                    "تعذر إنشاء الاستلام", result.Error.MessageAr, result.Error.Code);
            }
            return Results.Created($"/api/v1/purchasing/purchase-receipts/{result.Value!.ReceiptId}", result.Value);
        }).RequireAuthorization(PermissionCodes.PurchasingReceive);

        group.MapPost("/purchase-receipts/{receiptId:long}/post", async (
            long receiptId,
            [FromBody] PostDocumentRequest? request,
            [FromServices] ManageReceiptsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(receiptId, request ?? new PostDocumentRequest(null), http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل الاستلام", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingReceive);

        group.MapPost("/purchase-receipts/{receiptId:long}/cancel", async (
            long receiptId,
            [FromServices] ManageReceiptsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(receiptId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء الاستلام", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingReceive);

        // ── فواتير الموردين (PAGE-26) ───────────────────────────────────
        group.MapGet("/supplier-invoices", async (
            string? status, long? supplierId, int? page, int? pageSize,
            [FromServices] ManageSupplierInvoicesUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, supplierId, page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapGet("/supplier-invoices/{invoiceId:long}", async (
            long invoiceId,
            [FromServices] ManageSupplierInvoicesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(invoiceId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "الفاتورة غير موجودة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapPost("/supplier-invoices", async (
            [FromBody] CreateSupplierInvoiceRequest request,
            [FromServices] ManageSupplierInvoicesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            if (!result.IsSuccess)
            {
                return Problem(http,
                    result.Error!.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                    "تعذر إنشاء الفاتورة", result.Error.MessageAr, result.Error.Code);
            }
            return Results.Created($"/api/v1/purchasing/supplier-invoices/{result.Value!.InvoiceId}", result.Value);
        }).RequireAuthorization(PermissionCodes.PurchasingInvoiceManage);

        group.MapPost("/supplier-invoices/{invoiceId:long}/post", async (
            long invoiceId,
            [FromBody] PostDocumentRequest? request,
            [FromServices] ManageSupplierInvoicesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(invoiceId, request ?? new PostDocumentRequest(null), http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل الفاتورة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingInvoiceManage);

        group.MapPost("/supplier-invoices/{invoiceId:long}/cancel", async (
            long invoiceId,
            [FromServices] ManageSupplierInvoicesUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(invoiceId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء الفاتورة", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingInvoiceManage);

        // ── السداد (PAGE-26) ─────────────────────────────────────────────
        group.MapGet("/supplier-payments", async (
            long? supplierId, string? status,
            [FromServices] ManageSupplierPaymentsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(supplierId, status, ct))
        ).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapPost("/supplier-payments", async (
            [FromBody] CreateSupplierPaymentRequest request,
            [FromServices] ManageSupplierPaymentsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/purchasing/supplier-payments/{result.Value!.PaymentId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء السداد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingPaymentPost);

        group.MapPost("/supplier-payments/{paymentId:long}/post", async (
            long paymentId,
            [FromBody] PostDocumentRequest? request,
            [FromServices] ManageSupplierPaymentsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(paymentId, request ?? new PostDocumentRequest(null), http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل السداد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingPaymentPost);

        group.MapPost("/supplier-payments/{paymentId:long}/cancel", async (
            long paymentId,
            [FromServices] ManageSupplierPaymentsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(paymentId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء السداد", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingPaymentPost);

        // ── مرتجعات الموردين (PAGE-27) ───────────────────────────────────
        group.MapGet("/supplier-returns", async (
            string? status, long? supplierId,
            [FromServices] ManageSupplierReturnsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(status, supplierId, ct))
        ).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapGet("/supplier-returns/{returnId:long}", async (
            long returnId,
            [FromServices] ManageSupplierReturnsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(returnId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "المرتجع غير موجود", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingRead);

        group.MapPost("/supplier-returns", async (
            [FromBody] CreateSupplierReturnRequest request,
            [FromServices] ManageSupplierReturnsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/purchasing/supplier-returns/{result.Value!.ReturnId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء المرتجع", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingReturnPost);

        group.MapPost("/supplier-returns/{returnId:long}/post", async (
            long returnId,
            [FromBody] PostDocumentRequest? request,
            [FromServices] ManageSupplierReturnsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.PostAsync(returnId, request ?? new PostDocumentRequest(null), http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر ترحيل المرتجع", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingReturnPost);

        group.MapPost("/supplier-returns/{returnId:long}/cancel", async (
            long returnId,
            [FromServices] ManageSupplierReturnsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CancelAsync(returnId, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status409Conflict, "تعذر إلغاء المرتجع", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PurchasingReturnPost);

        // ── حسابات الخزينة (قراءة — الإنشاء والإدارة في G8) ─────────────
        group.MapGet("/treasury-accounts", async (
            [FromServices] ManageSupplierPaymentsUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListTreasuryAsync(ct))
        ).RequireAuthorization(PermissionCodes.FinanceTreasuryRead);

        MapParties(app);
        return app;
    }

    /// <summary>العملاء (PAGE-29/30) — تحت /parties مباشرة.</summary>
    private static void MapParties(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/parties").WithTags("Parties")
            .RequireRateLimiting(RateLimitPolicies.Api);

        group.MapGet("/customers", async (
            string? query, int? page, int? pageSize,
            [FromServices] ManageCustomersUseCase useCase,
            CancellationToken ct) =>
            Results.Ok(await useCase.ListAsync(query, page, pageSize, ct))
        ).RequireAuthorization(PermissionCodes.PartiesRead);

        group.MapGet("/customers/{customerId:long}", async (
            long customerId,
            [FromServices] ManageCustomersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.GetAsync(customerId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "العميل غير موجود", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PartiesRead);

        group.MapGet("/customers/{customerId:long}/statement", async (
            long customerId, DateOnly? from, DateOnly? to, string? docType,
            [FromServices] ManageCustomersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.StatementAsync(customerId, from, to, docType, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "تعذر تحميل الكشف", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.PartiesRead);

        group.MapPost("/customers", async (
            [FromBody] CreateCustomerRequest request,
            [FromServices] ManageCustomersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.CreateAsync(request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/parties/customers/{result.Value!.CustomerId}", result.Value)
                : Problem(http, StatusCodes.Status400BadRequest, "تعذر إنشاء العميل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CustomersManage);

        group.MapPost("/customers/{customerId:long}/status", async (
            long customerId,
            [FromBody] SetPartyStatusRequest request,
            [FromServices] ManageCustomersUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.SetStatusAsync(customerId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { customerId, request.AccountStatus })
                : Problem(http, StatusCodes.Status409Conflict, "تعذر تغيير حالة العميل", result.Error!.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CustomersManage);
    }

    private static IResult Problem(
        HttpContext http, int status, string title, string detail, string? code = null) =>
        Results.Json(
            new ApiProblem(
                Type: "https://wera.example/errors/" + (code ?? "purchasing").ToLowerInvariant(),
                Title: title,
                Status: status,
                Detail: detail,
                CorrelationId: http.GetCorrelationId(),
                OccurredAtUtc: DateTimeOffset.UtcNow),
            statusCode: status,
            contentType: "application/problem+json");
}

/// <summary>طلب إقفال أمر شراء قبل استلامه كاملاً — سبب موثق إلزامي.</summary>
public sealed record ClosePoRequest(string? ReasonAr);
