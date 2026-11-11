namespace WeraPro.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>
/// تهيئة EF — أسماء الأعمدة snake_case مطابقة للترحيلات حرفياً (ADR-009:
/// المخطط مملوك لملفات SQL؛ EF موقّع عليه بالتطابق لا بالتوليد).
/// </summary>
public sealed class BranchRowConfiguration : IEntityTypeConfiguration<BranchRow>
{
    public void Configure(EntityTypeBuilder<BranchRow> builder)
    {
        builder.ToTable("branches", "core");
        builder.HasKey(x => x.BranchId);
        builder.Property(x => x.BranchId).HasColumnName("branch_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.NameEn).HasColumnName("name_en");
        builder.Property(x => x.Timezone).HasColumnName("timezone");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class StationRowConfiguration : IEntityTypeConfiguration<StationRow>
{
    public void Configure(EntityTypeBuilder<StationRow> builder)
    {
        builder.ToTable("stations", "core");
        builder.HasKey(x => x.StationId);
        builder.Property(x => x.StationId).HasColumnName("station_id").ValueGeneratedOnAdd();
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.IsPos).HasColumnName("is_pos");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(x => new { x.BranchId, x.Code }).IsUnique();
        builder.HasOne<BranchRow>().WithMany().HasForeignKey(x => x.BranchId);
    }
}

public sealed class UserRowConfiguration : IEntityTypeConfiguration<UserRow>
{
    public void Configure(EntityTypeBuilder<UserRow> builder)
    {
        builder.ToTable("users", "core");
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Username).HasColumnName("username").HasMaxLength(64);
        builder.Property(x => x.DisplayNameAr).HasColumnName("display_name_ar");
        builder.Property(x => x.Email).HasColumnName("email");
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.Username).IsUnique();
    }
}

