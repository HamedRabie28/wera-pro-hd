namespace WeraPro.Application.Abstractions;

using WeraPro.Domain.Security;
using WeraPro.Domain.Settings;

/// <summary>مستودع قراءة المستخدمين والأدوار (تنفيقه Infrastructure عبر EF Core).</summary>
public interface IUserRepository
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> FindByIdAsync(long userId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPermissionCodesAsync(long userId, long? branchId, CancellationToken ct = default);
}

/// <summary>مستودع الجلسات (audit.user_sessions) ورموز التجديد (core.refresh_tokens).</summary>
public interface ISessionRepository
{
    Task<long> OpenSessionAsync(long userId, long? stationId, string? ipAddress, CancellationToken ct = default);
    Task CloseSessionAsync(long sessionId, CancellationToken ct = default);
}

public sealed record RefreshTokenRecord(long Id, long UserId, long SessionId, DateTimeOffset ExpiresAtUtc, DateTimeOffset IssuedAtUtc);

public interface IRefreshTokenStore
{
    Task<RefreshTokenRecord> IssueAsync(long userId, long sessionId, string tokenHash, DateTimeOffset expiresAtUtc, CancellationToken ct = default);
    Task<RefreshTokenRecord?> FindActiveByHashAsync(string tokenHash, CancellationToken ct = default);
    /// <summary>تدوير: وسم القديم بـ rotated_to وإصدار جديد ذرياً — إعادة استخدام القديم تكشف وتلغي السلسلة.</summary>
    Task<RefreshTokenRecord> RotateAsync(long oldTokenId, long userId, long sessionId, string newTokenHash, DateTimeOffset expiresAtUtc, CancellationToken ct = default);
    Task RevokeByUserAsync(long userId, string reasonAr, CancellationToken ct = default);
}

/// <summary>مستودع الإعدادات (core.business_settings) — قراءة فقط في G3.</summary>
public interface ISettingsRepository
{
    Task<IReadOnlyList<BusinessSetting>> ListAsync(string? category, CancellationToken ct = default);
}

/// <summary>مستودع الفروع والمحطات (core.branches / core.stations).</summary>
public interface IBranchRepository
{
    Task<IReadOnlyList<Branch>> ListActiveBranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Station>> ListStationsAsync(long branchId, bool activeOnly, CancellationToken ct = default);
}

public sealed record Branch(long BranchId, string Code, string NameAr, string Timezone, bool IsActive);
public sealed record Station(long StationId, long BranchId, string Code, string NameAr, bool IsPos, bool IsActive);
