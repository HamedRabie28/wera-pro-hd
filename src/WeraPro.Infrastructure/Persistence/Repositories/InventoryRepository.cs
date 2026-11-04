namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using WeraPro.Application.Inventory;
using WeraPro.Domain.Inventory;
using WeraPro.Domain.SharedKernel;
using WeraPro.Infrastructure.Persistence.Entities;

// ═══════════════════════════════════════════════════════════════════════════
// مستودع المخزون (G6): كل عملية ذرية داخل معاملة واحدة، وترجمة أخطاء
// PostgreSQL (23505/23514/23503) إلى سياسات عربية برموز مسماة (§12).
// ═══════════════════════════════════════════════════════════════════════════

public sealed class InventoryRepository : IInventoryRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public InventoryRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ── المخازن ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WarehouseInfo>> ListWarehousesAsync(bool includeInactive, CancellationToken ct = default)
    {
        var query =
            from w in _db.Warehouses
            join b in _db.Branches on w.BranchId equals b.BranchId
            where includeInactive || w.IsActive
            orderby w.Code
            select new WarehouseInfo(w.WarehouseId, w.BranchId, b.Code, w.Code, w.NameAr, w.WhType, w.IsActive);
        return [.. await query.ToListAsync(ct)];
    }

    public async Task<Result<long>> CreateWarehouseAsync(WarehouseDraft draft, CancellationToken ct = default)
    {
        try
        {
            var row = new WarehouseRow
            {
                BranchId = draft.BranchId,
                Code = draft.Code,
                NameAr = draft.NameAr,
                WhType = draft.WhType,
                IsActive = true,
            };
            _db.Warehouses.Add(row);
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.WarehouseId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return Result.Fail<long>(new(
                pg.SqlState == "23505" ? "WAREHOUSE_CODE_EXISTS" : "WAREHOUSE_CREATE_FAILED",
                pg.SqlState == "23505"
                    ? $"كود المخزن {draft.Code} مستخدم مسبقاً"
                    : "تعذر إنشاء المخزن — تحقق من الفرع والقيم"));
        }
    }

    public async Task<Result<long>> SetWarehouseStatusAsync(long warehouseId, bool isActive, string reasonAr, long userId, CancellationToken ct = default)
    {
        var rows = await _db.Warehouses.Where(w => w.WarehouseId == warehouseId).ExecuteUpdateAsync(
            s => s.SetProperty(w => w.IsActive, isActive), ct);
        return rows == 0
            ? Result.Fail<long>(new("WAREHOUSE_NOT_FOUND", "المخزن غير موجود"))
            : Result.Ok(warehouseId);
    }

    public async Task<long?> FindWarehouseBranchAsync(long warehouseId, CancellationToken ct = default)
        => await _db.Warehouses.Where(w => w.WarehouseId == warehouseId).Select(w => (long?)w.BranchId).FirstOrDefaultAsync(ct);

    // ── الأرصدة والتقييم ────────────────────────────────────────────────

    public async Task<(IReadOnlyList<StockBalanceInfo> Items, long TotalCount)> SearchBalancesAsync(
        string? query, long? warehouseId, bool? belowReorder, int page, int pageSize, CancellationToken ct = default)
    {
        var queryRows =
            from bal in _db.Set<StockBalanceRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on bal.SkuId equals sku.SkuId
            join prod in _db.Products.AsNoTracking() on sku.ProductId equals prod.ProductId
            join wh in _db.Warehouses.AsNoTracking() on bal.WarehouseId equals wh.WarehouseId
            join layer in _db.Set<CostLayerRow>().AsNoTracking()
                on new { bal.SkuId, bal.WarehouseId } equals new { layer.SkuId, layer.WarehouseId }
                into layers
            from layer in layers.DefaultIfEmpty()
            where (warehouseId == null || bal.WarehouseId == warehouseId)
            select new
            {
                bal.SkuId,
                sku.SkuCode,
                sku.ProductId,
                ProductCode = prod.Code,
                ProductNameAr = prod.NameAr,
                SkuNameAr = sku.SkuNameAr,
                sku.SaleMode,
                sku.ReorderLevel,
                bal.WarehouseId,
                WarehouseCode = wh.Code,
                bal.BaseQty,
                bal.ReservedQty,
                AvgUnitCost = (decimal?)layer.AvgUnitCost,
            };

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            queryRows = queryRows.Where(b =>
                b.SkuCode.Contains(q) || b.ProductNameAr.Contains(q) || b.ProductCode.Contains(q) || b.SkuNameAr!.Contains(q));
        }

        var raw = await queryRows.ToListAsync(ct);
        var items = raw
            .Select(b =>
            {
                var available = ReservationRules.AvailableOf(b.BaseQty, b.ReservedQty);
                var below = ReorderRules.IsBelow(b.ReorderLevel, available);
                var valuation = b.AvgUnitCost is { } c ? decimal.Round(b.BaseQty * c, 4) : (decimal?)null;
                return new StockBalanceInfo(
                    b.SkuId, b.SkuCode, b.ProductId, b.ProductCode, b.ProductNameAr, b.SkuNameAr ?? string.Empty,
                    b.SaleMode, b.WarehouseId, b.WarehouseCode,
                    b.BaseQty, b.ReservedQty, available, b.ReorderLevel, below,
                    b.AvgUnitCost, valuation);
            })
            .Where(i => belowReorder == null || belowReorder.Value == i.BelowReorder)
            .OrderBy(i => i.SkuCode).ThenBy(i => i.WarehouseCode)
            .ToList();

        var total = items.Count;
        var paged = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (paged, total);
    }

    public async Task<IReadOnlyList<MovementCardEntry>> GetMovementCardAsync(long skuId, long? warehouseId, int limit, CancellationToken ct = default)
    {
        // كرت الحركة بدفعات الدفتر + الرصيد الجاري (دالة نافذة) — التقارير تطابق الدفتر (بوابة القبول)
        var sql = """
            SELECT m.movement_id, m.sku_id, s.sku_code, m.warehouse_id, w.code AS warehouse_code,
                   m.movement_type, m.direction, m.base_qty,
                   m.source_uom_id, u.code AS uom_code, m.conversion_factor,
                   m.source_doc_type, m.source_doc_id, m.source_line_ref, m.reason,
                   m.user_id, usr.username, m.occurred_at,
                   SUM(m.direction * m.base_qty) OVER (PARTITION BY m.sku_id, m.warehouse_id
                        ORDER BY m.occurred_at, m.movement_id
                        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_balance
              FROM inv.stock_movements m
              JOIN catalog.skus s ON s.sku_id = m.sku_id
              JOIN inv.warehouses w ON w.warehouse_id = m.warehouse_id
              JOIN catalog.units u ON u.uom_id = m.source_uom_id
              JOIN core.users usr ON usr.user_id = m.user_id
             WHERE m.sku_id = @skuId AND (@whId IS NULL OR m.warehouse_id = @whId)
             ORDER BY m.occurred_at, m.movement_id
             LIMIT @lim
            """;
        return [.. await QueryAsync(sql, [
            new NpgsqlParameter("skuId", skuId),
            new NpgsqlParameter("whId", (object?)warehouseId ?? DBNull.Value),
            new NpgsqlParameter("lim", limit),
        ], MapMovementCard, ct)];
    }

    private static MovementCardEntry MapMovementCard(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4),
        r.GetString(5), r.GetInt16(6), r.GetDecimal(7),
        r.GetInt64(8), r.GetString(9), r.GetDecimal(10),
        r.GetString(11), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetString(14),
        r.GetInt64(15), r.GetString(16), r.GetDateTime(17), r.GetDecimal(18));

    public async Task<IReadOnlyList<ValuationEntry>> GetValuationAsync(long? warehouseId, CancellationToken ct = default)
    {
        var query =
            from layer in _db.Set<CostLayerRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on layer.SkuId equals sku.SkuId
            join wh in _db.Warehouses.AsNoTracking() on layer.WarehouseId equals wh.WarehouseId
            join bal in _db.Set<StockBalanceRow>().AsNoTracking()
                on new { layer.SkuId, layer.WarehouseId } equals new { bal.SkuId, bal.WarehouseId }
            where (warehouseId == null || layer.WarehouseId == warehouseId)
                  && bal.BaseQty > 0
            orderby sku.SkuCode
            select new ValuationEntry(
                layer.SkuId, sku.SkuCode, layer.WarehouseId, wh.Code,
                bal.BaseQty, layer.AvgUnitCost, decimal.Round(bal.BaseQty * layer.AvgUnitCost, 4));
        return [.. await query.ToListAsync(ct)];
    }

    // ── الحجز والمتاح ───────────────────────────────────────────────────

    public async Task<Result<ReservationInfo>> CreateReservationAsync(ReservationDraft draft, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        Result<ReservationInfo> outcome = Result.Fail<ReservationInfo>(new("RESERVATION_FAILED", "تعذر الحجز"));
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                // زيادة المحجوز على صف الرصيد داخل المعاملة (upsert — لا صف: يبدأ من صفر)
                // ثم فحص المتاح تحت قفل الصف: أي تجاوز يُلغي المعاملة كلها
                var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(ct);
                }
                await using (var upsert = connection.CreateCommand())
                {
                    upsert.CommandText = """
                        INSERT INTO inv.stock_balances (sku_id, warehouse_id, base_qty, reserved_qty, updated_at)
                        VALUES (@s, @w, 0, @q, now())
                        ON CONFLICT (sku_id, warehouse_id)
                        DO UPDATE SET reserved_qty = inv.stock_balances.reserved_qty + @q, updated_at = now()
                        """;
                    upsert.Parameters.AddWithValue("s", draft.SkuId);
                    upsert.Parameters.AddWithValue("w", draft.WarehouseId);
                    upsert.Parameters.AddWithValue("q", draft.QtyBase);
                    await upsert.ExecuteNonQueryAsync(ct);
                }
                var balance = await _db.Set<StockBalanceRow>()
                    .FromSql($"SELECT * FROM inv.stock_balances WHERE sku_id = {draft.SkuId} AND warehouse_id = {draft.WarehouseId} FOR UPDATE")
                    .FirstAsync(ct);
                ReservationRules.Validate(draft.QtyBase, ReservationRules.AvailableOf(balance.BaseQty, balance.ReservedQty - draft.QtyBase));

                var row = new StockReservationRow
                {
                    SkuId = draft.SkuId,
                    WarehouseId = draft.WarehouseId,
                    QtyBase = draft.QtyBase,
                    SourceDocType = draft.SourceDocType,
                    SourceDocId = draft.SourceDocId,
                    ReservedAt = _clock.GetUtcNow(),
                };
                _db.Set<StockReservationRow>().Add(row);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                var skuCode = await _db.Skus.Where(s => s.SkuId == draft.SkuId).Select(s => s.SkuCode).FirstAsync(ct);
                var whCode = await _db.Warehouses.Where(w => w.WarehouseId == draft.WarehouseId).Select(w => w.Code).FirstAsync(ct);
                outcome = Result.Ok(new ReservationInfo(
                    row.ReservationId, draft.SkuId, skuCode, draft.WarehouseId, whCode,
                    draft.QtyBase, draft.SourceDocType, draft.SourceDocId, row.ReservedAt, null));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("RESERVATION_EXCEEDS_AVAILABLE"))
            {
                throw new InventoryPolicyException("RESERVATION_EXCEEDS_AVAILABLE", ex.Message);
            }
        });
        return outcome;
    }

    public async Task<Result<long>> ReleaseReservationAsync(long reservationId, string? reasonAr, long userId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        Result<long> outcome = Result.Fail<long>(new("RESERVATION_RELEASE_FAILED", "تعذر تحرير الحجز"));
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var row = await _db.Set<StockReservationRow>()
                .FromSql($"SELECT * FROM inv.stock_reservations WHERE reservation_id = {reservationId} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            if (row is null)
            {
                outcome = Result.Fail<long>(new("RESERVATION_NOT_FOUND", "الحجز غير موجود"));
                return;
            }
            if (row.ReleasedAt is not null)
            {
                outcome = Result.Fail<long>(new("RESERVATION_ALREADY_RELEASED", "الحجز محرَّر مسبقاً"));
                return;
            }
            row.ReleasedAt = _clock.GetUtcNow();
            // إنقاص المحجوز على صف الرصيد (لا أنزل تحت الصفر)
            await _db.Set<StockBalanceRow>()
                .Where(b => b.SkuId == row.SkuId && b.WarehouseId == row.WarehouseId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.ReservedQty, b => b.ReservedQty - row.QtyBase < 0 ? 0 : b.ReservedQty - row.QtyBase)
                    .SetProperty(b => b.UpdatedAt, _clock.GetUtcNow()), ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            outcome = Result.Ok(reservationId);
        });
        return outcome;
    }

    public async Task<IReadOnlyList<ReservationInfo>> ListReservationsAsync(bool activeOnly, CancellationToken ct = default)
    {
        var query =
            from r in _db.Set<StockReservationRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on r.SkuId equals sku.SkuId
            join wh in _db.Warehouses.AsNoTracking() on r.WarehouseId equals wh.WarehouseId
            where !activeOnly || r.ReleasedAt == null
            orderby r.ReservedAt descending
            select new ReservationInfo(
                r.ReservationId, r.SkuId, sku.SkuCode, r.WarehouseId, wh.Code,
                r.QtyBase, r.SourceDocType, r.SourceDocId, r.ReservedAt, r.ReleasedAt);
        return [.. await query.ToListAsync(ct)];
    }

    public async Task<StockBalanceSnap?> FindBalanceAsync(long skuId, long warehouseId, CancellationToken ct = default)
    {
        var row = await _db.Set<StockBalanceRow>().AsNoTracking()
            .FirstOrDefaultAsync(b => b.SkuId == skuId && b.WarehouseId == warehouseId, ct);
        return row is null ? null : new StockBalanceSnap(row.BaseQty, row.ReservedQty);
    }

    // ── الجرد والتسويات ─────────────────────────────────────────────────

    public async Task<Result<long>> OpenCountAsync(CountDraft draft, CancellationToken ct = default)
    {
        var openExists = await _db.Set<CountSessionRow>()
            .AnyAsync(c => c.WarehouseId == draft.WarehouseId && (c.Status == "OPEN" || c.Status == "COUNTED"), ct);
        if (openExists)
        {
            return Result.Fail<long>(new("COUNT_ALREADY_OPEN", "جلسة جرد مفتوحة بالفعل لهذا المخزن — أغلقها أو اعتمدها أولاً"));
        }
        var row = new CountSessionRow
        {
            WarehouseId = draft.WarehouseId,
            Status = "OPEN",
            CountedBy = draft.CountedBy,
            Notes = draft.Notes,
            OpenedAt = _clock.GetUtcNow(),
        };
        _db.Set<CountSessionRow>().Add(row);
        await _db.SaveChangesAsync(ct);
        return Result.Ok(row.CountId);
    }

    public async Task<Result<long>> RecordCountLinesAsync(long countId, IReadOnlyList<CountLineDraft> lines, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var line in lines)
            {
                // لقطة الرصيد الدفتري وقت التسجيل — لا رقم حر
                var balance = await _db.Set<StockBalanceRow>().AsNoTracking()
                    .Where(b => b.SkuId == line.SkuId)
                    .Join(_db.Set<CountSessionRow>().AsNoTracking().Where(c => c.CountId == countId),
                        b => b.WarehouseId, c => c.WarehouseId, (b, _) => b)
                    .FirstOrDefaultAsync(ct);
                var systemQty = balance?.BaseQty ?? 0;

                var existing = await _db.Set<CountLineRow>()
                    .FirstOrDefaultAsync(l => l.CountId == countId && l.SkuId == line.SkuId, ct);
                if (existing is null)
                {
                    _db.Set<CountLineRow>().Add(new CountLineRow
                    {
                        CountId = countId,
                        SkuId = line.SkuId,
                        SystemQty = systemQty,
                        CountedQty = line.CountedQty,
                    });
                }
                else
                {
                    existing.SystemQty = systemQty;   // آخر لقطة وقت آخر تسجيل
                    existing.CountedQty = line.CountedQty;
                }
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        });
        return Result.Ok(0L);
    }

    public async Task<Result<long>> CloseCountAsync(long countId, CancellationToken ct = default)
    {
        var rows = await _db.Set<CountSessionRow>()
            .Where(c => c.CountId == countId && c.Status == "OPEN")
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, "COUNTED")
                .SetProperty(c => c.ClosedAt, _clock.GetUtcNow()), ct);
        return rows == 0
            ? Result.Fail<long>(new("COUNT_NOT_OPEN", "الجلسة ليست في OPEN"))
            : Result.Ok(0L);
    }

    public async Task<Result<long>> ReconcileCountAsync(long countId, CancellationToken ct = default)
    {
        var rows = await _db.Set<CountSessionRow>()
            .Where(c => c.CountId == countId && c.Status == "COUNTED")
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "RECONCILED"), ct);
        return rows == 0
            ? Result.Fail<long>(new("COUNT_NOT_COUNTED", "الجلسة ليست في COUNTED"))
            : Result.Ok(0L);
    }

    public async Task<Result<long>> CancelCountAsync(long countId, CancellationToken ct = default)
    {
        var rows = await _db.Set<CountSessionRow>()
            .Where(c => c.CountId == countId && (c.Status == "OPEN" || c.Status == "COUNTED"))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "CANCELLED"), ct);
        return rows == 0
            ? Result.Fail<long>(new("COUNT_NOT_CANCELLABLE", "الإلغاء من OPEN أو COUNTED فقط"))
            : Result.Ok(countId);
    }

    public async Task<CountDetail?> FindCountAsync(long countId, CancellationToken ct = default)
    {
        var session = await (from c in _db.Set<CountSessionRow>().AsNoTracking()
                             join counter in _db.Users.AsNoTracking() on c.CountedBy equals counter.UserId
                             join wh in _db.Warehouses.AsNoTracking() on c.WarehouseId equals wh.WarehouseId
                             where c.CountId == countId
                             select new { c, counter.Username, wh.Code }).FirstOrDefaultAsync(ct);
        if (session is null)
        {
            return null;
        }
        var approverName = session.c.ApprovedBy is { } aid
            ? await _db.Users.Where(u => u.UserId == aid).Select(u => u.Username).FirstOrDefaultAsync(ct)
            : null;
        var lines = await (
            from l in _db.Set<CountLineRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on l.SkuId equals sku.SkuId
            where l.CountId == countId
            orderby sku.SkuCode
            select new CountLineInfo(l.SkuId, sku.SkuCode, sku.SkuNameAr ?? sku.SkuCode, l.SystemQty, l.CountedQty,
                decimal.Round(l.CountedQty - l.SystemQty, 6)))
            .ToListAsync(ct);

        return new CountDetail(
            session.c.CountId, session.c.WarehouseId, session.Code, session.c.Status,
            session.c.CountedBy, session.Username, session.c.ApprovedBy, approverName,
            session.c.ApprovedAt, session.c.OpenedAt, session.c.ClosedAt, session.c.Notes, lines);
    }

    public async Task<IReadOnlyList<CountSummary>> ListCountsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query =
            from c in _db.Set<CountSessionRow>().AsNoTracking()
            join counter in _db.Users.AsNoTracking() on c.CountedBy equals counter.UserId
            join wh in _db.Warehouses.AsNoTracking() on c.WarehouseId equals wh.WarehouseId
            orderby c.OpenedAt descending
            select new { c, counter.Username, wh.Code };
        var raw = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var result = new List<CountSummary>();
        foreach (var r in raw)
        {
            var agg = await _db.Set<CountLineRow>().AsNoTracking()
                .Where(l => l.CountId == r.c.CountId)
                .GroupBy(l => 1)
                .Select(g => new { Count = g.Count(), Variance = g.Sum(l => l.CountedQty - l.SystemQty) })
                .FirstOrDefaultAsync(ct);
            var approverName = r.c.ApprovedBy is { } aid
                ? await _db.Users.Where(u => u.UserId == aid).Select(u => u.Username).FirstOrDefaultAsync(ct)
                : null;
            result.Add(new CountSummary(
                r.c.CountId, r.c.WarehouseId, r.Code, r.c.Status, r.c.CountedBy, r.Username,
                r.c.ApprovedBy, approverName, r.c.ApprovedAt, r.c.OpenedAt, r.c.ClosedAt, r.c.Notes,
                agg?.Count ?? 0, decimal.Round(agg?.Variance ?? 0, 6)));
        }
        return result;
    }

    public async Task<Result<ApprovedCountResult>> ApproveCountAsync(ApproveCountPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var session = await _db.Set<CountSessionRow>()
                    .FromSql($"SELECT * FROM inv.count_sessions WHERE count_id = {plan.CountId} FOR UPDATE")
                    .FirstAsync(ct);
                if (session.Status != "RECONCILED")
                {
                    throw new InventoryPolicyException("COUNT_NOT_RECONCILED", "الاعتماد من RECONCILED فقط (أُعيد فحصها داخل المعاملة)");
                }
                if (session.ApprovedBy == plan.ApprovedBy || session.CountedBy == plan.ApprovedBy)
                {
                    throw new InventoryPolicyException("COUNT_APPROVER_MUST_DIFFER", "المعتمد يجب أن يكون غير من قام بالجرد (فصل واجبات)");
                }

                // 1) صف الاعتماد: طلب من الجارّ وقرار من المعتمد (≠ بعضهما — قيد V003)
                var approval = new ApprovalRow
                {
                    EntityType = "inv.count_session",
                    EntityId = plan.CountId,
                    RequestedBy = plan.CountedBy,
                    DecidedBy = plan.ApprovedBy,
                    Decision = "APPROVED",
                    Reason = plan.Notes ?? "اعتماد تسوية جرد",
                    RequestedAt = _clock.GetUtcNow(),
                    DecidedAt = _clock.GetUtcNow(),
                };
                _db.Approvals.Add(approval);
                await _db.SaveChangesAsync(ct);

                // 2) مستند التسوية + الحركات: سطر لكل صنف ذي فرق (الفرق الصفر بلا حركة)
                var movementIds = new List<long>();
                var adjustmentDocIds = new List<long>();
                var zeroVariance = 0;
                var adjustedCount = 0;
                foreach (var line in plan.Lines)
                {
                    if (line.MovementType is null)
                    {
                        // فرق صفر: يُحصى في التقرير — بلا مستند ولا حركة (لا ضجيج في الدفتر)
                        zeroVariance++;
                        continue;
                    }
                    var doc = new StockAdjustmentRow
                    {
                        CountId = plan.CountId,
                        WarehouseId = session.WarehouseId,
                        SkuId = line.SkuId,
                        NewQty = decimal.Round(line.CountedQty, 6),
                        Reason = $"جرد {session.CountId}: الفرق {line.VarianceQty:0.######}" + (plan.Notes is null ? "" : $" — {plan.Notes}"),
                        ApprovalId = approval.ApprovalId,
                        PostedAt = _clock.GetUtcNow(),
                        CreatedBy = plan.ApprovedBy,
                        CreatedAt = _clock.GetUtcNow(),
                        AdjustmentKind = "STOCKTAKE",
                    };
                    _db.Set<StockAdjustmentRow>().Add(doc);
                    await _db.SaveChangesAsync(ct);
                    adjustmentDocIds.Add(doc.AdjustmentId);
                    adjustedCount++;

                    var baseUom = await GetBaseUomAsync(line.SkuId, ct);
                    var movement = BuildMovement(
                        line.SkuId, session.WarehouseId, line.MovementType, line.Direction, line.BaseQty,
                        baseUom, 1m, "inv.count_session", plan.CountId, doc.AdjustmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        approval.ApprovalId, $"جرد معتمد {plan.CountId}", plan.BranchId, plan.ApprovedBy);
                    _db.StockMovements.Add(movement);
                    await _db.SaveChangesAsync(ct);
                    movementIds.Add(movement.MovementId);
                }

                // 3) حالة الجلسة APPROVED
                session.Status = "APPROVED";
                session.ApprovedBy = plan.ApprovedBy;
                session.ApprovedAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return Result.Ok(new ApprovedCountResult(
                    plan.CountId, approval.ApprovalId, [.. adjustmentDocIds], [.. movementIds],
                    adjustedCount, zeroVariance));
            });
        }
        catch (InventoryPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            throw new InventoryPolicyException(TranslateConstraint(pg), "الحركة تخالف قيود الدفتر (D-03/الاتساق)");
        }
    }

    public async Task<Result<PostedAdjustment>> PostAdjustmentAsync(AdjustmentPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                // الاعتماد يجب أن يكون معتمداً (قرار APPROVED) — قيد V003 adjust_approval_required
                var approval = await _db.Approvals.FromSql(
                    $"SELECT * FROM core.approvals WHERE approval_id = {plan.ApprovalId} FOR UPDATE").FirstOrDefaultAsync(ct);
                if (approval is null || approval.Decision != "APPROVED")
                {
                    throw new InventoryPolicyException("APPROVAL_NOT_APPROVED", "التسوية تلزم اعتماداً بقرار APPROVED — أنشئ طلباً ودع المشرف يبت فيه");
                }

                var doc = new StockAdjustmentRow
                {
                    CountId = plan.CountId,
                    WarehouseId = plan.WarehouseId,
                    SkuId = plan.SkuId,
                    NewQty = decimal.Round(plan.NewQty, 6),
                    Reason = plan.ReasonAr,
                    ApprovalId = plan.ApprovalId,
                    PostedAt = _clock.GetUtcNow(),
                    CreatedBy = plan.UserId,
                    CreatedAt = _clock.GetUtcNow(),
                    AdjustmentKind = plan.AdjustmentKind,
                };
                _db.Set<StockAdjustmentRow>().Add(doc);
                await _db.SaveChangesAsync(ct);

                long? movementId = null;
                if (plan.MovementType is not null && plan.Direction != 0)
                {
                    var baseUom = await GetBaseUomAsync(plan.SkuId, ct);
                    var movement = BuildMovement(
                        plan.SkuId, plan.WarehouseId, plan.MovementType, plan.Direction, plan.BaseQty,
                        baseUom, 1m, plan.CountId is null ? "inv.adjustment" : "inv.count_session",
                        plan.CountId ?? doc.AdjustmentId, doc.AdjustmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        plan.ApprovalId, plan.ReasonAr, plan.BranchId, plan.UserId);
                    _db.StockMovements.Add(movement);
                    await _db.SaveChangesAsync(ct);
                    movementId = movement.MovementId;
                }
                await tx.CommitAsync(ct);
                return Result.Ok(new PostedAdjustment(doc.AdjustmentId, movementId, plan.MovementType, plan.BaseQty, plan.Direction));
            });
        }
        catch (InventoryPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            throw new InventoryPolicyException(TranslateConstraint(pg), "الحركة تخالف قيود الدفتر (D-03/الاتساق)");
        }
    }

    public async Task<IReadOnlyList<AdjustmentEntry>> ListAdjustmentsAsync(long? warehouseId, int limit, CancellationToken ct = default)
    {
        var query =
            from a in _db.Set<StockAdjustmentRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on a.SkuId equals sku.SkuId
            join wh in _db.Warehouses.AsNoTracking() on a.WarehouseId equals wh.WarehouseId
            join usr in _db.Users.AsNoTracking() on a.CreatedBy equals usr.UserId
            where warehouseId == null || a.WarehouseId == warehouseId
            orderby a.AdjustmentId descending
            select new AdjustmentEntry(
                a.AdjustmentId, a.AdjustmentKind, a.SkuId, sku.SkuCode, a.WarehouseId, wh.Code,
                a.NewQty, a.Reason, a.ApprovalId, a.CountId, a.PostedAt, usr.Username);
        return [.. await query.Take(limit).ToListAsync(ct)];
    }

    // ── التحويلات ───────────────────────────────────────────────────────

    public async Task<Result<long>> CreateTransferAsync(TransferDraft draft, CancellationToken ct = default)
    {
        try
        {
            var order = new TransferOrderRow
            {
                FromWarehouseId = draft.FromWarehouseId,
                ToWarehouseId = draft.ToWarehouseId,
                Status = "DRAFT",
                CreatedBy = draft.UserId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Set<TransferOrderRow>().Add(order);
            await _db.SaveChangesAsync(ct);
            foreach (var line in draft.Lines)
            {
                _db.Set<TransferLineRow>().Add(new TransferLineRow
                {
                    TransferId = order.TransferId,
                    SkuId = line.SkuId,
                    QtyBase = line.QtyBase,
                });
            }
            await _db.SaveChangesAsync(ct);
            return Result.Ok(order.TransferId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            throw new InventoryPolicyException(TranslateConstraint(pg), "التحويل يخالف قيود القاعدة (مخزنان مختلفان مثلاً)");
        }
    }

    public async Task<Result<TransferAction>> SendTransferAsync(SendTransferPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var order = await _db.Set<TransferOrderRow>()
                    .FromSql($"SELECT * FROM inv.transfer_orders WHERE transfer_id = {plan.TransferId} FOR UPDATE")
                    .FirstAsync(ct);
                if (order.Status != "DRAFT")
                {
                    throw new InventoryPolicyException("TRANSFER_NOT_DRAFT", "الإرسال من DRAFT فقط (أُعيد فحصها داخل المعاملة)");
                }
                long? approvalId = null;
                if (plan.ApprovalId is { } aid)
                {
                    var approval = await _db.Approvals.FromSql(
                        $"SELECT * FROM core.approvals WHERE approval_id = {aid} FOR UPDATE").FirstOrDefaultAsync(ct);
                    if (approval is null || approval.Decision != "APPROVED")
                    {
                        throw new InventoryPolicyException("APPROVAL_NOT_APPROVED", "اعتماد D-03 غير موجود أو غير معتمد");
                    }
                    approvalId = approval.ApprovalId;
                }

                var movementIds = new List<long>();
                foreach (var m in plan.Movements)
                {
                    var movement = BuildMovement(
                        m.SkuId, order.FromWarehouseId, "TRANSFER_OUT", -1, m.BaseQty,
                        m.SourceUomId, m.ConversionFactor, "inv.transfer", plan.TransferId, null,
                        approvalId, plan.ReasonAr, plan.BranchId, plan.UserId);
                    _db.StockMovements.Add(movement);
                    await _db.SaveChangesAsync(ct);
                    movementIds.Add(movement.MovementId);
                }
                order.Status = "SENT";
                order.SentAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result.Ok(new TransferAction(plan.TransferId, "SENT", [.. movementIds]));
            });
        }
        catch (InventoryPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            throw new InventoryPolicyException(TranslateConstraint(pg), "حركة التحويل تخالف قيود الدفتر — D-03 يحتسب اعتماداً معتمداً");
        }
    }

    public async Task<Result<TransferAction>> ReceiveTransferAsync(long transferId, long userId, long branchId, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var order = await _db.Set<TransferOrderRow>()
                .FromSql($"SELECT * FROM inv.transfer_orders WHERE transfer_id = {transferId} FOR UPDATE")
                .FirstAsync(ct);
            if (order.Status != "SENT")
            {
                return Result.Fail<TransferAction>(new("TRANSFER_NOT_SENT", "الاستلام من SENT فقط"));
            }
            var lines = await _db.Set<TransferLineRow>().Where(l => l.TransferId == transferId).ToListAsync(ct);
            var movementIds = new List<long>();
            foreach (var line in lines)
            {
                var baseUom = await GetBaseUomAsync(line.SkuId, ct);
                var movement = BuildMovement(
                    line.SkuId, order.ToWarehouseId, "TRANSFER_IN", 1, line.QtyBase,
                    baseUom, 1m, "inv.transfer", transferId, null,
                    null, null, branchId, userId);
                _db.StockMovements.Add(movement);
                await _db.SaveChangesAsync(ct);
                movementIds.Add(movement.MovementId);
            }
            order.Status = "RECEIVED";
            order.ReceivedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Ok(new TransferAction(transferId, "RECEIVED", [.. movementIds]));
        });
    }

    public async Task<Result<long>> CancelTransferAsync(long transferId, CancellationToken ct = default)
    {
        var rows = await _db.Set<TransferOrderRow>()
            .Where(t => t.TransferId == transferId && t.Status == "DRAFT")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "CANCELLED"), ct);
        return rows == 0
            ? Result.Fail<long>(new("TRANSFER_NOT_DRAFT", "الإلغاء من DRAFT فقط"))
            : Result.Ok(0L);
    }

    public async Task<TransferDetail?> FindTransferAsync(long transferId, CancellationToken ct = default)
    {
        var header = await (from t in _db.Set<TransferOrderRow>().AsNoTracking()
                            join fromWh in _db.Warehouses.AsNoTracking() on t.FromWarehouseId equals fromWh.WarehouseId
                            join toWh in _db.Warehouses.AsNoTracking() on t.ToWarehouseId equals toWh.WarehouseId
                            join usr in _db.Users.AsNoTracking() on t.CreatedBy equals usr.UserId
                            where t.TransferId == transferId
                            select new { t, FromCode = fromWh.Code, ToCode = toWh.Code, usr.Username }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }
        var lines = await (
            from l in _db.Set<TransferLineRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on l.SkuId equals sku.SkuId
            where l.TransferId == transferId
            orderby sku.SkuCode
            select new TransferLineInfo(l.SkuId, sku.SkuCode, l.QtyBase)).ToListAsync(ct);
        return new TransferDetail(
            header.t.TransferId, header.t.FromWarehouseId, header.FromCode,
            header.t.ToWarehouseId, header.ToCode, header.t.Status,
            header.t.SentAt, header.t.ReceivedAt, header.Username, header.t.CreatedAt, lines);
    }

    public async Task<IReadOnlyList<TransferSummary>> ListTransfersAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query =
            from t in _db.Set<TransferOrderRow>().AsNoTracking()
            join fromWh in _db.Warehouses.AsNoTracking() on t.FromWarehouseId equals fromWh.WarehouseId
            join toWh in _db.Warehouses.AsNoTracking() on t.ToWarehouseId equals toWh.WarehouseId
            where status == null || t.Status == status
            orderby t.TransferId descending
            select new TransferSummary(
                t.TransferId, t.FromWarehouseId, fromWh.Code, t.ToWarehouseId, toWh.Code,
                t.Status, t.SentAt, t.ReceivedAt, t.CreatedAt);
        return [.. await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct)];
    }

    // ── النواقص والدفعات ────────────────────────────────────────────────

    public async Task<IReadOnlyList<ReorderEntry>> GetReorderAsync(long? warehouseId, CancellationToken ct = default)
    {
        var query =
            from bal in _db.Set<StockBalanceRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on bal.SkuId equals sku.SkuId
            join prod in _db.Products.AsNoTracking() on sku.ProductId equals prod.ProductId
            join wh in _db.Warehouses.AsNoTracking() on bal.WarehouseId equals wh.WarehouseId
            where (warehouseId == null || bal.WarehouseId == warehouseId)
            select new
            {
                sku.SkuId, sku.SkuCode, sku.SkuNameAr, prod.NameAr,
                bal.WarehouseId, wh.Code,
                bal.BaseQty, bal.ReservedQty, sku.ReorderLevel,
            };
        var raw = await query.ToListAsync(ct);
        return [.. raw
            .Select(b =>
            {
                var rawAvailable = decimal.Round(b.BaseQty - b.ReservedQty, 6, MidpointRounding.AwayFromZero);   // خام — يشمل العجز (D-03)
                return new ReorderEntry(
                    b.SkuId, b.SkuCode, b.SkuNameAr ?? b.SkuCode, b.NameAr, b.WarehouseId, b.Code,
                    rawAvailable, b.ReorderLevel, ReorderRules.SuggestedQty(b.ReorderLevel, rawAvailable));
            })
            .Where(r => ReorderRules.IsBelow(r.ReorderLevel, r.AvailableQty))
            .OrderBy(r => r.SkuCode)];
    }

    public async Task<IReadOnlyList<LotEntry>> GetLotsAsync(int? expiringInDays, CancellationToken ct = default)
    {
        var horizon = expiringInDays ?? 30;
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var sql = """
            SELECT l.lot_id, l.sku_id, s.sku_code, l.lot_code, l.expiry_date, l.received_at,
                   (SELECT r.receipt_id FROM purch.purchase_receipt_lines rl
                     JOIN purch.purchase_receipts r ON r.receipt_id = rl.receipt_id
                    WHERE rl.lot_id = l.lot_id AND r.status = 'POSTED'
                    ORDER BY r.receipt_id LIMIT 1) AS receipt_id,
                   (SELECT r.receipt_number FROM purch.purchase_receipt_lines rl
                     JOIN purch.purchase_receipts r ON r.receipt_id = rl.receipt_id
                    WHERE rl.lot_id = l.lot_id AND r.status = 'POSTED'
                    ORDER BY r.receipt_id LIMIT 1) AS receipt_number,
                   COALESCE((SELECT SUM(m.direction * m.base_qty) FROM inv.stock_movements m
                              WHERE m.lot_id = l.lot_id), 0) AS on_hand
              FROM inv.lots l
              JOIN catalog.skus s ON s.sku_id = l.sku_id
             ORDER BY l.expiry_date NULLS LAST, l.lot_id
            """;
        var rows = await QueryAsync(sql, [], r => new
        {
            LotId = r.GetInt64(0),
            SkuId = r.GetInt64(1),
            SkuCode = r.GetString(2),
            LotCode = r.GetString(3),
            Expiry = r.IsDBNull(4) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(4)),
            ReceivedAt = r.GetDateTime(5),
            ReceiptId = r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
            ReceiptNumber = r.IsDBNull(7) ? null : r.GetString(7),
            OnHand = r.GetDecimal(8),
        }, ct);

        return [.. rows.Select(x => new LotEntry(
            x.LotId, x.SkuId, x.SkuCode, x.LotCode, x.Expiry, x.ReceivedAt, x.ReceiptId, x.ReceiptNumber,
            x.Expiry is { } e && e <= today.AddDays(horizon),
            x.OnHand))];
    }

    public async Task<SkuUomInfo?> FindSkuBaseUomAsync(long skuId, CancellationToken ct = default)
    {
        var info = await (
            from sku in _db.Skus.AsNoTracking()
            join u in _db.Units.AsNoTracking() on sku.BaseUomId equals u.UomId
            where sku.SkuId == skuId
            select new SkuUomInfo(sku.SkuId, sku.SkuCode, sku.BaseUomId, u.Code, sku.SaleMode)).FirstOrDefaultAsync(ct);
        return info;
    }

    private async Task<long> GetBaseUomAsync(long skuId, CancellationToken ct)
        => await _db.Skus.Where(s => s.SkuId == skuId).Select(s => s.BaseUomId).FirstAsync(ct);

    internal StockMovementRow BuildMovement(
        long skuId, long warehouseId, string movementType, int direction, decimal baseQty,
        long sourceUomId, decimal conversionFactor, string sourceDocType, long sourceDocId,
        string? sourceLineRef, long? approvalId, string? reason, long branchId, long userId) => new()
    {
        SkuId = skuId,
        WarehouseId = warehouseId,
        MovementType = movementType,
        Direction = (short)direction,
        BaseQty = decimal.Round(baseQty, 6),
        SourceUomId = sourceUomId,
        ConversionFactor = conversionFactor,
        SourceDocType = sourceDocType,
        SourceDocId = sourceDocId,
        SourceLineRef = sourceLineRef,
        ApprovalId = approvalId,
        Reason = reason,
        BranchId = branchId,
        UserId = userId,
        OccurredAt = _clock.GetUtcNow(),
    };

    internal static string TranslateConstraint(PostgresException pg)
    {
        var raw = pg.MessageText ?? string.Empty;
        if (raw.Contains("NEGATIVE_STOCK_NEEDS_APPROVAL"))
        {
            return "NEGATIVE_STOCK_NEEDS_APPROVAL";
        }
        if (raw.Contains("NEGATIVE_STOCK_BLOCKED"))
        {
            return "NEGATIVE_STOCK_BLOCKED";
        }
        if (raw.Contains("STOCK_LEDGER_IMMUTABLE"))
        {
            return "STOCK_LEDGER_IMMUTABLE";
        }
        return "CONSTRAINT_VIOLATION";
    }

    private async Task<List<T>> QueryAsync<T>(string sql, NpgsqlParameter[] parameters, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(map(reader));
        }
        return result;
    }
}
