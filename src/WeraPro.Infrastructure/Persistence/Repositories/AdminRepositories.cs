namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Security;
using WeraPro.Infrastructure.Persistence;
using WeraPro.Infrastructure.Persistence.Entities;

// ═══ G-ADMIN (PAGE-47/51): مستودع إدارة المستخدمين + سجل النسخ الاحتياطية ═══

/// <summary>
/// إدارة المستخدمين (core.users/user_roles): إنشاء/تعديل/تعطيل/كلمة مرور/إسناد.
/// التعطيل لا الحذف؛ الجلسات المرجعية (roles) للقراءة فقط — صلبة بـ V009.
/// </summary>
public sealed class UsersAdminRepository : IUsersAdminRepository
{
    private readonly WeraDbContext _db;

    public UsersAdminRepository(WeraDbContext db) => _db = db;

    public async Task<IReadOnlyList<UserAdminRow>> ListAsync(string? search, CancellationToken ct = default)
    {
        var query = from u in _db.Users.AsNoTracking()
                    join ur in _db.Set<UserRoleRow>().AsNoTracking() on u.UserId equals ur.UserId into urs
                    from ur in urs.DefaultIfEmpty()
                    join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.RoleId into rls
                    from r in rls.DefaultIfEmpty()
                    select new { u, ur, r };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.u.Username.Contains(term) || x.u.DisplayNameAr.Contains(term));
        }

        var flat = await query.OrderBy(x => x.u.UserId).ToListAsync(ct);
        var grouped = flat.GroupBy(x => x.u.UserId).Select(g => new UserAdminRow(
            g.Key,
            g.First().u.Username,
            g.First().u.DisplayNameAr,
            g.First().u.Email,
            g.First().u.IsActive,
            g.First().u.CreatedAt,
            g.First().u.UpdatedAt,
            [.. g.Where(x => x.ur is not null).Select(x => new AdminRoleAssignment(
                x.ur!.UserRoleId, x.r!.Code, x.r.NameAr, x.ur.BranchId, x.ur.GrantedAt))]))
            .ToList();
        return grouped;
    }

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default)
        => await _db.Users.AsNoTracking().AnyAsync(u => u.Username == username, ct);

    public async Task<bool> RoleExistsAsync(string roleCode, CancellationToken ct = default)
        => await _db.Roles.AsNoTracking().AnyAsync(r => r.Code == roleCode, ct);

    public async Task<long> CreateAsync(string username, string displayNameAr, string? email, string passwordHash,
        string roleCode, long? branchId, long grantedBy, CancellationToken ct = default)
    {
        var role = await _db.Roles.AsNoTracking().SingleAsync(r => r.Code == roleCode, ct);
        var user = new UserRow
        {
            Username = username,
            DisplayNameAr = displayNameAr,
            Email = email,
            PasswordHash = passwordHash,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        _db.Set<UserRoleRow>().Add(new UserRoleRow
        {
            UserId = user.UserId,
            RoleId = role.RoleId,
            BranchId = branchId,
            GrantedBy = grantedBy,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return user.UserId;
    }

    public async Task<bool> UpdateProfileAsync(long userId, string displayNameAr, string? email, CancellationToken ct = default)
    {
        var row = await _db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (row is null) return false;
        row.DisplayNameAr = displayNameAr;
        row.Email = email;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetActiveAsync(long userId, bool isActive, CancellationToken ct = default)
    {
        var row = await _db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (row is null) return false;
        row.IsActive = isActive;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(long userId, string passwordHash, CancellationToken ct = default)
    {
        var row = await _db.Users.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (row is null) return false;
        row.PasswordHash = passwordHash;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AdminRoleAssignment>> ListRolesAsync(long userId, CancellationToken ct = default)
        => await (from ur in _db.Set<UserRoleRow>().AsNoTracking()
                  join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.RoleId
                  where ur.UserId == userId
                  select new AdminRoleAssignment(ur.UserRoleId, r.Code, r.NameAr, ur.BranchId, ur.GrantedAt))
                  .OrderBy(x => x.GrantedAt).ToListAsync(ct);

    public async Task<long> AssignRoleAsync(long userId, string roleCode, long? branchId, long grantedBy, CancellationToken ct = default)
    {
        var role = await _db.Roles.AsNoTracking().SingleAsync(r => r.Code == roleCode, ct);
        var row = new UserRoleRow
        {
            UserId = userId,
            RoleId = role.RoleId,
            BranchId = branchId,
            GrantedBy = grantedBy,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        _db.Set<UserRoleRow>().Add(row);
        await _db.SaveChangesAsync(ct);
        return row.UserRoleId;
    }

    public async Task<bool> RevokeRoleAsync(long userId, long userRoleId, CancellationToken ct = default)
    {
        var row = await _db.Set<UserRoleRow>()
            .SingleOrDefaultAsync(ur => ur.UserRoleId == userRoleId && ur.UserId == userId, ct);
        if (row is null) return false;
        _db.Set<UserRoleRow>().Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>عدد مالكي ACCOUNT النشطين — حارس «آخر OWNER» في حالة الاستخدام.</summary>
    public async Task<int> CountActiveOwnersAsync(CancellationToken ct = default)
        => await (from u in _db.Users.AsNoTracking()
                  join ur in _db.Set<UserRoleRow>().AsNoTracking() on u.UserId equals ur.UserId
                  join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.RoleId
                  where r.Code == RoleCodes.Owner && u.IsActive
                  select u.UserId).Distinct().CountAsync(ct);

    public async Task<IReadOnlyList<RoleOverview>> ListRoleOverviewsAsync(CancellationToken ct = default)
    {
        var roles = await _db.Roles.AsNoTracking().OrderBy(r => r.RoleId).ToListAsync(ct);
        var perms = await (from rp in _db.Set<RolePermissionRow>().AsNoTracking()
                           join p in _db.Permissions.AsNoTracking() on rp.PermissionId equals p.PermissionId
                           select new { rp.RoleId, p.Code, p.DomainArea, p.DescriptionAr })
                          .ToListAsync(ct);
        return [.. roles.Select(r => new RoleOverview(
            r.RoleId, r.Code, r.NameAr, r.Description,
            [.. perms.Where(p => p.RoleId == r.RoleId)
                     .OrderBy(p => p.DomainArea).ThenBy(p => p.Code)
                     .Select(p => new PermissionCell(p.Code, p.DomainArea, p.DescriptionAr))]))];
    }
}

/// <summary>سجل النسخ الاحتياطية (ops.backup_records · V008/V020) — سلسلة إسناد كاملة.</summary>
public sealed class BackupRepository : IBackupRepository
{
    private readonly WeraDbContext _db;

    public BackupRepository(WeraDbContext db) => _db = db;

    public async Task<IReadOnlyList<BackupRecord>> ListAsync(int limit, CancellationToken ct = default)
    {
        var cap = Math.Clamp(limit, 1, 200);
        var rows = await _db.BackupRecords.AsNoTracking()
            .OrderByDescending(b => b.BackupId).Take(cap).ToListAsync(ct);
        return [.. rows.Select(r => new BackupRecord(
            r.BackupId, r.BackupSetId, r.BackupKind, r.Status,
            r.SizeBytes ?? 0, r.StartedAt, r.CreatedBy, r.ChecksumSha256, r.ErrorSummary))];
    }

    public async Task<long> RegisterAsync(string fileName, string kind, long sizeBytes, string createdBy,
        string? checksum, string? noteAr, CancellationToken ct = default)
    {
        var row = new BackupRecordRow
        {
            BackupKind = kind,
            StartedAt = DateTimeOffset.UtcNow,
            SizeBytes = sizeBytes is 0 ? null : sizeBytes,
            Status = "RUNNING",
            Scope = "db",
            StorageLocationRef = "local://backups/" + fileName,
            BackupSetId = fileName,
            VerifiedReadable = false,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
            ChecksumSha256 = checksum,
            ErrorSummary = noteAr,
        };
        _db.BackupRecords.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.BackupId;
    }

    public async Task<BackupRecord?> FindByIdAsync(long backupId, CancellationToken ct = default)
    {
        var r = await _db.BackupRecords.AsNoTracking().SingleOrDefaultAsync(b => b.BackupId == backupId, ct);
        return r is null ? null : new BackupRecord(
            r.BackupId, r.BackupSetId, r.BackupKind, r.Status,
            r.SizeBytes ?? 0, r.StartedAt, r.CreatedBy, r.ChecksumSha256, r.ErrorSummary);
    }

    public async Task MarkStatusAsync(long backupId, string status, CancellationToken ct = default)
    {
        var row = await _db.BackupRecords.SingleOrDefaultAsync(b => b.BackupId == backupId, ct);
        if (row is null) return;
        row.Status = status;
        row.VerifiedReadable = status == "VERIFIED";
        row.FinishedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateSizeAsync(long backupId, long sizeBytes, string? checksum, CancellationToken ct = default)
    {
        var row = await _db.BackupRecords.SingleOrDefaultAsync(b => b.BackupId == backupId, ct);
        if (row is null) return;
        row.SizeBytes = sizeBytes;
        row.ChecksumSha256 = checksum;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(long backupId, string? errorSummary, CancellationToken ct = default)
    {
        var row = await _db.BackupRecords.SingleOrDefaultAsync(b => b.BackupId == backupId, ct);
        if (row is null) return;
        row.Status = "FAILED";
        row.ErrorSummary = errorSummary?[..Math.Min(errorSummary.Length, 500)];
        row.FinishedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteRegistryAsync(long backupId, CancellationToken ct = default)
    {
        var row = await _db.BackupRecords.SingleOrDefaultAsync(b => b.BackupId == backupId, ct);
        if (row is null) return false;
        _db.BackupRecords.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
