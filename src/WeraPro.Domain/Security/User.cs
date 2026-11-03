namespace WeraPro.Domain.Security;

/// <summary>
/// مستخدم النظام — كلمة المرور hash فقط (Argon2id/bcrypt حصراً، §18).
/// الكيان صرف: لا يعرف شيئاً عن EF أو HTTP.
/// </summary>
public sealed class User
{
    public long UserId { get; init; }
    public required string Username { get; init; }
    public required string DisplayNameAr { get; init; }
    public string? Email { get; init; }
    public required string PasswordHash { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>إسنادات الأدوار (نطاق فرع أو كل الفروع).</summary>
    public IReadOnlyList<UserRole> Roles { get; init; } = [];

    /// <summary>
    /// هل يملك المستخدم دوراً فعّالاً داخل نطاق فرع معين؟
    /// الإسناد بلا فرع (branch_id = null) يعني كل الفروع (V001 uq_user_roles_scope).
    /// </summary>
    public bool HasRoleInBranch(string roleCode, long? branchId) =>
        IsActive && Roles.Any(r =>
            r.RoleCode == roleCode &&
            (r.BranchId is null || (branchId is not null && r.BranchId == branchId)));
}

/// <summary>إسناد دور لمستخدم داخل نطاق فرع — النطاق فارغ يعني كل الفروع.</summary>
public sealed class UserRole
{
    public long UserRoleId { get; init; }
    public long UserId { get; init; }
    public required string RoleCode { get; init; }
    /// <summary>null = كل الفروع.</summary>
    public long? BranchId { get; init; }
    public long? GrantedBy { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
}

/// <summary>دور مرجعي (سبعة أدوار seed).</summary>
public sealed class Role
{
    public long RoleId { get; init; }
    public required string Code { get; init; }
    public required string NameAr { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> PermissionCodes { get; init; } = [];
}

/// <summary>صلاحية ذرية (مثل sales.discount.over_15) — الفحص على الخادم لكل عملية (§18).</summary>
public sealed class PermissionDefinition
{
    public long PermissionId { get; init; }
    public required string Code { get; init; }
    public required string DomainArea { get; init; }
    public required string DescriptionAr { get; init; }
}
