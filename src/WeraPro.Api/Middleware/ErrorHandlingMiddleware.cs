namespace WeraPro.Api.Middleware;

using System.Text.Json;
using WeraPro.Contracts.V1;

/// <summary>
/// معالجة الأخطاء الموحدة (RFC 7807): كل استثناء غير معالج → 500 ProblemDetails
/// بمعرف ترابط وبلا كشف مسارات أو أسرار. أخطاء التحقق والصلاحيات تُعالج في
/// مساراتها المخصصة (400/401/403/429) — هذه الطبقة للحوادث غير المتوقعة فقط.
/// </summary>
public sealed partial class ErrorHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.GetCorrelationId();
            // أخطاء قواعد العمل (InvalidOperationException برسالة عربية من حالات الاستخدام)
            // هي أخطاء مدخلات متوقعة → 400 برسالتها، لا 500 (إصلاح 2026-11-11)
            if (ex is InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/problem+json";
                var badRequest = new ApiProblem(
                    Type: "https://wera.example/errors/validation",
                    Title: "طلب غير مقبول",
                    Status: 400,
                    Detail: ex.Message,
                    CorrelationId: correlationId,
                    OccurredAtUtc: DateTimeOffset.UtcNow);
                await context.Response.WriteAsync(JsonSerializer.Serialize(badRequest, JsonOptions));
                return;
            }
            LogUnhandled(ex, context.Request.Method, context.Request.Path, correlationId);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            var problem = new ApiProblem(
                Type: "https://wera.example/errors/unexpected",
                Title: "حدث خطأ غير متوقع",
                Status: 500,
                Detail: _env.IsDevelopment()
                    ? ex.Message
                    : "تعذر إكمال الطلب — سجل المعرف وراجع سجلات الخادم",
                CorrelationId: correlationId,
                OccurredAtUtc: DateTimeOffset.UtcNow);
            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "Unhandled exception on {Method} {Path} (correlation={CorrelationId})")]
    private partial void LogUnhandled(Exception ex, string method, string path, string? correlationId);
}
