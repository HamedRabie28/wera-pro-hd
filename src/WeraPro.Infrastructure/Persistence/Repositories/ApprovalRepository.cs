namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using WeraPro.Application.Inventory;
using WeraPro.Domain.SharedKernel;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>
/// مستودع الاعتمادات (core.approvals — D-03 ب): البت من غير طالب العملية
/// (قيد V003 approvals_no_self) وقرار معلق فقط — قيدان يفحصان داخل المعاملة.
/// </summary>
public sealed class ApprovalRepository : IApprovalRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public ApprovalRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ApprovalInfo> CreateAsync(ApprovalDraft draft, CancellationToken ct = default)
    {
        var row = new ApprovalRow
        {
            EntityType = draft.EntityType,
            EntityId = draft.EntityId,
            RequestedBy = draft.RequestedBy,
            Decision = "PENDING",
            Reason = draft.ReasonAr,
            RequestedAt = _clock.GetUtcNow(),
        };
        _db.Approvals.Add(row);
        await _db.SaveChangesAsync(ct);
        var requester = await _db.Users.Where(u => u.UserId == draft.RequestedBy).Select(u => u.Username).FirstAsync(ct);
        return new ApprovalInfo(row.ApprovalId, row.EntityType, row.EntityId, row.RequestedBy, requester,
            row.Decision, null, null, row.Reason, row.RequestedAt, null);
    }

    public async Task<Result<ApprovalInfo>> DecideAsync(long approvalId, string decision, string reasonAr, long decidedBy, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var row = await _db.Approvals
                    .FromSql($"SELECT * FROM core.approvals WHERE approval_id = {approvalId} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
                if (row is null)
                {
                    throw new ApprovalPolicyException("APPROVAL_NOT_FOUND", "طلب الاعتماد غير موجود");
                }
                if (row.Decision != "PENDING")
                {
                    throw new ApprovalPolicyException("APPROVAL_ALREADY_DECIDED", $"الطلب مبتوت فيه سلفاً ({row.Decision})");
                }
                if (row.RequestedBy == decidedBy)
                {
                    throw new ApprovalPolicyException("APPROVAL_SELF_DECISION", "لا يبت طالب العملية في طلبه نفسه (قيد V003 approvals_no_self)");
                }

                row.Decision = decision;
                row.DecidedBy = decidedBy;
                row.Reason = reasonAr;
                row.DecidedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var decider = await _db.Users.Where(u => u.UserId == decidedBy).Select(u => u.Username).FirstAsync(ct);
                var requester = await _db.Users.Where(u => u.UserId == row.RequestedBy).Select(u => u.Username).FirstAsync(ct);
                return Result.Ok(new ApprovalInfo(
                    row.ApprovalId, row.EntityType, row.EntityId, row.RequestedBy, requester,
                    row.Decision, row.DecidedBy, decider, row.Reason, row.RequestedAt, row.DecidedAt));
            });
        }
        catch (ApprovalPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            var raw = pg.MessageText ?? string.Empty;
            if (raw.Contains("approvals_no_self"))
            {
                throw new ApprovalPolicyException("APPROVAL_SELF_DECISION", "لا يبت طالب العملية في طلبه نفسه (قيد V003)");
            }
            throw new ApprovalPolicyException("APPROVAL_GUARD", "البت يخالف قيود الاعتمادات");
        }
    }

    public async Task<IReadOnlyList<ApprovalInfo>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query =
            from a in _db.Approvals.AsNoTracking()
            join requester in _db.Users.AsNoTracking() on a.RequestedBy equals requester.UserId
            into requesters
            from requester in requesters.DefaultIfEmpty()
            where status == null || a.Decision == status
            orderby a.ApprovalId descending
            select new
            {
                a,
                RequestedByName = requester != null ? requester.Username : string.Empty,
            };
        var rows = await query.ToListAsync(ct);
        var result = new List<ApprovalInfo>();
        foreach (var r in rows)
        {
            var decider = r.a.DecidedBy is { } did
                ? await _db.Users.Where(u => u.UserId == did).Select(u => u.Username).FirstOrDefaultAsync(ct)
                : null;
            result.Add(new ApprovalInfo(
                r.a.ApprovalId, r.a.EntityType, r.a.EntityId, r.a.RequestedBy, r.RequestedByName,
                r.a.Decision, r.a.DecidedBy, decider, r.a.Reason, r.a.RequestedAt, r.a.DecidedAt));
        }
        return result;
    }

    public async Task<ApprovalInfo?> FindAsync(long approvalId, CancellationToken ct = default)
    {
        var row = await _db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.ApprovalId == approvalId, ct);
        return row is null ? null : ToInfo(row, username: null);
    }

    private static ApprovalInfo ToInfo(ApprovalRow a, string? username) => new(
        a.ApprovalId, a.EntityType, a.EntityId, a.RequestedBy, username ?? "-", a.Decision,
        a.DecidedBy, null, a.Reason, a.RequestedAt, a.DecidedAt);
}
