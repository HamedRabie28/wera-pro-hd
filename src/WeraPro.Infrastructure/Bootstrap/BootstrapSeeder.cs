namespace WeraPro.Infrastructure.Bootstrap;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeraPro.Application.Abstractions;
using WeraPro.Infrastructure.Persistence;
using WeraPro.Infrastructure.Persistence.Entities;
using TreasuryAccountEntity = WeraPro.Infrastructure.Persistence.Entities.TreasuryAccountRow;

/// <summary>خيارات تهيئة الإقلاع الأول — القسم «Bootstrap» في الإعدادات/البيئة.</summary>
public sealed record BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>اسم دخول حساب المالك الذي يُنشأ عند أول إقلاع على قاعدة فارغة.</summary>
    public string OwnerUsername { get; set; } = "owner";

    /// <summary>
    /// كلمة مرور المالك الافتراضية — تُقرأ من البيئة BOOTSTRAP__OWNERPASSWORD في الإنتاج.
    /// سياسة كلمة المرور: 8 أحرف على الأقل ورقم واحد على الأقل.
    /// </summary>
    public string OwnerPassword { get; set; } = "Wera@2026";

    /// <summary>الاسم العربي المعروض لحساب المالك.</summary>
    public string OwnerDisplayName { get; set; } = "المالك";
}

/// <summary>
/// مُهيّئ الإقلاع الأول (إصلاح جوهري 2026-11-11): تثبيت نظيف = قاعدة بلا مستخدمين ولا
/// فروع — فلا يستطيع أحد الدخول وتبقى كل الصفحات فارغة. هذا المكوّن يُنشئ الحد الأدنى
/// التشغيلي عند أول إقلاع فقط (لا يمس بيانات قائمة أبداً):
///   1) الفرع الرئيسي (MAIN) إن لم يوجد أي فرع،
///   2) محطة نقاط البيع الأولى (POS-1) للفرع الرئيسي،
///   3) المخزن الرئيسي (MAIN) إن لم يوجد أي مخزن،
///   4) حساب المالك بدور OWNER بكلمة مرور موثقة قابلة للتغيير من صفحة المستخدمين.
/// كل خطوة تُسجل في سجل التدقيق (ops.audit_logs) وسجل التطبيق — دون طباعة كلمة المرور.
/// </summary>
public static partial class BootstrapSeeder
{
    public static async Task<BootstrapOutcome> SeedAsync(
        WeraDbContext db,
        IPasswordHasher hasher,
        IOptions<BootstrapOptions> options,
        ILogger logger,
        CancellationToken ct = default)
    {
        var opts = options.Value;
        var outcome = new BootstrapOutcome(false, false, false, false, 0);

        // ── 1) الفرع الرئيسي ──
        var branchId = 0L;
        if (!await db.Branches.AsNoTracking().AnyAsync(ct))
        {
            var branch = new BranchRow
            {
                Code = "MAIN",
                NameAr = "الفرع الرئيسي",
                NameEn = "Main Branch",
                Timezone = "Africa/Cairo",
            };
            db.Branches.Add(branch);
            await db.SaveChangesAsync(ct);
            branchId = branch.BranchId;
            outcome = outcome with { SeededBranch = true };
            LogSeededBranch(logger, branchId);
        }
        else
        {
            branchId = await db.Branches.AsNoTracking().OrderBy(b => b.BranchId).Select(b => b.BranchId).FirstAsync(ct);
        }

        // ── 2) محطة POS الأولى ──
        if (!await db.Stations.AsNoTracking().AnyAsync(s => s.BranchId == branchId, ct))
        {
            var station = new StationRow { BranchId = branchId, Code = "POS-1", NameAr = "محطة نقاط البيع 1" };
            db.Stations.Add(station);
            await db.SaveChangesAsync(ct);
            outcome = outcome with { SeededStation = true };
            LogSeededStation(logger, branchId);
        }

        // ── 3) المخزن الرئيسي ──
        if (!await db.Warehouses.AsNoTracking().AnyAsync(ct))
        {
            var warehouse = new WarehouseRow
            {
                BranchId = branchId,
                Code = "MAIN",
                NameAr = "المخزن الرئيسي",
                WhType = "MAIN",
            };
            db.Warehouses.Add(warehouse);
            await db.SaveChangesAsync(ct);
            outcome = outcome with { SeededWarehouse = true };
            LogSeededWarehouse(logger, branchId);
        }

        // ── 4) حساب المالك ──
        if (!await db.Users.AsNoTracking().AnyAsync(ct))
        {
            var username = opts.OwnerUsername.Trim();
            var password = opts.OwnerPassword;
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || !password.Any(char.IsDigit))
            {
                throw new InvalidOperationException(
                    "Bootstrap: كلمة مرور المالك لا تحترم السياسة (8 أحرف ورقم على الأقل) — عدّل BOOTSTRAP__OWNERPASSWORD");
            }

            var ownerRole = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Code == "OWNER", ct)
                ?? throw new InvalidOperationException("Bootstrap: دور OWNER غير مزروع — راجع ترحيل V009");

