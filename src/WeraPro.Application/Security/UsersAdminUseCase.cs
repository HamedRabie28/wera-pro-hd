namespace WeraPro.Application.Security;

using WeraPro.Application.Abstractions;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;

/// <summary>
/// إدارة المستخدمين والأدوار (PAGE-47): قائمة/إنشاء/تعديل/تعطيل/كلمة مرور/إسناد.
/// القواعد الصلبة: hash فقط (§18) · التعطيل لا الحذف · لا تعطيل آخر OWNER نشط ·
/// الأدوار المرجعية السبعة صلبة (V009) — الإسناد والفك فقط.
/// </summary>
public sealed class UsersAdminUseCase
{
    private readonly IUsersAdminRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly IAuditWriter _audit;

    public UsersAdminUseCase(IUsersAdminRepository users, IPasswordHasher hasher, IAuditWriter audit)
    {
        _users = users;
        _hasher = hasher;
        _audit = audit;
    }

    public async Task<IReadOnlyList<UserAdminDto>> ListAsync(string? search, CancellationToken ct = default)
    {
        var rows = await _users.ListAsync(search, ct);
        return [.. rows.Select(ToDto)];
    }

    public async Task<IReadOnlyList<RoleDetailDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var roles = await _users.ListRoleOverviewsAsync(ct);
        return [.. roles.Select(r => new RoleDetailDto(
            r.RoleId, r.Code, r.NameAr, r.Description,
            [.. r.Permissions.Select(p => new PermissionDto(p.Code, p.DomainArea, p.DescriptionAr))]))];
    }

    public async Task<UserAdminDto> CreateAsync(CreateUserRequest request, long actorUserId, CancellationToken ct = default)
    {
        var username = request.Username.Trim();
        if (username.Length is < 3 or > 40 || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
        {
            throw new InvalidOperationException("اسم الدخول: 3–40 حرفاً إنجليزياً/أرقاماً و _ . - فقط");
        }
        if (string.IsNullOrWhiteSpace(request.DisplayNameAr))
        {
            throw new InvalidOperationException("الاسم بالعربية مطلوب");
        }
        ValidatePassword(request.Password);
        if (await _users.UsernameExistsAsync(username, ct))
        {
            throw new InvalidOperationException($"اسم الدخول «{username}» مستخدم مسبقاً");
        }
        if (!await _users.RoleExistsAsync(request.RoleCode, ct))
        {
            throw new InvalidOperationException($"الدور «{request.RoleCode}» غير معرف (الأدوار المرجعية السبعة فقط)");
        }

        var hash = _hasher.Hash(request.Password);
        var userId = await _users.CreateAsync(
            username, request.DisplayNameAr.Trim(), request.Email?.Trim(), hash,
            request.RoleCode, request.BranchId, actorUserId, ct);

        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, request.BranchId, "core.users", userId,
            "USER_CREATE", "OK", $"دور {request.RoleCode}" + (request.BranchId is null ? " (كل الفروع)" : $" (فرع {request.BranchId})")), ct);

        var rows = await _users.ListAsync(null, ct);
        return ToDto(rows.Single(u => u.UserId == userId));
    }

    public async Task<bool> UpdateProfileAsync(long userId, UpdateUserRequest request, long actorUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayNameAr))
        {
            throw new InvalidOperationException("الاسم بالعربية مطلوب");
        }
        var ok = await _users.UpdateProfileAsync(userId, request.DisplayNameAr.Trim(), request.Email?.Trim(), ct);
        if (ok)
        {
            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, null, "core.users", userId, "USER_UPDATE", "OK", "تعديل بيانات"), ct);
        }
        return ok;
    }

    public async Task<bool> SetActiveAsync(long userId, SetUserActiveRequest request, long actorUserId, CancellationToken ct = default)
    {
        // لا يجوز تعطيل آخر OWNER نشط — القفل يبقى للمالك دائماً (V009 حارس الملكية)
        if (!request.IsActive && await _users.CountActiveOwnersAsync(ct) == 1 &&
            (await _users.ListRolesAsync(userId, ct)).Any(r => r.RoleCode == RoleCodes.Owner))
        {
            throw new InvalidOperationException("لا يمكن تعطيل آخر مالك (OWNER) نشط — أنشئ مالكاً بديلاً أولاً");
        }
        if (userId == actorUserId && !request.IsActive)
        {
            throw new InvalidOperationException("لا يمكنك تعطيل حسابك الحالي");
        }
        var ok = await _users.SetActiveAsync(userId, request.IsActive, ct);
        if (ok)
        {
            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, null, "core.users", userId,
                request.IsActive ? "USER_ACTIVATE" : "USER_DEACTIVATE", "OK", request.ReasonAr), ct);
        }
        return ok;
    }

    public async Task<bool> ResetPasswordAsync(long userId, ResetPasswordRequest request, long actorUserId, CancellationToken ct = default)
    {
        ValidatePassword(request.NewPassword);
        var ok = await _users.ResetPasswordAsync(userId, _hasher.Hash(request.NewPassword), ct);
        if (ok)
        {
            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, null, "core.users", userId,
                "USER_PASSWORD_RESET", "OK", request.ReasonAr ?? "إعادة تعيين كلمة المرور"), ct);
        }
        return ok;
    }

    public async Task<IReadOnlyList<AdminRoleAssignment>> ListUserRolesAsync(long userId, CancellationToken ct = default)
        => await _users.ListRolesAsync(userId, ct);

    public async Task<AdminRoleAssignment> AssignRoleAsync(long userId, AssignRoleRequest request, long actorUserId, CancellationToken ct = default)
    {
        if (!await _users.RoleExistsAsync(request.RoleCode, ct))
        {
            throw new InvalidOperationException($"الدور «{request.RoleCode}» غير معرف");
        }
        var existing = await _users.ListRolesAsync(userId, ct);
        if (existing.Any(r => r.RoleCode == request.RoleCode && r.BranchId == request.BranchId))
        {
            throw new InvalidOperationException("الدور مُسند مسبقاً بنفس النطاق");
        }
        var userRoleId = await _users.AssignRoleAsync(userId, request.RoleCode, request.BranchId, actorUserId, ct);
        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, request.BranchId, "core.user_roles", userRoleId,
            "ROLE_ASSIGN", "OK", $"إسناد {request.RoleCode}" + (request.ReasonAr is null ? "" : $" — {request.ReasonAr}")), ct);
        var updated = await _users.ListRolesAsync(userId, ct);
        return updated.Single(r => r.UserRoleId == userRoleId);
    }

    public async Task<bool> RevokeRoleAsync(long userId, RevokeRoleRequest request, long actorUserId, CancellationToken ct = default)
    {
        var roles = await _users.ListRolesAsync(userId, ct);
        var target = roles.FirstOrDefault(r => r.UserRoleId == request.UserRoleId);
        if (target is null)
        {
            throw new InvalidOperationException("الإسناد غير موجود لهذا المستخدم");
        }
        if (target.RoleCode == RoleCodes.Owner && await _users.CountActiveOwnersAsync(ct) == 1)
        {
            throw new InvalidOperationException("لا يمكن سحب دور المالك من آخر مالك نشط");
        }
        var ok = await _users.RevokeRoleAsync(userId, request.UserRoleId, ct);
        if (ok)
        {
            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, target.BranchId, "core.user_roles", request.UserRoleId,
                "ROLE_REVOKE", "OK", $"سحب {target.RoleCode}" + (request.ReasonAr is null ? "" : $" — {request.ReasonAr}")), ct);
        }
        return ok;
    }

    /// <summary>حد أدنى: 8 أحرف مع رقم — سياسة الكلمة تُفصَّل في بوابة الأمان (§18).</summary>
    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || !password.Any(char.IsDigit))
        {
            throw new InvalidOperationException("كلمة المرور: 8 أحرف على الأقل وتتضمن رقماً واحداً على الأقل");
        }
    }

    private static UserAdminDto ToDto(UserAdminRow u) => new(
        u.UserId, u.Username, u.DisplayNameAr, u.Email, u.IsActive, u.CreatedAt, u.UpdatedAt,
        [.. u.Roles.Select(r => r.RoleCode)],
        [.. u.Roles.Select(r => r.RoleNameAr + (r.BranchId is null ? "" : $" (فرع {r.BranchId})"))]);
}
