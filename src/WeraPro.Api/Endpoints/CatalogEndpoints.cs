namespace WeraPro.Api.Endpoints;

using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using WeraPro.Api.Middleware;
using WeraPro.Application.Catalog;
using WeraPro.Application.Validation;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// نقاط نهاية الكتالوج (G4 — §4): إنشاء متعدد الأقسام ذري · بحث نصي محدود
/// بترقيم خادمي · مطابقة دقيقة بالباركود/SKU/PLU · الوحدات والتحويلات ·
/// ترحيل حركة مخزون موثقة (إثبات التحويل) · حالة المنتج.
/// الصلاحيات ذرية خادمية (§18): القراءة لكل الأدوار (V015)، والكتابة
/// بصلاحياتها من V009، وترحيل الحركات لأمين المخزن/الإدارة (V015).
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + ApiRoutes.Prefix + "/catalog").WithTags("Catalog")
            .RequireRateLimiting(RateLimitPolicies.Api);

        // ── الوحدات والتحويلات (تغذية المحرر) ─────────────────────────────
        group.MapGet("/units", async (
            [FromServices] ListUnitsUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status500InternalServerError, "تعذر تحميل الوحدات", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogProductRead);

        // ── مرجع المحرر: فئات/علامات/قوائم سعرية ───────────────────────
        group.MapGet("/reference", async (
            [FromServices] GetCatalogReferenceUseCase useCase,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http: null!, StatusCodes.Status500InternalServerError, "تعذر تحميل المرجع", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogProductRead);

        // ── بحث نصي محدود (جدول الكتالوج المتقدم) ─────────────────────
        group.MapGet("/products", async (
            string? query,
            string? saleMode,
            string? status,
            int? page,
            int? pageSize,
            [FromServices] SearchCatalogUseCase search,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await search.SearchAsync(query, saleMode, status, page ?? 1, pageSize ?? 25, ct);
            if (!result.IsSuccess)
            {
                return Problem(http, StatusCodes.Status400BadRequest, "طلب بحث غير صالح", result.Error!.MessageAr);
            }
            return Results.Ok(result.Value);
        }).RequireAuthorization(PermissionCodes.CatalogProductRead);

        // ── تفاصيل منتج كامل ─────────────────────────────────────────────
        group.MapGet("/products/{productId:long}", async (
            long productId,
            [FromServices] GetProductUseCase useCase,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(productId, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "المنتج غير موجود", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogProductRead).WithName("GetProductById");

        // ── مطابقة دقيقة: باركود/SKU/PLU (Exact Match §3.1 — ماسح POS) ────
        group.MapGet("/lookup", async (
            string code,
            [FromServices] SearchCatalogUseCase search,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await search.LookupAsync(code, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Problem(http, StatusCodes.Status404NotFound, "لا مطابقة", result.Error!.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogProductRead);

        // ── إنشاء منتج متعدد الأقسام — معاملة واحدة (لا حفظ جزئي) ────────
        group.MapPost("/products", async (
            [FromBody] CreateProductRequest request,
            [FromServices] CreateProductUseCase create,
            [FromServices] IValidator<CreateProductRequest> validator,
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

            var result = await create.ExecuteAsync(request, http.GetCorrelationId(), ct);
            if (result.IsSuccess)
            {
                return Results.CreatedAtRoute(
                    "GetProductById",
                    routeValues: new { productId = result.Value!.ProductId },
                    value: result.Value);
            }

            var status = result.Error!.Code switch
            {
                "PRODUCT_CODE_DUPLICATE" or "SKU_CODE_DUPLICATE" or "BARCODE_DUPLICATE" or "PLU_DUPLICATE"
                    => StatusCodes.Status409Conflict,
                "CODE_RACE_DUPLICATE" => StatusCodes.Status409Conflict,
                "CATEGORY_NOT_FOUND" or "BRAND_NOT_FOUND" or "PRICE_LIST_NOT_FOUND" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest,
            };
            return Problem(http, status, "تعذر إنشاء المنتج", result.Error.MessageAr, result.Error.Code);
        }).RequireAuthorization(PermissionCodes.CatalogProductCreate);

        // ── حالة المنتج (تفعيل/تعطيل/إيقاف دائم — لا حذف تاريخي) ──────────
        group.MapPost("/products/{productId:long}/status", async (
            long productId,
            [FromBody] SetProductStatusRequest request,
            [FromServices] SetProductStatusUseCase useCase,
            [FromServices] IValidator<SetProductStatusRequest> validator,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }
            var result = await useCase.ExecuteAsync(productId, request, http.GetCorrelationId(), ct);
            return result.IsSuccess
                ? Results.Ok(new { productId, status = request.Status })
                : Problem(
                    http,
                    result.Error!.Code == "PRODUCT_NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                    "تعذر تغيير حالة المنتج",
                    result.Error.MessageAr);
        }).RequireAuthorization(PermissionCodes.CatalogProductDeactivate);

        // ── ترحيل حركة مخزون موثقة (إثبات تحويل المخزون لكل صيغة) ─────────
        group.MapPost("/skus/{skuId:long}/stock-movements", async (
            long skuId,
            [FromBody] PostStockMovementRequest request,
            [FromServices] PostStockMovementUseCase useCase,
            [FromServices] IValidator<PostStockMovementRequest> validator,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }
            try
            {
                var result = await useCase.ExecuteAsync(skuId, request, http.GetCorrelationId(), ct);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/catalog/skus/{skuId}/stock-movements/{result.Value!.MovementId}", result.Value)
                    : Problem(
                        http,
                        result.Error!.Code == "SKU_NOT_FOUND" || result.Error.Code == "PACKAGE_CONTENT_NOT_FOUND"
                            ? StatusCodes.Status404NotFound
                            : StatusCodes.Status400BadRequest,
                        "تعذر ترحيل الحركة",
                        result.Error.MessageAr,
                        result.Error.Code);
            }
            catch (WeraPro.Application.Catalog.StockPolicyViolationException ex)
            {
                // سياسة القاعدة الحاسمة (D-03 وقيود الدفتر) — رسالة مفهومة بلا كشف داخلي
                return Problem(http, StatusCodes.Status409Conflict, "الحركة مرفوضة بسياسة المخزون", ex.UserMessageAr, "stock_policy_violation");
            }
        }).RequireAuthorization(PermissionCodes.InventoryMovementPost);

        return app;
    }

    private static IResult Problem(
        HttpContext http, int status, string title, string detail, string? code = null) =>
        Results.Json(
            new ApiProblem(
                Type: "https://wera.example/errors/" + (code ?? "catalog").ToLowerInvariant(),
                Title: title,
                Status: status,
                Detail: detail,
                CorrelationId: http.GetCorrelationId(),
                OccurredAtUtc: DateTimeOffset.UtcNow),
            statusCode: status,
            contentType: "application/problem+json");
}
