namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using WeraPro.Application.Finance;
using WeraPro.Domain.Finance;
using WeraPro.Domain.SharedKernel;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>
/// مستودع G8 المالي (§14/§13): الخزائن والسندات والمصروفات ورأس المال وإغلاق الوردية —
/// كل ترحيل ذري بمعاملة استراتيجية، وخرق القيود (مشغلات V019/قيود V006) يُحوَّل إلى
/// FinancePolicyException برمز موثق لا استثناء خام.
/// </summary>
public sealed class FinanceRepository(WeraDbContext db, TimeProvider clock) : IFinanceRepository
{
    private readonly WeraDbContext _db = db;
    private readonly TimeProvider _clock = clock;

    // ═══════════════ الخزائن (§14.2) ═══════════════════════════════════════

    public async Task<IReadOnlyList<TreasuryAccountData>> ListTreasuryAccountsAsync(bool activeOnly, CancellationToken ct = default) =>
        await _db.TreasuryAccounts.AsNoTracking()
            .Where(a => !activeOnly || a.IsActive)
            .OrderBy(a => a.AccountKind).ThenBy(a => a.Code)
            .Select(a => new TreasuryAccountData(
                a.TreasuryAccountId, a.Code, a.NameAr, a.AccountKind, a.BranchId, a.StationId, a.IsActive,
                _db.TreasuryTransactions.Where(t => t.TreasuryAccountId == a.TreasuryAccountId)
                    .Sum(t => (decimal?)t.Amount * t.Direction) ?? 0m))
            .ToListAsync(ct);

    public async Task<TreasuryAccountData?> GetTreasuryAccountAsync(long treasuryAccountId, CancellationToken ct = default) =>
        await _db.TreasuryAccounts.AsNoTracking()
            .Where(a => a.TreasuryAccountId == treasuryAccountId)
            .Select(a => new TreasuryAccountData(
                a.TreasuryAccountId, a.Code, a.NameAr, a.AccountKind, a.BranchId, a.StationId, a.IsActive,
                _db.TreasuryTransactions.Where(t => t.TreasuryAccountId == a.TreasuryAccountId)
                    .Sum(t => (decimal?)t.Amount * t.Direction) ?? 0m))
            .FirstOrDefaultAsync(ct);

