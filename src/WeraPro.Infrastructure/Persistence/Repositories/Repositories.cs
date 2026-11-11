namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Security;
using WeraPro.Domain.Settings;
using WeraPro.Infrastructure.Persistence;
using WeraPro.Infrastructure.Persistence.Entities;

public sealed class UserRepository : IUserRepository
{
    private readonly WeraDbContext _db;

    public UserRepository(WeraDbContext db) => _db = db;

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var row = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == username, ct);
        return row is null ? null : await ComposeAsync(row, ct);
    }

    public async Task<User?> FindByIdAsync(long userId, CancellationToken ct = default)
    {
        var row = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, ct);
        return row is null ? null : await ComposeAsync(row, ct);
    }

    private async Task<User> ComposeAsync(UserRow row, CancellationToken ct)
    {
        var roles = await (
            from ur in _db.Set<UserRoleRow>().AsNoTracking()
            join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.RoleId
            where ur.UserId == row.UserId
            select new UserRole
            {
                UserRoleId = ur.UserRoleId,
                UserId = ur.UserId,
                RoleCode = r.Code,
                BranchId = ur.BranchId,
                GrantedBy = ur.GrantedBy,
                GrantedAt = ur.GrantedAt,
            }).ToListAsync(ct);

        return new User
        {
            UserId = row.UserId,
            Username = row.Username,
            DisplayNameAr = row.DisplayNameAr,
            Email = row.Email,
            PasswordHash = row.PasswordHash,
            IsActive = row.IsActive,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            Roles = roles,
        };
    }

    public async Task<IReadOnlyList<string>> GetPermissionCodesAsync(long userId, long? branchId, CancellationToken ct = default)
    {
        var query =
            from ur in _db.Set<UserRoleRow>().AsNoTracking()
            join rp in _db.Set<RolePermissionRow>().AsNoTracking() on ur.RoleId equals rp.RoleId
            join p in _db.Permissions.AsNoTracking() on rp.PermissionId equals p.PermissionId
            where ur.UserId == userId && (ur.BranchId == null || (branchId != null && ur.BranchId == branchId))
            select p.Code;
        return [.. (await query.Distinct().OrderBy(x => x).ToListAsync(ct))];
    }
}

public sealed class SessionRepository : ISessionRepository
{
    private readonly WeraDbContext _db;

    public SessionRepository(WeraDbContext db) => _db = db;

    public async Task<long> OpenSessionAsync(long userId, long? stationId, string? ipAddress, CancellationToken ct = default)
    {
        var session = new UserSessionRow
        {
            UserId = userId,
            StationId = stationId,
            IpAddress = ipAddress is null ? null : System.Net.IPAddress.Parse(ipAddress),
            LoginAt = DateTimeOffset.UtcNow,
            Outcome = "ACTIVE",
        };
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session.SessionId;
    }

