namespace WeraPro.Api.Middleware;

/// <summary>
/// معرف الترابط (§11.1/§20): يقرأ X-Correlation-ID الوارد (مقيد الطول والمحارف)
/// أو يولد واحداً جديداً، يضعه في HttpContext.Items + LogContext + ترويسة الاستجابة،
/// ويصل به حتى audit_logs. العميل (Desktop) يولده لكل عملية ويجدده لكل إعادة محاولة.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private const string Prefix = "wera-";
    private const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Sanitize(context.Request.Headers[HeaderName].FirstOrDefault())
                            ?? Prefix + Guid.NewGuid().ToString("N")[..20];

        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }

    /// <summary>يقبل [A-Za-z0-9-_] بطول معقول فقط — لا حقن سجلات ولا ترويسات.</summary>
    private static string? Sanitize(string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming) || incoming.Length > MaxLength)
        {
            return null;
        }
        return incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? incoming : null;
    }
}

public static class CorrelationIdExtensions
{
    /// <summary>قراءة معرف الترابط الحالي من أي مكان في الطلب.</summary>
    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var value) ? value as string : null;
}
