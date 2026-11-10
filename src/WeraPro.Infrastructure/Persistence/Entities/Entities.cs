namespace WeraPro.Infrastructure.Persistence.Entities;

using System.ComponentModel.DataAnnotations.Schema;

/// <summary>كيانات الثبات — صور مباشرة لجداول V001/V008/V013 (أسماء snake_case مطابقة حرفياً).</summary>

[Table("branches", Schema = "core")]
public sealed class BranchRow
{
    public long BranchId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? NameEn { get; set; }
    public required string Timezone { get; set; } = "Africa/Cairo";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("stations", Schema = "core")]
public sealed class StationRow
{
    public long StationId { get; set; }
    public long BranchId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public bool IsPos { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

[Table("users", Schema = "core")]
public sealed class UserRow
{
    public long UserId { get; set; }
    public required string Username { get; set; }
    public required string DisplayNameAr { get; set; }
    public string? Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("roles", Schema = "core")]
public sealed class RoleRow
{
    public long RoleId { get; set; }
    public required string Code { get; set; }
    public required string NameAr { get; set; }
    public string? Description { get; set; }
}

[Table("permissions", Schema = "core")]
public sealed class PermissionRow
{
    public long PermissionId { get; set; }
    public required string Code { get; set; }
    public required string DomainArea { get; set; }
    public required string DescriptionAr { get; set; }
}

[Table("role_permissions", Schema = "core")]
public sealed class RolePermissionRow
{
    public long RoleId { get; set; }
    public long PermissionId { get; set; }
}

[Table("user_roles", Schema = "core")]
public sealed class UserRoleRow
{
    public long UserRoleId { get; set; }
    public long UserId { get; set; }
    public long RoleId { get; set; }
    /// <summary>null = كل الفروع (V001).</summary>
    public long? BranchId { get; set; }
    public long? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

[Table("business_settings", Schema = "core")]
public sealed class BusinessSettingRow
{
    public long SettingId { get; set; }
    public required string SettingKey { get; set; }
    public required string ScopeType { get; set; } = "BUSINESS";
    public long? BranchId { get; set; }
    public required string ValueText { get; set; }
    public required string ValueType { get; set; } = "string";
    public required string Category { get; set; }
    public string? DecisionRef { get; set; }
    public required string ConfirmationStatus { get; set; } = "APPROVED_WITH_SPEC";
    public required string DescriptionAr { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("user_sessions", Schema = "audit")]
public sealed class UserSessionRow
{
    public long SessionId { get; set; }
    public long UserId { get; set; }
    public long? StationId { get; set; }
    public DateTimeOffset LoginAt { get; set; }
    public DateTimeOffset? LogoutAt { get; set; }
    /// <summary>inet في PostgreSQL — خريطة Npgsql الأصلية لـIPAddress.</summary>
    public System.Net.IPAddress? IpAddress { get; set; }
    public string Outcome { get; set; } = "ACTIVE";
}

/// <summary>رمز تجديد الجلسة — hash فقط (V013).</summary>
[Table("refresh_tokens", Schema = "core")]
public sealed class RefreshTokenRow
{
    public long RefreshTokenId { get; set; }
    public long SessionId { get; set; }
    public required string TokenHash { get; set; }
    public long UserId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long? RotatedToId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}

[Table("audit_logs", Schema = "audit")]
public sealed class AuditLogRow
{
    public long AuditId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long UserId { get; set; }
    public string? RoleCode { get; set; }
    public long? StationId { get; set; }
    public long? BranchId { get; set; }
    public required string EntitySchema { get; set; }
    public long EntityId { get; set; }
    public required string Action { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? Reason { get; set; }
    public required string Result { get; set; }
    public string? CorrelationId { get; set; }
}

[Table("backup_records", Schema = "ops")]
public sealed class BackupRecordRow
{
    public long BackupId { get; set; }
    public required string BackupKind { get; set; } = "FULL";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? SizeBytes { get; set; }
    public required string Status { get; set; } = "RUNNING";
    public required string Scope { get; set; } = "db";
    public required string StorageLocationRef { get; set; }
    public required string BackupSetId { get; set; }
    public bool VerifiedReadable { get; set; }
    public string? ErrorSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>V020: من أنشأ النسخة وبصمة الملف (سلسلة الإسناد §19).</summary>
    public string CreatedBy { get; set; } = "system";
    public string? ChecksumSha256 { get; set; }
}
