namespace WeraPro.Application.Abstractions;

using WeraPro.Domain.Security;

/// <summary>تجزئة كلمات المرور — Argon2id/bcrypt فقط (§18) ولا نص صريح أبداً.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

/// <summary>إصدار رموز الجلسة: وصول قصير العمر + تجديد قابل للإلغاء على الخادم.</summary>
public interface ISessionTokenService
{
    (string AccessToken, DateTimeOffset ExpiresAtUtc) IssueAccessToken(User user, long? branchId, long? stationId, long sessionId);
    string GenerateRefreshToken();
    string HashRefreshToken(string refreshToken);
}

/// <summary>مستخدم الطلب الحالي (من JWT) — للتفويض والنطاق على الخادم.</summary>
public interface ICurrentUser
{
    long UserId { get; }
    string Username { get; }
    IReadOnlyList<string> Roles { get; }
    long? BranchId { get; }
    long? StationId { get; }
    long? SessionId { get; }
    bool IsAuthenticated { get; }
}

/// <summary>سجل التدقيق الإلزامي للعمليات الحساسة (audit.audit_logs).</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed record AuditEntry(
    long? UserId,
    string? RoleCode,
    long? StationId,
    long? BranchId,
    string EntitySchema,
    long EntityId,
    string Action,
    string Result,
    string? Reason = null,
    string? CorrelationId = null);