    public async Task<TreasuryAccountData?> FindDrawerAccountAsync(long stationId, CancellationToken ct = default)
    {
        // درج المحطة أولاً (الحقيقة الفيزيائية) ثم درج فرعها (توافق خلفي G7)
        const string sql = """
            SELECT a.treasury_account_id, a.code, a.name_ar, a.account_kind, a.branch_id, a.station_id, a.is_active,
                   COALESCE((SELECT SUM(t.amount * t.direction) FROM finance.treasury_transactions t
                              WHERE t.treasury_account_id = a.treasury_account_id), 0) AS balance
              FROM finance.treasury_accounts a
              JOIN core.stations s ON s.station_id = @station
             WHERE a.is_active AND a.account_kind = 'CASH_DRAWER'
               AND (a.station_id = @station OR (a.station_id IS NULL AND a.branch_id = s.branch_id))
             ORDER BY (a.station_id IS NOT NULL) DESC, a.treasury_account_id
             LIMIT 1
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("station", stationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }
        return new TreasuryAccountData(
            r.GetInt64(r.GetOrdinal("treasury_account_id")), r.GetString(r.GetOrdinal("code")),
            r.GetString(r.GetOrdinal("name_ar")), r.GetString(r.GetOrdinal("account_kind")),
            r.IsDBNull(r.GetOrdinal("branch_id")) ? null : r.GetInt64(r.GetOrdinal("branch_id")),
            r.IsDBNull(r.GetOrdinal("station_id")) ? null : r.GetInt64(r.GetOrdinal("station_id")),
            r.GetBoolean(r.GetOrdinal("is_active")),
            GetDecimal(r, "balance"));
    }

    public async Task<Result<TreasuryAccountData>> CreateTreasuryAccountAsync(
        string code, string nameAr, string accountKind, long? branchId, long? stationId, CancellationToken ct = default)
    {
        try
        {
            var row = new TreasuryAccountRow
            {
                Code = code,
                NameAr = nameAr,
                AccountKind = accountKind,
                BranchId = branchId,
                StationId = stationId,
                IsActive = true,
            };
            _db.TreasuryAccounts.Add(row);
            await _db.SaveChangesAsync(ct);
            return Result.Ok(new TreasuryAccountData(
                row.TreasuryAccountId, row.Code, row.NameAr, row.AccountKind, row.BranchId, row.StationId, true, 0m));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Map(pg, $"TREASURY_{pg.SqlState}");
        }
    }

    public async Task<(IReadOnlyList<TreasuryTxnData> Items, int TotalCount)> ListTreasuryTransactionsAsync(
        long? treasuryAccountId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int page, int pageSize, CancellationToken ct = default)
    {
        var query = from t in _db.TreasuryTransactions.AsNoTracking()
                    join a in _db.TreasuryAccounts.AsNoTracking() on t.TreasuryAccountId equals a.TreasuryAccountId
                    join u in _db.Users.AsNoTracking() on t.CreatedBy equals u.UserId
                    where (treasuryAccountId == null || t.TreasuryAccountId == treasuryAccountId)
                       && (fromUtc == null || t.OccurredAt >= fromUtc)
                       && (toUtc == null || t.OccurredAt <= toUtc)
                    orderby t.OccurredAt descending, t.TxnId descending
                    select new { total = 0, t, a, u };
        var total = await query.CountAsync(ct);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (rows.Select(x => new TreasuryTxnData(
            x.t.TxnId, x.t.TreasuryAccountId, x.a.Code, x.a.NameAr, x.a.AccountKind,
            x.t.Direction, x.t.Amount, x.t.SourceDocType, x.t.SourceDocId, x.t.ShiftId,
            x.t.Reason, x.t.OccurredAt, x.u.DisplayNameAr)).ToList(), total);
    }

    // ═══════════════ السندات (§14.2/§13) ═══════════════════════════════════

    public async Task<string> NextVoucherNumberAsync(string voucherType, CancellationToken ct = default)
    {
        var seq = await _db.Database.SqlQuery<long>($"SELECT nextval('finance.voucher_number_seq') AS \"Value\"").FirstAsync(ct);
        return $"{VoucherRules.NumberPrefix(voucherType)}-{seq}";
    }

    public async Task<Result<VoucherData>> CreateVoucherAsync(VoucherCreatePlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = new FinanceVoucherRow
                {
                    VoucherNumber = plan.VoucherNumber,
                    VoucherType = plan.VoucherType,
                    TreasuryAccountId = plan.TreasuryAccountId,
                    CounterTreasuryAccountId = plan.CounterTreasuryAccountId,
                    Amount = plan.Amount,
                    CounterpartyType = plan.CounterpartyType,
                    CounterpartyId = plan.CounterpartyId,
                    Reason = plan.Reason,
                    Status = "DRAFT",
                    CreatedBy = plan.CreatedByUserId,
                    CreatedAt = _clock.GetUtcNow(),
                    ShiftId = plan.ShiftId,
                };
                _db.FinanceVouchers.Add(row);
                await _db.SaveChangesAsync(ct);

                foreach (var allocation in plan.Allocations)
                {
                    _db.VoucherAllocations.Add(new VoucherAllocationRow
                    {
                        VoucherId = row.VoucherId,
                        InvoiceId = allocation.InvoiceId,
                        Amount = allocation.Amount,
                        CreatedAt = _clock.GetUtcNow(),
                    });
                }
                if (plan.Allocations.Count > 0)
                {
                    await _db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct);
                var data = await GetVoucherAsync(row.VoucherId, ct);
                return Result.Ok(data!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"VOUCHER_{pg.SqlState}");
            }
        });
    }

    public async Task<Result<VoucherData>> PostVoucherAsync(VoucherPostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = await _db.FinanceVouchers
                    .FromSql($"SELECT * FROM finance.vouchers WHERE voucher_id = {plan.VoucherId} FOR UPDATE")
                    .FirstAsync(ct);
                if (row.Status != "DRAFT")
                {
                    throw new FinancePolicyException("VOUCHER_NOT_DRAFT", $"السند في حالة {row.Status} — لا ترحيل مكرر (§14.2)");
                }

                var now = plan.PostedAt;
                // أثر واحد للقبض/الصرف وحركتان متقابلتان للتحويل — من قواعد المجال (§14.2)
                foreach (var leg in VoucherRules.PostingLegs(row.VoucherType, row.Amount, row.TreasuryAccountId, row.CounterTreasuryAccountId))
                {
                    _db.TreasuryTransactions.Add(new TreasuryTxnRow
                    {
                        TreasuryAccountId = leg.TreasuryAccountId,
                        Direction = (short)leg.Direction,
                        Amount = leg.Amount,
                        SourceDocType = "finance.voucher",
                        SourceDocId = row.VoucherId,
                        Reason = LegReason(row),
                        OccurredAt = now,
                        CreatedBy = plan.UserId,
                        BranchId = await BranchIdOfAsync(row.TreasuryAccountId, plan.FallbackBranchId, ct),
                        ShiftId = plan.ShiftId,     // سياق الوردية لعمليات الدرج — المتوقع يطابق كل حركاته
                    });
                }
                row.Status = "POSTED";
                row.PostedAt = now;
                row.ShiftId = plan.ShiftId;   // سياق الوردية وقت الترحيل — مطابق لأرجل الخزينة
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);   // المشغل المؤجل يتحقق من الأرجل عند الالتزام — لا ترحيل بأثر ناقص
                var data = await GetVoucherAsync(row.VoucherId, ct);
                return Result.Ok(data!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"VOUCHER_{pg.SqlState}");
            }
        });
    }

    private static string LegReason(FinanceVoucherRow v) => v.VoucherType switch
    {
        "RECEIPT" => $"سند قبض {v.VoucherNumber}",
        "DISBURSEMENT" => $"سند صرف {v.VoucherNumber}",
        "TRANSFER" => $"تحويل بين خزينتين {v.VoucherNumber} — لا يولد إيراداً ولا مصروفاً (§14.2)",
        _ => v.VoucherNumber,
    };

    public async Task<Result<VoucherData>> CancelVoucherDraftAsync(long voucherId, long userId, string reasonAr, CancellationToken ct = default)
    {
        var row = await _db.FinanceVouchers
            .FromSql($"SELECT * FROM finance.vouchers WHERE voucher_id = {voucherId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Result.Fail<VoucherData>(new("VOUCHER_NOT_FOUND", "السند غير موجود"));
        }
        if (row.Status != "DRAFT")
        {
            return Result.Fail<VoucherData>(new("VOUCHER_NOT_DRAFT", "لا يُلغى إلا سند مسودة — المرحّل نهائي (§14.2)"));
        }
        try
        {
            row.Status = "CANCELLED";
            await _db.SaveChangesAsync(ct);
            var data = await GetVoucherAsync(voucherId, ct);
            return Result.Ok(data!);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Map(pg, $"VOUCHER_{pg.SqlState}");
        }
    }

    public async Task<VoucherData?> GetVoucherAsync(long voucherId, CancellationToken ct = default)
    {
        var row = await (from v in _db.FinanceVouchers.AsNoTracking()
                         join a in _db.TreasuryAccounts.AsNoTracking() on v.TreasuryAccountId equals a.TreasuryAccountId
                         join ca in _db.TreasuryAccounts.AsNoTracking() on v.CounterTreasuryAccountId equals ca.TreasuryAccountId into caj
                         from ca in caj.DefaultIfEmpty()
                         where v.VoucherId == voucherId
                         select new { v, a, ca }).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        string? counterpartyName = row.v.CounterpartyType switch
        {
            "CUSTOMER" => row.v.CounterpartyId is null ? null
                : await _db.Customers.AsNoTracking().Where(c => c.CustomerId == row.v.CounterpartyId).Select(c => c.NameAr).FirstOrDefaultAsync(ct),
            "SUPPLIER" => row.v.CounterpartyId is null ? null
                : await _db.Suppliers.AsNoTracking().Where(s => s.SupplierId == row.v.CounterpartyId).Select(s => s.NameAr).FirstOrDefaultAsync(ct),
            "EMPLOYEE" => row.v.CounterpartyId is null ? null
                : await _db.Users.AsNoTracking().Where(u => u.UserId == row.v.CounterpartyId).Select(u => u.DisplayNameAr).FirstOrDefaultAsync(ct),
            "OWNER" => "المالك",
            "OTHER" => "طرف آخر",
            _ => null,
        };
        var allocations = await (from va in _db.VoucherAllocations.AsNoTracking()
                                 join i in _db.SalesInvoices.AsNoTracking() on va.InvoiceId equals i.InvoiceId
                                 where va.VoucherId == voucherId
                                 select new VoucherAllocationData(va.InvoiceId, i.InvoiceNumber, va.Amount, 0m)).ToListAsync(ct);
        return new VoucherData(
            row.v.VoucherId, row.v.VoucherNumber, row.v.VoucherType, row.v.Status,
            row.v.TreasuryAccountId, row.a.Code, row.a.NameAr,
            row.v.CounterTreasuryAccountId, row.ca?.Code,
            row.v.Amount, row.v.CounterpartyType, row.v.CounterpartyId, counterpartyName,
            row.v.Reason, row.v.ShiftId, row.v.CreatedAt, row.v.PostedAt, allocations);
    }

    public async Task<(IReadOnlyList<VoucherData> Items, int TotalCount)> ListVouchersAsync(
        string? voucherType, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.FinanceVouchers.AsNoTracking()
            .Where(v => (voucherType == null || v.VoucherType == voucherType)
                     && (status == null || v.Status == status));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(v => v.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var result = new List<VoucherData>();
        foreach (var v in rows)
        {
            var data = await GetVoucherAsync(v.VoucherId, ct);
            if (data is not null)
            {
                result.Add(data);
            }
        }
        return (result, total);
    }

    public async Task<IReadOnlyList<CustomerReceivableData>> GetCustomerReceivablesAsync(long customerId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT i.invoice_id, i.invoice_number, i.posted_at, i.status,
                   (i.gross_total - i.line_discount_total - i.invoice_discount) AS due_amount,
                   COALESCE((SELECT SUM(va.amount) FROM finance.voucher_allocations va
                              JOIN finance.vouchers v ON v.voucher_id = va.voucher_id
                             WHERE va.invoice_id = i.invoice_id AND v.status = 'POSTED'), 0) AS allocated,
                   i.returned_total
              FROM sales.invoices i
             WHERE i.customer_id = @cust
               AND i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
               AND EXISTS (SELECT 1 FROM sales.payments p
                            WHERE p.invoice_id = i.invoice_id AND p.payment_method = 'CREDIT'
                              AND p.status IN ('CAPTURED','SETTLED'))
               AND (i.gross_total - i.line_discount_total - i.invoice_discount) - i.returned_total
                 - COALESCE((SELECT SUM(va.amount) FROM finance.voucher_allocations va
                              JOIN finance.vouchers v ON v.voucher_id = va.voucher_id
                             WHERE va.invoice_id = i.invoice_id AND v.status = 'POSTED'), 0) > 0.00005
             ORDER BY i.posted_at
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("cust", customerId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<CustomerReceivableData>();
        while (await r.ReadAsync(ct))
        {
            // المستحق من أعمدة V005 (إجمالي − خصوم) والمتبقي يخصم المرتجعات والتوزيعات المرحلة (§13)
            var due = GetDecimal(r, "due_amount");
            var allocated = GetDecimal(r, "allocated");
            var returned = GetDecimal(r, "returned_total");
            result.Add(new CustomerReceivableData(
                r.GetInt64(r.GetOrdinal("invoice_id")), r.GetString(r.GetOrdinal("invoice_number")),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("posted_at")), r.GetString(r.GetOrdinal("status")),
                due, allocated, due - returned - allocated));
        }
        return result;
    }

    public async Task<bool> CustomerExistsAsync(long customerId, CancellationToken ct = default) =>
        await _db.Customers.AsNoTracking().AnyAsync(c => c.CustomerId == customerId, ct);

    public async Task<bool> SupplierExistsAsync(long supplierId, CancellationToken ct = default) =>
        await _db.Suppliers.AsNoTracking().AnyAsync(s => s.SupplierId == supplierId, ct);

    public async Task<bool> UserExistsAsync(long userId, CancellationToken ct = default) =>
        await _db.Users.AsNoTracking().AnyAsync(u => u.UserId == userId, ct);

    // ═══════════════ المصروفات (§14.4) ═════════════════════════════════════

    public async Task<string> NextExpenseNumberAsync(CancellationToken ct = default)
    {
        var seq = await _db.Database.SqlQuery<long>($"SELECT nextval('finance.expense_number_seq') AS \"Value\"").FirstAsync(ct);
        return $"EX-{seq}";
    }

    public async Task<Result<ExpenseData>> CreateExpenseAsync(ExpenseCreatePlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = new ExpenseRow
                {
                    ExpenseNumber = plan.ExpenseNumber,
                    CategoryId = plan.CategoryId,
                    BranchId = plan.BranchId,
                    ExpenseDate = plan.ExpenseDate,
                    Amount = plan.Amount,
                    TaxAmount = plan.TaxAmount,
                    PaymentMethod = plan.PaymentMethod,
                    TreasuryAccountId = plan.TreasuryAccountId,
                    Beneficiary = plan.Beneficiary,
                    CostCenter = plan.CostCenter,
                    Status = "DRAFT",
                    Notes = plan.Notes,
                    CreatedBy = plan.CreatedByUserId,
                    CreatedAt = _clock.GetUtcNow(),
                    ShiftId = plan.ShiftId,
                };
                _db.Expenses.Add(row);
                await _db.SaveChangesAsync(ct);

                // مصروف الوردية المدفوع من الدرج (القاعدة الموثقة G8): يُرحَّل فور إنشائه —
                // المال خرج فعلاً والدفتر يطابق الواقع (§14.1/§32)، وأثر مالي واحد
                if (plan.PostDirectly)
                {
                    await PostExpenseWithinTxAsync(row, plan.CreatedByUserId, _clock.GetUtcNow(), ct);
                }
                await tx.CommitAsync(ct);
                var data = await GetExpenseAsync(row.ExpenseId, ct);
                return Result.Ok(data!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"EXPENSE_{pg.SqlState}");
            }
        });
    }

    public async Task<Result<ExpenseData>> ApproveExpenseAsync(long expenseId, long approverUserId, string? reasonAr, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = await _db.Expenses
                    .FromSql($"SELECT * FROM finance.expenses WHERE expense_id = {expenseId} FOR UPDATE")
                    .FirstAsync(ct);
                if (row.Status != "DRAFT")
                {
                    throw new FinancePolicyException("EXPENSE_NOT_DRAFT", "الاعتماد لمصروف مسودة فقط");
                }
                // اعتماد ثنائي موثق (core.approvals — قيد no_self: المعتمد ≠ المنشئ)
                var approval = new ApprovalRow
                {
                    EntityType = "finance.expense",
                    EntityId = expenseId,
                    RequestedBy = row.CreatedBy,
                    DecidedBy = approverUserId,
                    Decision = "APPROVED",
                    Reason = string.IsNullOrWhiteSpace(reasonAr) ? "اعتماد مصروف" : reasonAr.Trim(),
                    RequestedAt = _clock.GetUtcNow(),
                    DecidedAt = _clock.GetUtcNow(),
                };
                _db.Approvals.Add(approval);
                await _db.SaveChangesAsync(ct);
                row.Status = "APPROVED";
                row.ApprovalId = approval.ApprovalId;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                var data = await GetExpenseAsync(expenseId, ct);
                return Result.Ok(data!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                if (pg.SqlState == "23514" && pg.MessageText.Contains("approvals_no_self"))
                {
                    throw new FinancePolicyException("EXPENSE_SELF_APPROVAL_DENIED",
                        "لا يعتمد المنفذ مصروفه بنفسه — الاعتماد الثنائي من دور آخر (قيد no_self)");
                }
                throw Map(pg, $"EXPENSE_{pg.SqlState}");
            }
        });
    }

    public async Task<Result<ExpenseData>> PostExpenseAsync(ExpensePostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = await _db.Expenses
                    .FromSql($"SELECT * FROM finance.expenses WHERE expense_id = {plan.ExpenseId} FOR UPDATE")
                    .FirstAsync(ct);
                if (row.Status is not ("DRAFT" or "APPROVED"))
                {
                    throw new FinancePolicyException("EXPENSE_ALREADY_POSTED", "المصروف ليس قابلاً للترحيل (حالته ليست مسودة/معتمداً) — لا ترحيل مكرر (§14.4)");
                }
                await PostExpenseWithinTxAsync(row, plan.UserId, plan.PostedAt, ct);
                await tx.CommitAsync(ct);
                var data = await GetExpenseAsync(plan.ExpenseId, ct);
                return Result.Ok(data!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"EXPENSE_{pg.SqlState}");
            }
        });
    }

    private async Task PostExpenseWithinTxAsync(ExpenseRow row, long userId, DateTimeOffset postedAt, CancellationToken ct)
    {
        // السداد يوجب خزينة — لا ترحيل مصروف بلا وجهة دفع (§14.4)
        if (row.TreasuryAccountId is null)
        {
            throw new FinancePolicyException("EXPENSE_TREASURY_REQUIRED", "السداد يستلزم حساب خزينة — لا ترحيل مصروف بلا وجهة دفع");
        }
        _db.TreasuryTransactions.Add(new TreasuryTxnRow
        {
            TreasuryAccountId = row.TreasuryAccountId.Value,
            Direction = -1,
            Amount = row.Amount + row.TaxAmount,
            SourceDocType = "finance.expense",
            SourceDocId = row.ExpenseId,
            Reason = $"مصروف {row.ExpenseNumber}" + (string.IsNullOrWhiteSpace(row.Beneficiary) ? "" : $" — {row.Beneficiary}"),
            OccurredAt = postedAt,
            CreatedBy = userId,
            BranchId = row.BranchId,
            ShiftId = row.ShiftId,
        });
        row.Status = "POSTED";
        row.PostedAt = postedAt;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Result<ExpenseData>> CancelExpenseAsync(long expenseId, long userId, string reasonAr, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = await _db.Expenses
                    .FromSql($"SELECT * FROM finance.expenses WHERE expense_id = {expenseId} FOR UPDATE")
                    .FirstAsync(ct);
                if (row.Status == "CANCELLED")
                {
                    throw new FinancePolicyException("EXPENSE_ALREADY_CANCELLED", "المصروف ملغى سلفاً");
                }
                if (row.Status == "POSTED")
                {
                    // إلغاء المرحّل = عكس موثق (§14.4): حركة مقابل بمصدر مميز — الأثر الصافي صفر والمشغل المؤجل يتحقق
                    _db.TreasuryTransactions.Add(new TreasuryTxnRow
                    {
                        TreasuryAccountId = row.TreasuryAccountId!.Value,
                        Direction = 1,
                        Amount = row.Amount + row.TaxAmount,
                        SourceDocType = "finance.expense.reversal",
                        SourceDocId = row.ExpenseId,
                        Reason = $"عكس إلغاء مصروف مرحّل {row.ExpenseNumber} — {reasonAr}",
                        OccurredAt = _clock.GetUtcNow(),
                        CreatedBy = userId,
                        BranchId = row.BranchId,
                        ShiftId = row.ShiftId,
                    });
                    row.Notes = string.IsNullOrWhiteSpace(row.Notes) ? $"إلغاء: {reasonAr}" : $"{row.Notes} — إلغاء: {reasonAr}";
                }
                row.Status = "CANCELLED";
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                var data = await GetExpenseAsync(expenseId, ct);
                return Result.Ok(data!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"EXPENSE_{pg.SqlState}");
            }
        });
    }

    public async Task<ExpenseData?> GetExpenseAsync(long expenseId, CancellationToken ct = default) =>
        await (from e in _db.Expenses.AsNoTracking()
               join c in _db.ExpenseCategories.AsNoTracking() on e.CategoryId equals c.CategoryId
               join t in _db.TreasuryAccounts.AsNoTracking() on e.TreasuryAccountId equals t.TreasuryAccountId into tj
               from t in tj.DefaultIfEmpty()
               where e.ExpenseId == expenseId
               select new ExpenseData(
                   e.ExpenseId, e.ExpenseNumber, e.Status, e.CategoryId, c.NameAr, e.BranchId, e.ExpenseDate,
                   e.Amount, e.TaxAmount, e.Amount + e.TaxAmount, e.PaymentMethod,
                   e.TreasuryAccountId, t == null ? null : t.Code, t == null ? null : t.NameAr,
                   e.Beneficiary, e.CostCenter, e.ShiftId, e.ApprovalId, e.Notes, e.CreatedBy, e.CreatedAt, e.PostedAt))
              .FirstOrDefaultAsync(ct);

    public async Task<(IReadOnlyList<ExpenseData> Items, int TotalCount)> ListExpensesAsync(
        string? status, long? shiftId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.Expenses.AsNoTracking()
            .Where(e => (status == null || e.Status == status) && (shiftId == null || e.ShiftId == shiftId));
        var total = await query.CountAsync(ct);
        var ids = await query.OrderByDescending(e => e.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => e.ExpenseId).ToListAsync(ct);
        var result = new List<ExpenseData>();
        foreach (var id in ids)
        {
            var data = await GetExpenseAsync(id, ct);
            if (data is not null)
            {
                result.Add(data);
            }
        }
        return (result, total);
    }

    public async Task<IReadOnlyList<ExpenseCategoryData>> ListExpenseCategoriesAsync(bool activeOnly, CancellationToken ct = default) =>
        await _db.ExpenseCategories.AsNoTracking()
            .Where(c => !activeOnly || c.IsActive)
            .OrderBy(c => c.Code)
            .Select(c => new ExpenseCategoryData(c.CategoryId, c.Code, c.NameAr, c.IsActive))
            .ToListAsync(ct);

    public async Task<Result<ExpenseCategoryData>> CreateExpenseCategoryAsync(string code, string nameAr, CancellationToken ct = default)
    {
        try
        {
            var row = new ExpenseCategoryRow { Code = code, NameAr = nameAr, IsActive = true };
            _db.ExpenseCategories.Add(row);
            await _db.SaveChangesAsync(ct);
            return Result.Ok(new ExpenseCategoryData(row.CategoryId, row.Code, row.NameAr, true));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Map(pg, $"EXPENSE_CATEGORY_{pg.SqlState}");
        }
    }

    // ═══════════════ رأس المال (§14.3/D-14) ════════════════════════════════

    public async Task<string> NextCapitalEventNumberAsync(CancellationToken ct = default)
    {
        var seq = await _db.Database.SqlQuery<long>($"SELECT nextval('finance.capital_event_number_seq') AS \"Value\"").FirstAsync(ct);
        return $"CE-{seq}";
    }

    public async Task<Result<CapitalEventData>> CreateCapitalEventAsync(CapitalCreatePlan plan, CancellationToken ct = default)
    {
        try
        {
            var row = new CapitalEventRow
            {
                EventNumber = plan.EventNumber,
                EventType = plan.EventType,
                Amount = plan.Amount,
                TreasuryAccountId = plan.TreasuryAccountId,
                EventDate = plan.EventDate,
                Description = plan.Description,
                Status = "DRAFT",
                ApprovalId = plan.ApprovalId,
                CreatedBy = plan.CreatedByUserId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.CapitalEvents.Add(row);
            await _db.SaveChangesAsync(ct);
            return Result.Ok((await GetCapitalEventAsync(row.CapitalId, ct))!);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            if (pg.SqlState == "23514" && pg.MessageText.Contains("capital_opening_needs_approval"))
            {
                throw new FinancePolicyException("CAPITAL_OPENING_APPROVAL_REQUIRED",
                    "الرصيد الافتتاحي يستلزم اعتماداً موثقاً مسبقاً — الأرصدة بمستندات معتمدة فقط (D-14/§14.3)");
            }
            throw Map(pg, $"CAPITAL_{pg.SqlState}");
        }
    }

    public async Task<Result<CapitalEventData>> PostCapitalEventAsync(long capitalId, long userId, long? fallbackBranchId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var row = await _db.CapitalEvents
                    .FromSql($"SELECT * FROM finance.capital_events WHERE capital_id = {capitalId} FOR UPDATE")
                    .FirstAsync(ct);
                if (row.Status != "DRAFT")
                {
                    throw new FinancePolicyException("CAPITAL_NOT_DRAFT", "لا يُرحَّل إلا حدث مسودة — لا ترحيل مكرر (§14.3)");
                }
                var now = _clock.GetUtcNow();
                if (row.TreasuryAccountId is { } accountId)
                {
                    _db.TreasuryTransactions.Add(new TreasuryTxnRow
                    {
                        TreasuryAccountId = accountId,
                        Direction = (short)CapitalRules.TreasuryDirection(row.EventType),
                        Amount = row.Amount,
                        SourceDocType = "finance.capital_event",
                        SourceDocId = row.CapitalId,
                        Reason = $"{EventTypeAr(row.EventType)} {row.EventNumber} — {row.Description}",
                        OccurredAt = now,
                        CreatedBy = userId,
                        BranchId = await BranchIdOfAsync(accountId, fallbackBranchId, ct),
                    });
                }
                row.Status = "POSTED";
                row.PostedAt = now;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);    // المشغل المؤجل يتحقق من الرجل عند الالتزام
                return Result.Ok((await GetCapitalEventAsync(capitalId, ct))!);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"CAPITAL_{pg.SqlState}");
            }
        });
    }

    private static string EventTypeAr(string eventType) => eventType switch
    {
        "OWNER_CONTRIBUTION" => "مساهمة مالك",
        "OWNER_WITHDRAWAL" => "سحب مالك",
        "LOAN_RECEIVED" => "قرض/تمويل مستلم",
        "LOAN_REPAYMENT" => "سداد قرض/تمويل",
        "OPENING_BALANCE" => "رصيد افتتاحي معتمد",
        _ => eventType,
    };

    public async Task<Result<CapitalEventData>> CancelCapitalEventDraftAsync(long capitalId, long userId, string reasonAr, CancellationToken ct = default)
    {
        var row = await _db.CapitalEvents
            .FromSql($"SELECT * FROM finance.capital_events WHERE capital_id = {capitalId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Result.Fail<CapitalEventData>(new("CAPITAL_NOT_FOUND", "الحدث غير موجود"));
        }
        if (row.Status != "DRAFT")
        {
            return Result.Fail<CapitalEventData>(new("CAPITAL_NOT_DRAFT", "لا يُلغى إلا حدث مسودة — المرحّل موثق نهائي (§14.3)"));
        }
        try
        {
            row.Status = "CANCELLED";
            await _db.SaveChangesAsync(ct);
            return Result.Ok((await GetCapitalEventAsync(capitalId, ct))!);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Map(pg, $"CAPITAL_{pg.SqlState}");
        }
    }

    public async Task<CapitalEventData?> GetCapitalEventAsync(long capitalId, CancellationToken ct = default) =>
        await (from c in _db.CapitalEvents.AsNoTracking()
               join t in _db.TreasuryAccounts.AsNoTracking() on c.TreasuryAccountId equals t.TreasuryAccountId into tj
               from t in tj.DefaultIfEmpty()
               where c.CapitalId == capitalId
               select new CapitalEventData(
                   c.CapitalId, c.EventNumber, c.EventType, c.Status, c.Amount,
                   c.TreasuryAccountId, t == null ? null : t.Code, c.EventDate, c.Description,
                   c.ApprovalId, c.CreatedAt, c.PostedAt))
              .FirstOrDefaultAsync(ct);

    public async Task<(IReadOnlyList<CapitalEventData> Items, int TotalCount)> ListCapitalEventsAsync(
        string? eventType, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.CapitalEvents.AsNoTracking()
            .Where(c => (eventType == null || c.EventType == eventType) && (status == null || c.Status == status));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var result = new List<CapitalEventData>();
        foreach (var c in rows)
        {
            var data = await GetCapitalEventAsync(c.CapitalId, ct);
            if (data is not null)
            {
                result.Add(data);
            }
        }
        return (result, total);
    }

    public async Task<bool> IsCapitalApprovalApprovedAsync(long approvalId, CancellationToken ct = default) =>
        await _db.Approvals.AsNoTracking()
            .AnyAsync(a => a.ApprovalId == approvalId
                        && a.EntityType == "finance.capital_event"
                        && a.Decision == "APPROVED", ct);

    // ═══════════════ الورديات: الإغلاق والمطابقة (§14.1) ═══════════════════

    public async Task<ShiftListData?> GetShiftAsync(long shiftId, CancellationToken ct = default) =>
        (await ShiftQuery(_db.SalesShifts.AsNoTracking().Where(s => s.ShiftId == shiftId), ct)).FirstOrDefault();

    public async Task<(IReadOnlyList<ShiftListData> Items, int TotalCount)> ListShiftsAsync(
        long? stationId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.SalesShifts.AsNoTracking()
            .Where(s => (stationId == null || s.StationId == stationId) && (status == null || s.Status == status));
        var total = await query.CountAsync(ct);
        var items = await ShiftQuery(query.OrderByDescending(s => s.OpenedAt).Skip((page - 1) * pageSize).Take(pageSize), ct);
        return (items, total);
    }

    private async Task<List<ShiftListData>> ShiftQuery(IQueryable<SalesShiftRow> query, CancellationToken ct)
    {
        // variance عمود مولّد (V005) — يُقرأ من القاعدة لا من EF
        return await (from s in query
                      join st in _db.Stations.AsNoTracking() on s.StationId equals st.StationId
                      join ob in _db.Users.AsNoTracking() on s.OpenedBy equals ob.UserId
                      join cb in _db.Users.AsNoTracking() on s.ClosedBy equals cb.UserId into cbj
                      from cb in cbj.DefaultIfEmpty()
                      orderby s.OpenedAt descending
                      select new
                      {
                          s.ShiftId,
                          Number = _db.SalesShifts.Where(x => x.StationId == s.StationId && x.OpenedAt <= s.OpenedAt).Count(),
                          s.StationId,
                          st.BranchId,
                          st.Code,
                          st.NameAr,
                          OpenedBy = ob.DisplayNameAr,
                          s.OpenedAt,
                          s.OpeningFloat,
                          s.Status,
                          s.ClosedAt,
                          ClosedBy = cb == null ? null : cb.DisplayNameAr,
                          s.ExpectedCash,
                          s.CountedCash,
                          s.VarianceReason,
                          s.VarianceSettledByVoucherId,
                      })
                      .Select(x => new ShiftListData(
                          x.ShiftId, x.Number, x.StationId, x.BranchId, x.Code, x.NameAr,
                          x.OpenedBy, x.OpenedAt, x.OpeningFloat, x.Status, x.ClosedAt, x.ClosedBy,
                          x.ExpectedCash, x.CountedCash,
                          x.ExpectedCash == null || x.CountedCash == null ? null : x.CountedCash - x.ExpectedCash,
                          x.VarianceReason, x.VarianceSettledByVoucherId))
                      .ToListAsync(ct);
    }

    public async Task<ShiftDrawerLedgerData?> GetShiftDrawerLedgerAsync(long shiftId, long drawerAccountId, CancellationToken ct = default)
    {
        const string sql = """
            WITH txns AS (
                SELECT t.*, v.voucher_type AS vtype
                  FROM finance.treasury_transactions t
                  LEFT JOIN finance.vouchers v
                         ON t.source_doc_type = 'finance.voucher' AND v.voucher_id = t.source_doc_id
                 WHERE t.shift_id = @sid AND t.treasury_account_id = @drawer
            )
            SELECT
              (SELECT opening_float FROM sales.shifts WHERE shift_id = @sid) AS opening_float,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'sales.payment'), 0) AS cash_sales,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'sales.refund'), 0) AS cash_refunds,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'finance.expense'), 0) AS drawer_expenses,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'finance.voucher' AND direction =  1 AND vtype IN ('RECEIPT')), 0) AS voucher_deposits,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'finance.voucher' AND direction = -1 AND vtype IN ('DISBURSEMENT')), 0) AS voucher_withdrawals,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'finance.voucher' AND direction =  1 AND vtype = 'TRANSFER'), 0) AS transfers_in,
              COALESCE((SELECT SUM(amount)      FROM txns WHERE source_doc_type = 'finance.voucher' AND direction = -1 AND vtype = 'TRANSFER'), 0) AS transfers_out,
              COALESCE((SELECT SUM(amount * direction) FROM txns), 0) AS net_movement,
              COALESCE((SELECT SUM(p.amount) FROM sales.payments p JOIN sales.invoices i ON i.invoice_id = p.invoice_id
                          WHERE i.shift_id = @sid AND i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
                            AND p.payment_method IN ('CARD','SPLIT_CARD') AND p.status IN ('CAPTURED','SETTLED')), 0) AS card_collected,
              COALESCE((SELECT SUM(p.amount) FROM sales.payments p JOIN sales.invoices i ON i.invoice_id = p.invoice_id
                          WHERE i.shift_id = @sid AND i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
                            AND p.payment_method = 'CREDIT' AND p.status IN ('CAPTURED','SETTLED')), 0) AS credit_issued,
              (SELECT COUNT(*) FROM sales.invoices WHERE shift_id = @sid AND status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')) AS invoice_count
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sid", shiftId);
        cmd.Parameters.AddWithValue("drawer", drawerAccountId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }
        var opening = GetDecimal(r, "opening_float");
        var net = GetDecimal(r, "net_movement");
        var movements = await ListShiftMovementsAsync(shiftId, ct);
        return new ShiftDrawerLedgerData(
            opening,
            GetDecimal(r, "cash_sales"), GetDecimal(r, "cash_refunds"), GetDecimal(r, "drawer_expenses"),
            GetDecimal(r, "voucher_deposits"), GetDecimal(r, "voucher_withdrawals"),
            GetDecimal(r, "transfers_in"), GetDecimal(r, "transfers_out"),
            net, opening + net,
            GetDecimal(r, "card_collected"), GetDecimal(r, "credit_issued"),
            Convert.ToInt32(r.GetInt64(r.GetOrdinal("invoice_count"))),
            movements);
    }

    private async Task<List<TreasuryTxnData>> ListShiftMovementsAsync(long shiftId, CancellationToken ct) =>
        await (from t in _db.TreasuryTransactions.AsNoTracking()
               join a in _db.TreasuryAccounts.AsNoTracking() on t.TreasuryAccountId equals a.TreasuryAccountId
               join u in _db.Users.AsNoTracking() on t.CreatedBy equals u.UserId
               where t.ShiftId == shiftId
               orderby t.OccurredAt, t.TxnId
               select new TreasuryTxnData(
                   t.TxnId, t.TreasuryAccountId, a.Code, a.NameAr, a.AccountKind,
                   t.Direction, t.Amount, t.SourceDocType, t.SourceDocId, t.ShiftId,
                   t.Reason, t.OccurredAt, u.DisplayNameAr)).ToListAsync(ct);

    public async Task<IReadOnlyList<DrawerEventListData>> ListDrawerEventsAsync(long shiftId, CancellationToken ct = default) =>
        await (from e in _db.DrawerEvents.AsNoTracking()
               join u in _db.Users.AsNoTracking() on e.TriggeredBy equals u.UserId
               where e.ShiftId == shiftId
               orderby e.RequestedAt
               select new DrawerEventListData(
                   e.EventId, e.EventType, e.Reason, e.RequestedAt, e.RespondedAt, e.ResultStatus, u.DisplayNameAr))
              .ToListAsync(ct);

    public async Task<IReadOnlyList<CashCountData>> ListCashCountsAsync(long shiftId, CancellationToken ct = default) =>
        await (from c in _db.ShiftCashCounts.AsNoTracking()
               join u in _db.Users.AsNoTracking() on c.CountedBy equals u.UserId
               where c.ShiftId == shiftId
               orderby c.CountedAt
               select new CashCountData(c.CountId, c.CountedCash, u.DisplayNameAr, c.CountedAt, c.Notes))
              .ToListAsync(ct);

    public async Task<Result<ShiftCloseResultData>> CloseShiftAsync(
        long shiftId, long closedByUserId, decimal expectedCash, decimal countedCash,
        string? varianceReasonAr, string? notesAr, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var shift = await _db.SalesShifts
                    .FromSql($"SELECT * FROM sales.shifts WHERE shift_id = {shiftId} FOR UPDATE")
                    .FirstAsync(ct);
                if (shift.Status != "OPEN")
                {
                    throw new FinancePolicyException("SHIFT_ALREADY_CLOSED", "الوردية مغلقة بالفعل — لا إغلاق مرتين (§14.1)");
                }

                // العدّة موثقة: من عدّ ومتى وبكم (SFT-002)
                var count = new ShiftCashCountRow
                {
                    ShiftId = shiftId,
                    CountedCash = countedCash,
                    CountedBy = closedByUserId,
                    CountedAt = _clock.GetUtcNow(),
                    Notes = notesAr,
                };
                _db.ShiftCashCounts.Add(count);
                await _db.SaveChangesAsync(ct);

                // الإغلاق: مشغل V019 يفرض العدّ والسبب عند الفارق ويجمد المغلقة بعدها
                shift.Status = "CLOSED";
                shift.ExpectedCash = expectedCash;
                shift.CountedCash = countedCash;
                shift.VarianceReason = varianceReasonAr;
                shift.ClosedBy = closedByUserId;
                shift.ClosedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var shiftData = await GetShiftAsync(shiftId, ct);
                var drawer = await FindDrawerAccountAsync(shiftData!.StationId, ct);
                var ledger = drawer is null ? null : await GetShiftDrawerLedgerAsync(shiftId, drawer.TreasuryAccountId, ct);
                var counter = await _db.Users.AsNoTracking().Where(u => u.UserId == closedByUserId).Select(u => u.DisplayNameAr).FirstAsync(ct);
                return Result.Ok(new ShiftCloseResultData(
                    shiftData,
                    new CashCountData(count.CountId, count.CountedCash, counter, count.CountedAt, count.Notes),
                    ledger ?? new ShiftDrawerLedgerData(0, 0, 0, 0, 0, 0, 0, 0, 0, expectedCash, 0, 0, 0, []),
                    countedCash - expectedCash));
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                await tx.RollbackAsync(ct);
                throw Map(pg, $"SHIFT_{pg.SqlState}");
            }
        });
    }

    public async Task<Result<long>> LinkVarianceSettlementAsync(long shiftId, long voucherId, CancellationToken ct = default)
    {
        var shift = await _db.SalesShifts
            .FromSql($"SELECT * FROM sales.shifts WHERE shift_id = {shiftId} FOR UPDATE")
            .FirstAsync(ct);
        if (shift.Status != "CLOSED")
        {
            return Result.Fail<long>(new("SHIFT_NOT_CLOSED", "تسوية الفارق لوَردية مغلقة فقط"));
        }
        if (shift.CountedCash is null || shift.ExpectedCash is null || shift.CountedCash == shift.ExpectedCash)
        {
            return Result.Fail<long>(new("SHIFT_NO_VARIANCE", "لا فارق على هذه الوردية"));
        }
        try
        {
            // الربط وحده مسموح على الوردية المغلقة (مشغل V019 يجيز هذا التغيير فقط)
            shift.VarianceSettledByVoucherId = voucherId;
            await _db.SaveChangesAsync(ct);
            return Result.Ok(shiftId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Map(pg, $"SHIFT_{pg.SqlState}");
        }
    }

    // ═══════════════ طباعة مستندات G8 (§29.10/§31/§35.2) ═══════════════════

    public async Task<StationRolePrinterData?> GetStationRolePrinterAsync(long stationId, string documentRole, CancellationToken ct = default) =>
        await (from role in _db.PrinterDocumentRoles.AsNoTracking()
               join prn in _db.PrinterProfiles.AsNoTracking() on role.PrinterProfileId equals prn.PrinterProfileId
               where role.StationId == stationId && role.DocumentRole == documentRole && prn.IsActive
               select new StationRolePrinterData(
                   prn.PrinterProfileId, prn.ProfileVersion, prn.MediaClass, prn.PrintableWidthMm,
                   prn.DpiX, prn.IsCalibrated, role.IsAutoRoutingEnabled))
              .FirstOrDefaultAsync(ct);

    public async Task<PrintOutcome> CreateFinancePrintJobAsync(
        long stationId, long userId, string documentType, long documentId,
        string copyType, long? reprintOfJobId, CancellationToken ct = default)
    {
        // خصوصية الرواتب مفروضة قبل الوصول هنا (FinancePrintPolicy في حال الاستخدام) —
        // هذا المسار لا يستقبل مستند رواتب إلا بطابعة مؤمنة معايرة لدور Confidential
        var printer = await GetStationRolePrinterAsync(stationId, FinancePrintPolicy.TargetRole(documentType), ct);
        if (printer is null)
        {
            return new PrintOutcome(false, null, null, null, "PRINTER_ROLE_NOT_ASSIGNED",
                "لم تُعيَّن طابعة لهذا الدور على المحطة — المستند محفوظ إلكترونياً (§29.10)");
        }
        var routingOk = printer.IsCalibrated && printer.IsAutoRoutingEnabled;

        // وسيط الطابعة المعلن يحدد القالب المستقل (80 → ROLL_80 · 58 → ROLL_58 — §29.2/§29.6 ق3)
        var mediaClass = printer.MediaClass;
        var template = await _db.DocumentTemplates.AsNoTracking()
            .Where(t => t.DocumentType == documentType && t.MediaClass == mediaClass && t.IsActive)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);
        if (template is null)
        {
            return new PrintOutcome(false, null, null, null, "TEMPLATE_NOT_REGISTERED",
                $"لا قالب {mediaClass} مسجل لنوع المستند {documentType} — الرندر في G10 بعد جرد الطابعات (B-07)");
        }

        var status = routingOk ? "QUEUED" : "HELD_CONFIGURATION_MISMATCH";
        var job = new PrintJobRow
        {
            DocumentId = documentId,
            DocumentType = documentType,
            TemplateId = template.TemplateId,
            TemplateVersion = template.Version,
            PrinterProfileId = printer.PrinterProfileId,
            PrinterProfileVersion = printer.PrinterProfileVersion,
            MediaClass = mediaClass,
            PrintableWidthMm = printer.PrintableWidthMm,
            Dpi = printer.Dpi,
            CopyType = copyType,
            Status = status,
            AttemptCount = 0,
            MaxAttempts = 3,
            FailureReason = routingOk ? null : "الطابعة غير معايرة أو التوجيه التلقائي معطل — مهمة معلقة HELD (الرندر الفعلي G10/B-07)",
            IdempotencyKey = $"findoc:{documentType}:{documentId}:{copyType}",
            RequestedBy = userId,
            RelatedReprintOf = reprintOfJobId,
            CreatedAt = _clock.GetUtcNow(),
        };
        try
        {
            _db.PrintJobs.Add(job);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // مهمة بنفس المفتاح موجودة — إعادة Idempotency الصادقة (لا تكرار طباعي §29.9)
            _db.Entry(job).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            var existing = await _db.PrintJobs.AsNoTracking()
                .FirstAsync(j => j.IdempotencyKey == job.IdempotencyKey, ct);
            return new PrintOutcome(true, existing.JobId, existing.Status, existing.MediaClass, null,
                "مهمة بهذا المفتاح موجودة — أعيدت كما هي (لا تكرار طباعي)");
        }

        return new PrintOutcome(
            true, job.JobId, job.Status, job.MediaClass,
            routingOk ? null : "HELD_CONFIGURATION_MISMATCH",
            routingOk
                ? $"أُنشئت مهمة {documentType} على دور {FinancePrintPolicy.TargetRole(documentType)} ({mediaClass}) — الرندر الفعلي في G10 (B-07)"
                : "المهمة معلقة HELD: الطابعة غير معايرة — المستند محفوظ ولا يتأثر (§29.9)");
    }

    // ═══════════════ أدوات مساعدة ═════════════════════════════════════════

    /// <summary>فرع الحركة: فرع الخزينة إن كان لها فرع (مركزية إن لم يكن) — وسياق فرع المنفذ احتياطاً.</summary>
    private async Task<long> BranchIdOfAsync(long treasuryAccountId, long? fallbackBranchId, CancellationToken ct)
    {
        var accountBranch = await _db.TreasuryAccounts.AsNoTracking()
            .Where(a => a.TreasuryAccountId == treasuryAccountId)
            .Select(a => a.BranchId)
            .FirstAsync(ct);
        if (accountBranch is { } branch)
        {
            return branch;
        }
        if (fallbackBranchId is { } fallback)
        {
            return fallback;
        }
        return await _db.Branches.AsNoTracking().OrderBy(b => b.BranchId).Select(b => b.BranchId).FirstAsync(ct);
    }

    private static decimal GetDecimal(NpgsqlDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? 0m : r.GetDecimal(ordinal);
    }

    /// <summary>خرق قيد قاعدة (مشغلات V019/قيود V006) → FinancePolicyException برمز الرسالة الموثق.</summary>
    private static FinancePolicyException Map(PostgresException pg, string fallbackCode)
    {
        var message = pg.MessageText ?? pg.Message;
        var separator = message.IndexOf(':');
        if (pg.SqlState is "23514" or "23505" or "P0001" && separator > 0)
        {
            var code = message[..separator].Trim();
            if (code.Length is > 2 and < 64 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_'))
            {
                return new FinancePolicyException(code, message[(separator + 1)..].Trim());
            }
        }
        return new FinancePolicyException(fallbackCode, $"خرق قيد قاعدة ({pg.SqlState}): {message}");
    }
}