public sealed class RoleRowConfiguration : IEntityTypeConfiguration<RoleRow>
{
    public void Configure(EntityTypeBuilder<RoleRow> builder)
    {
        builder.ToTable("roles", "core");
        builder.HasKey(x => x.RoleId);
        builder.Property(x => x.RoleId).HasColumnName("role_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.NameAr).HasColumnName("name_ar");
        builder.Property(x => x.Description).HasColumnName("description");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class PermissionRowConfiguration : IEntityTypeConfiguration<PermissionRow>
{
    public void Configure(EntityTypeBuilder<PermissionRow> builder)
    {
        builder.ToTable("permissions", "core");
        builder.HasKey(x => x.PermissionId);
        builder.Property(x => x.PermissionId).HasColumnName("permission_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(128);
        builder.Property(x => x.DomainArea).HasColumnName("domain_area");
        builder.Property(x => x.DescriptionAr).HasColumnName("description_ar");
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class UserRoleRowConfiguration : IEntityTypeConfiguration<UserRoleRow>
{
    public void Configure(EntityTypeBuilder<UserRoleRow> builder)
    {
        builder.ToTable("user_roles", "core");
        builder.HasKey(x => x.UserRoleId);
        builder.Property(x => x.UserRoleId).HasColumnName("user_role_id").ValueGeneratedOnAdd();
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.RoleId).HasColumnName("role_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.GrantedBy).HasColumnName("granted_by");
        builder.Property(x => x.GrantedAt).HasColumnName("granted_at");
        builder.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasOne<RoleRow>().WithMany().HasForeignKey(x => x.RoleId);
    }
}

public sealed class RolePermissionRowConfiguration : IEntityTypeConfiguration<RolePermissionRow>
{
    public void Configure(EntityTypeBuilder<RolePermissionRow> builder)
    {
        builder.ToTable("role_permissions", "core");
        builder.HasKey(x => new { x.RoleId, x.PermissionId });
        builder.Property(x => x.RoleId).HasColumnName("role_id");
        builder.Property(x => x.PermissionId).HasColumnName("permission_id");
        builder.HasOne<RoleRow>().WithMany().HasForeignKey(x => x.RoleId);
        builder.HasOne<PermissionRow>().WithMany().HasForeignKey(x => x.PermissionId);
    }
}

public sealed class BusinessSettingRowConfiguration : IEntityTypeConfiguration<BusinessSettingRow>
{
    public void Configure(EntityTypeBuilder<BusinessSettingRow> builder)
    {
        builder.ToTable("business_settings", "core");
        builder.HasKey(x => x.SettingId);
        builder.Property(x => x.SettingId).HasColumnName("setting_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SettingKey).HasColumnName("setting_key").HasMaxLength(128);
        builder.Property(x => x.ScopeType).HasColumnName("scope_type").HasMaxLength(16);
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.ValueText).HasColumnName("value_text");
        builder.Property(x => x.ValueType).HasColumnName("value_type").HasMaxLength(16);
        builder.Property(x => x.Category).HasColumnName("category").HasMaxLength(32);
        builder.Property(x => x.DecisionRef).HasColumnName("decision_ref").HasMaxLength(8);
        builder.Property(x => x.ConfirmationStatus).HasColumnName("confirmation_status").HasMaxLength(32);
        builder.Property(x => x.DescriptionAr).HasColumnName("description_ar");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => x.SettingKey);
    }
}

public sealed class UserSessionRowConfiguration : IEntityTypeConfiguration<UserSessionRow>
{
    public void Configure(EntityTypeBuilder<UserSessionRow> builder)
    {
        builder.ToTable("user_sessions", "audit");
        builder.HasKey(x => x.SessionId);
        builder.Property(x => x.SessionId).HasColumnName("session_id").ValueGeneratedOnAdd();
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.LoginAt).HasColumnName("login_at");
        builder.Property(x => x.LogoutAt).HasColumnName("logout_at");
        // inet في PostgreSQL ↔ IPAddress في C# (خريطة Npgsql الأصلية — لا تحويل يدوي)
        builder.Property(x => x.IpAddress).HasColumnName("ip_address");
        builder.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(16);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
    }
}

public sealed class RefreshTokenRowConfiguration : IEntityTypeConfiguration<RefreshTokenRow>
{
    public void Configure(EntityTypeBuilder<RefreshTokenRow> builder)
    {
        builder.ToTable("refresh_tokens", "core");
        builder.HasKey(x => x.RefreshTokenId);
        builder.Property(x => x.RefreshTokenId).HasColumnName("refresh_token_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128);
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.IssuedAt).HasColumnName("issued_at");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.RotatedToId).HasColumnName("rotated_to_id");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        builder.Property(x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(64);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasOne<UserSessionRow>().WithMany().HasForeignKey(x => x.SessionId);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
    }
}

public sealed class AuditLogRowConfiguration : IEntityTypeConfiguration<AuditLogRow>
{
    public void Configure(EntityTypeBuilder<AuditLogRow> builder)
    {
        builder.ToTable("audit_logs", "audit");
        builder.HasKey(x => x.AuditId);
        builder.Property(x => x.AuditId).HasColumnName("audit_id").ValueGeneratedOnAdd();
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.RoleCode).HasColumnName("role_code").HasMaxLength(32);
        builder.Property(x => x.StationId).HasColumnName("station_id");
        builder.Property(x => x.BranchId).HasColumnName("branch_id");
        builder.Property(x => x.EntitySchema).HasColumnName("entity_schema").HasMaxLength(64);
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(32);
        // jsonb في PostgreSQL — تحديد النوع صراحةً لسلاسل JSON
        builder.Property(x => x.OldValues).HasColumnName("old_values").HasColumnType("jsonb");
        builder.Property(x => x.NewValues).HasColumnName("new_values").HasColumnType("jsonb");
        builder.Property(x => x.Reason).HasColumnName("reason");
        builder.Property(x => x.Result).HasColumnName("result").HasMaxLength(16);
        builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId);
        builder.HasIndex(x => new { x.EntitySchema, x.EntityId });
        builder.HasIndex(x => x.CorrelationId);
    }
}

/// <summary>
/// G-ADMIN (V008/V020): سجل النسخ الاحتياطية ops.backup_records — التهيئة التي كانت
/// مفقودة فأبطلت نموذج EF كاملاً (خطأ إقلاع دورة عامل الاستيراد 2026-11-11).
/// </summary>
public sealed class BackupRecordRowConfiguration : IEntityTypeConfiguration<BackupRecordRow>
{
    public void Configure(EntityTypeBuilder<BackupRecordRow> builder)
    {
        builder.ToTable("backup_records", "ops");
        builder.HasKey(x => x.BackupId);
        builder.Property(x => x.BackupId).HasColumnName("backup_id").ValueGeneratedOnAdd();
        builder.Property(x => x.BackupKind).HasColumnName("backup_kind").HasMaxLength(16);
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at");
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(16);
        builder.Property(x => x.StorageLocationRef).HasColumnName("storage_location_ref").HasMaxLength(512);
        builder.Property(x => x.BackupSetId).HasColumnName("backup_set_id").HasMaxLength(64);
        builder.Property(x => x.VerifiedReadable).HasColumnName("verified_readable");
        builder.Property(x => x.ErrorSummary).HasColumnName("error_summary");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
        builder.Property(x => x.ChecksumSha256).HasColumnName("checksum_sha256").HasMaxLength(64);
        builder.HasIndex(x => x.CreatedAt);
    }
}
