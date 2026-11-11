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
    /// <summary>إبطال توكنات جلسة محددة فقط — خروج الجلسة الواحدة لا يقتل جلسات المستخدم الأخرى (إصلاح 2026-11-11).</summary>
    Task RevokeBySessionAsync(long sessionId, string reasonAr, CancellationToken ct = default);
}

/// <summary>مستودع الإعدادات (core.business_settings) — القراءة للجميع والتعديل بصلاحية.</summary>
public interface ISettingsRepository
{
    Task<IReadOnlyList<BusinessSetting>> ListAsync(string? category, CancellationToken ct = default);
    Task<BusinessSetting?> FindByKeyAsync(string settingKey, CancellationToken ct = default);
    Task<BusinessSetting> UpdateValueAsync(string settingKey, string valueText, CancellationToken ct = default);
}

/// <summary>مستودع الفروع والمحطات (core.branches / core.stations).</summary>
public interface IBranchRepository
{
    Task<IReadOnlyList<Branch>> ListActiveBranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Station>> ListStationsAsync(long branchId, bool activeOnly, CancellationToken ct = default);
}

public sealed record Branch(long BranchId, string Code, string NameAr, string Timezone, bool IsActive);
public sealed record Station(long StationId, long BranchId, string Code, string NameAr, bool IsPos, bool IsActive);

// ── إدارة المستخدمين (PAGE-47) — الإسناد والتعطيل، لا حذف حساب ────────────────

/// <summary>سطر مستخدم إداري مركّب: الدور + نطاقه — بلا hash أبداً (§18).</summary>
public sealed record UserAdminRow(
    long UserId, string Username, string DisplayNameAr, string? Email,
    bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<AdminRoleAssignment> Roles);

/// <summary>إسناد دور واحد: الكود + الاسم + النطاق + معرف السطر (للفك).</summary>
public sealed record AdminRoleAssignment(long UserRoleId, string RoleCode, string RoleNameAr, long? BranchId, DateTimeOffset GrantedAt);

/// <summary>دور مرجعي بصلاحياته الذرية (عرض فقط — الأدوار صلبة V009).</summary>
public sealed record RoleOverview(long RoleId, string Code, string NameAr, string? Description, IReadOnlyList<PermissionCell> Permissions);

/// <summary>صلاحية ذرية.</summary>
public sealed record PermissionCell(string Code, string DomainArea, string DescriptionAr);

/// <summary>مستودع الإدارة: قائمة المستخدمين، الإنشاء، التعديل، الإسناد، كلمة المرور.</summary>
public interface IUsersAdminRepository
{
    Task<IReadOnlyList<UserAdminRow>> ListAsync(string? search, CancellationToken ct = default);
    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);
    Task<long> CreateAsync(string username, string displayNameAr, string? email, string passwordHash, string roleCode, long? branchId, long grantedBy, CancellationToken ct = default);
    Task<bool> UpdateProfileAsync(long userId, string displayNameAr, string? email, CancellationToken ct = default);
    Task<bool> SetActiveAsync(long userId, bool isActive, CancellationToken ct = default);
    Task<bool> ResetPasswordAsync(long userId, string passwordHash, CancellationToken ct = default);
    Task<IReadOnlyList<AdminRoleAssignment>> ListRolesAsync(long userId, CancellationToken ct = default);
    Task<long> AssignRoleAsync(long userId, string roleCode, long? branchId, long grantedBy, CancellationToken ct = default);
    Task<bool> RevokeRoleAsync(long userId, long userRoleId, CancellationToken ct = default);
    Task<int> CountActiveOwnersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RoleOverview>> ListRoleOverviewsAsync(CancellationToken ct = default);
    Task<bool> RoleExistsAsync(string roleCode, CancellationToken ct = default);
}

// ── النسخ الاحتياطي والصيانة (PAGE-51) ───────────────────────────────────────

/// <summary>سجل نسخة احتياطية مادية (system.backups).</summary>
public sealed record BackupRecord(
    long BackupId, string FileName, string Kind, string Status,
    long SizeBytes, DateTimeOffset CreatedAtUtc, string CreatedBy,
    string? Checksum, string? NoteAr);

/// <summary>مستودع سجل النسخ الاحتياطية — إنشاء/قائمة/وسم الحالة/حذف السجل.</summary>
public interface IBackupRepository
{
    Task<IReadOnlyList<BackupRecord>> ListAsync(int limit, CancellationToken ct = default);
    Task<long> RegisterAsync(string fileName, string kind, long sizeBytes, string createdBy, string? checksum, string? noteAr, CancellationToken ct = default);
    Task<BackupRecord?> FindByIdAsync(long backupId, CancellationToken ct = default);
    Task MarkStatusAsync(long backupId, string status, CancellationToken ct = default);
    Task UpdateSizeAsync(long backupId, long sizeBytes, string? checksum, CancellationToken ct = default);
    Task MarkFailedAsync(long backupId, string? errorSummary, CancellationToken ct = default);
    Task<bool> DeleteRegistryAsync(long backupId, CancellationToken ct = default);
}