    public async Task CloseSessionAsync(long sessionId, CancellationToken ct = default)
    {
        var session = await _db.UserSessions.SingleOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (session is not null && session.Outcome == "ACTIVE")
        {
            session.Outcome = "LOGOUT";
            session.LogoutAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}

public sealed class RefreshTokenStore : IRefreshTokenStore
{
    private readonly WeraDbContext _db;

    public RefreshTokenStore(WeraDbContext db) => _db = db;

    public async Task<RefreshTokenRecord> IssueAsync(long userId, long sessionId, string tokenHash, DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        var row = new RefreshTokenRow
        {
            SessionId = sessionId,
            TokenHash = tokenHash,
            UserId = userId,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAtUtc,
        };
        _db.RefreshTokens.Add(row);
        await _db.SaveChangesAsync(ct);
        return ToRecord(row);
    }

    public async Task<RefreshTokenRecord?> FindActiveByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var row = await _db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        return row is null || row.RevokedAt is not null || row.RotatedToId is not null || row.ExpiresAt <= DateTimeOffset.UtcNow
            ? null
            : ToRecord(row);
    }

    public async Task<RefreshTokenRecord> RotateAsync(long oldTokenId, long userId, long sessionId, string newTokenHash, DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        // EnableRetryOnFailure يمنع المعاملات المستخدم المباشرة — التدوير كله
        // داخل استراتيجية التنفيذ (ذريّ واحد: وسم القديم + إصدار الجديد)
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var old = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.RefreshTokenId == oldTokenId, ct)
                ?? throw new InvalidOperationException("رمز التجديد القديم غير موجود");
            var fresh = new RefreshTokenRow
            {
                SessionId = sessionId,
                TokenHash = newTokenHash,
                UserId = userId,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAtUtc,
            };
            _db.RefreshTokens.Add(fresh);
            await _db.SaveChangesAsync(ct);
            old.RotatedToId = fresh.RefreshTokenId;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToRecord(fresh);
        });
    }

    public async Task RevokeByUserAsync(long userId, string reasonAr, CancellationToken ct = default)
    {
        var actives = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in actives)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            token.RevokedReason = reasonAr;
        }
        if (actives.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// إبطال توكنات جلسة واحدة (إصلاح 2026-11-11): الخروج من جلسة يجب ألا يقتل توكنات
    /// الجلسات الأخرى لنفس المستخدم (تسجيل الدخول الموقّع نطاقياً بعد المركزي أنشأ جلسة ثانية).
    /// </summary>
    public async Task RevokeBySessionAsync(long sessionId, string reasonAr, CancellationToken ct = default)
    {
        var actives = await _db.RefreshTokens
            .Where(t => t.SessionId == sessionId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in actives)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            token.RevokedReason = reasonAr;
        }
        if (actives.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    private static RefreshTokenRecord ToRecord(RefreshTokenRow row) =>
        new(row.RefreshTokenId, row.UserId, row.SessionId, row.ExpiresAt, row.IssuedAt);
}

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly WeraDbContext _db;

    public SettingsRepository(WeraDbContext db) => _db = db;

    public async Task<IReadOnlyList<BusinessSetting>> ListAsync(string? category, CancellationToken ct = default)
    {
        var query = _db.BusinessSettings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(s => s.Category == category);
        }
        var rows = await query.OrderBy(s => s.SettingKey).ToListAsync(ct);
        return [.. rows.Select(r => new BusinessSetting
        {
            SettingId = r.SettingId,
            SettingKey = r.SettingKey,
            ScopeType = r.ScopeType,
            BranchId = r.BranchId,
            ValueText = r.ValueText,
            ValueType = r.ValueType,
            Category = r.Category,
            DecisionRef = r.DecisionRef,
            Confirmation = r.ConfirmationStatus switch
            {
                "OWNER_CONFIRMED" => ConfirmationStatus.OwnerConfirmed,
                "PENDING_OWNER_CONFIRMATION" => ConfirmationStatus.PendingOwnerConfirmation,
                _ => ConfirmationStatus.ApprovedWithSpec,
            },
            DescriptionAr = r.DescriptionAr,
            UpdatedAt = r.UpdatedAt,
        })];
    }

    public async Task<BusinessSetting?> FindByKeyAsync(string settingKey, CancellationToken ct = default)
    {
        var row = await _db.BusinessSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.SettingKey == settingKey && s.ScopeType == "BUSINESS", ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<BusinessSetting> UpdateValueAsync(string settingKey, string valueText, CancellationToken ct = default)
    {
        var row = await _db.BusinessSettings
            .SingleOrDefaultAsync(s => s.SettingKey == settingKey && s.ScopeType == "BUSINESS", ct)
            ?? throw new InvalidOperationException($"الإعداد «{settingKey}» غير موجود");
        row.ValueText = valueText;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    private static BusinessSetting ToDomain(BusinessSettingRow r) => new()
    {
        SettingId = r.SettingId,
        SettingKey = r.SettingKey,
        ScopeType = r.ScopeType,
        BranchId = r.BranchId,
        ValueText = r.ValueText,
        ValueType = r.ValueType,
        Category = r.Category,
        DecisionRef = r.DecisionRef,
        Confirmation = r.ConfirmationStatus switch
        {
            "OWNER_CONFIRMED" => ConfirmationStatus.OwnerConfirmed,
            "PENDING_OWNER_CONFIRMATION" => ConfirmationStatus.PendingOwnerConfirmation,
            _ => ConfirmationStatus.ApprovedWithSpec,
        },
        DescriptionAr = r.DescriptionAr,
        UpdatedAt = r.UpdatedAt,
    };
}

public sealed class BranchRepository : IBranchRepository
{
    private readonly WeraDbContext _db;

    public BranchRepository(WeraDbContext db) => _db = db;

    public async Task<IReadOnlyList<Branch>> ListActiveBranchesAsync(CancellationToken ct = default)
    {
        var rows = await _db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code).ToListAsync(ct);
        return [.. rows.Select(r => new Branch(r.BranchId, r.Code, r.NameAr, r.Timezone, r.IsActive))];
    }

    public async Task<IReadOnlyList<Station>> ListStationsAsync(long branchId, bool activeOnly, CancellationToken ct = default)
    {
        var query = _db.Stations.AsNoTracking().Where(s => s.BranchId == branchId);
        if (activeOnly)
        {
            query = query.Where(s => s.IsActive);
        }
        var rows = await query.OrderBy(s => s.Code).ToListAsync(ct);
        return [.. rows.Select(r => new Station(r.StationId, r.BranchId, r.Code, r.NameAr, r.IsPos, r.IsActive))];
    }
}

/// <summary>كاتب التدقيق — audit.audit_logs لكل عملية حساسة (V008).</summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly WeraDbContext _db;

    public AuditWriter(WeraDbContext db) => _db = db;

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _db.AuditLogs.Add(new AuditLogRow
        {
            OccurredAt = DateTimeOffset.UtcNow,
            UserId = entry.UserId ?? 0,
            RoleCode = entry.RoleCode,
            StationId = entry.StationId,
            BranchId = entry.BranchId,
            EntitySchema = entry.EntitySchema,
            EntityId = entry.EntityId,
            Action = entry.Action,
            Reason = entry.Reason,
            Result = entry.Result,
            CorrelationId = entry.CorrelationId,
        });
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>قراءة أحداث الدخول من سجل التدقيق — أحدث أولاً (استعلام قراءة فقط).</summary>
public sealed class AuditReadRepository : WeraPro.Application.Audit.IAuditReadRepository
{
    private readonly WeraDbContext _db;

    public AuditReadRepository(WeraDbContext db) => _db = db;

    public async Task<IReadOnlyList<WeraPro.Application.Audit.LoginEvent>> GetRecentLoginEventsAsync(int limit, CancellationToken ct = default)
    {
        var events = await (
            from a in _db.AuditLogs.AsNoTracking()
            join u in _db.Users.AsNoTracking() on a.UserId equals u.UserId into users
            from u in users.DefaultIfEmpty()
            where a.Action == "LOGIN"
            orderby a.OccurredAt descending
            select new WeraPro.Application.Audit.LoginEvent(
                a.OccurredAt,
                u != null ? u.DisplayNameAr : null,
                a.Action,
                a.Result,
                a.Reason,
                a.CorrelationId))
            .Take(limit)
            .ToListAsync(ct);
        return events;
    }
}
