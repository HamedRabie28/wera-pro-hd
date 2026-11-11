namespace WeraPro.Api.Endpoints;

using Microsoft.AspNetCore.Mvc;
using WeraPro.Application.Analytics;
using WeraPro.Contracts.V1;

/// <summary>نقاط نهاية التحليلات — لوحة الرئيسية HOME-01 (§7.5 · بوابة G9).</summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/" + AnalyticsApi.Prefix).WithTags("Analytics");

        // لوحة الرئيسية: مصادقة فقط — الكتل الإدارية تُفلتر داخل حالة الاستخدام
        // على الخادم بحسب صلاحيات المستخدم (لا إخفاء واجهة بلا حماية §0.1 ق10).
        group.MapGet("/dashboard", async (
            [AsParameters] DashboardQuery query,
            [FromServices] DashboardUseCase useCase,
            CancellationToken ct) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var from = query.From is { } f ? f : today;
            var to = query.To is { } t ? t : today;
            if (to < from)
            {
                return Results.BadRequest(new { title = "فترة غير صالحة", detail = "نهاية الفترة قبل بدايتها" });
            }
            var result = await useCase.ExecuteAsync(from, to, query.BranchId, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        return app;
    }
}

/// <summary>معاملات استعلام اللوحة: from/to بتاريخ محلي (أفريقيا/القاهرة) + فرع اختياري.</summary>
public sealed record DashboardQuery(
    [FromQuery(Name = "from")] DateOnly? From,
    [FromQuery(Name = "to")] DateOnly? To,
    [FromQuery(Name = "branchId")] long? BranchId);