            var user = new UserRow
            {
                Username = username,
                DisplayNameAr = string.IsNullOrWhiteSpace(opts.OwnerDisplayName) ? "المالك" : opts.OwnerDisplayName.Trim(),
                PasswordHash = hasher.Hash(password),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            db.Set<UserRoleRow>().Add(new UserRoleRow { UserId = user.UserId, RoleId = ownerRole.RoleId, BranchId = null });
            await db.SaveChangesAsync(ct);

            db.AuditLogs.Add(new AuditLogRow
            {
                UserId = user.UserId,
                RoleCode = "OWNER",
                StationId = null,
                BranchId = branchId,
                EntitySchema = "core.users",
                EntityId = user.UserId,
                Action = "USER_CREATE",
                Result = "SUCCESS",
                Reason = "تهيئة الإقلاع الأول: حساب المالك بدور OWNER (كل الفروع)",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);

            outcome = outcome with { SeededOwner = true, OwnerUserId = user.UserId };
            LogSeededOwner(logger, username);
        }

        // ── 5) درج محطة نقاط البيع (إصلاح 2026-11-11): بلا درج لا يُرحّل البيع أثر خزينة —
        // البذر القديم (V009) يزرع الخزينة والبنك فقط. درج لكل محطة (V019/G8: الحقيقة الفيزيائية).
        var stationId = await db.Stations.AsNoTracking()
            .Where(s => s.BranchId == branchId)
            .OrderBy(s => s.StationId)
            .Select(s => (long?)s.StationId)
            .FirstOrDefaultAsync(ct);
        if (stationId is null)
        {
            return outcome;
        }
        var drawerStationId = stationId.Value;
        if (!await db.Set<TreasuryAccountEntity>().AsNoTracking()
                .AnyAsync(t => t.AccountKind == "CASH_DRAWER" && t.StationId == drawerStationId, ct))
        {
            db.Set<TreasuryAccountEntity>().Add(new TreasuryAccountEntity
            {
                Code = $"DRAWER-ST{drawerStationId}",
                NameAr = "درج محطة نقاط البيع 1",
                AccountKind = "CASH_DRAWER",
                BranchId = branchId,
                StationId = drawerStationId,
                Currency = "EGP",
                IsActive = true,
            });
            await db.SaveChangesAsync(ct);
            outcome = outcome with { SeededDrawer = true };
            LogSeededDrawer(logger, drawerStationId);
        }

        return outcome;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "التهيئة الأولى: أُنشئ الفرع الرئيسي (MAIN) — id={BranchId}")]
    private static partial void LogSeededBranch(ILogger logger, long branchId);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "التهيئة الأولى: أُنشئت محطة نقاط البيع الأولى (POS-1) للفرع {BranchId}")]
    private static partial void LogSeededStation(ILogger logger, long branchId);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "التهيئة الأولى: أُنشئ المخزن الرئيسي (MAIN) للفرع {BranchId}")]
    private static partial void LogSeededWarehouse(ILogger logger, long branchId);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "التهيئة الأولى: أُنشئ حساب المالك «{Username}» بدور OWNER — غيّر كلمة المرور من صفحة المستخدمين بعد أول دخول")]
    private static partial void LogSeededOwner(ILogger logger, string username);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "التهيئة الأولى: أُنشئ درج المحطة {StationId} (CASH_DRAWER) — ترحيل أثر الخزينة للبيع")]
    private static partial void LogSeededDrawer(ILogger logger, long stationId);
}

/// <summary>نتيجة التهيئة — لأغراض السجل والاختبار.</summary>
public sealed record BootstrapOutcome(
    bool SeededBranch,
    bool SeededStation,
    bool SeededWarehouse,
    bool SeededOwner,
    long OwnerUserId,
    bool SeededDrawer = false);
