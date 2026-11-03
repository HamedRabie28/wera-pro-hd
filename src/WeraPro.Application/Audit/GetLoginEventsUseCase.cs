namespace WeraPro.Application.Audit;

using WeraPro.Application.Abstractions;

/// <summary>حدث دخول واحد من سجل التدقيق (عرض فقط — بلا أي أسرار).</summary>
public sealed record LoginEvent(
    DateTimeOffset OccurredAtUtc,
    string? DisplayNameAr,
    string Action,
    string Result,
    string? Reason,
    string? CorrelationId);

/// <summary>قراءة أحداث الدخول من سجل التدقيق (تنفيقه Infrastructure).</summary>
public interface IAuditReadRepository
{
    Task<IReadOnlyList<LoginEvent>> GetRecentLoginEventsAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// عرض أحدث أحداث الدخول (§11.2/§18) — تُستدعى بصلاحية security.audit.view؛
/// الكاشير مرفوض (403) — الفحص خادمي عبر سياسة الصلاحية الذرية.
/// </summary>
public sealed class GetLoginEventsUseCase
{
    private readonly IAuditReadRepository _audit;

    public GetLoginEventsUseCase(IAuditReadRepository audit) => _audit = audit;

    public Task<IReadOnlyList<LoginEvent>> ExecuteAsync(int limit, CancellationToken ct = default) =>
        _audit.GetRecentLoginEventsAsync(Math.Clamp(limit, 1, 200), ct);
}
