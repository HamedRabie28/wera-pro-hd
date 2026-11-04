namespace WeraPro.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using WeraPro.Application.Purchasing;
using WeraPro.Domain.Purchasing;
using WeraPro.Domain.SharedKernel;
using WeraPro.Infrastructure.Persistence.Entities;
using ApplicationPurchasing = WeraPro.Application.Purchasing;

// ═══════════════════════════════════════════════════════════════════════════
// مستودع المشتريات (G6): الاستلام يزيد المخزون (لا الفاتورة)، والسداد أثر
// خزينة واحد، والمرتجع مرتبط بأصله بحد ما استُلم — كل ترحيل ذري.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class PurchasingRepository : IPurchasingRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public PurchasingRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ── الموردون ─────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<SupplierRecord> Items, long Total)> SearchSuppliersAsync(string? query, int page, int pageSize, CancellationToken ct = default)
    {
        var rows = _db.Set<SupplierRow>().AsNoTracking().OrderBy(s => s.Code).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = rows.Where(s => s.Code.Contains(q) || s.NameAr.Contains(q) || (s.NameEn != null && s.NameEn.Contains(q)) || (s.Phone != null && s.Phone.Contains(q)));
        }
        var total = await rows.LongCountAsync(ct);
        var pageRows = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return ([.. pageRows.Select(ToRecord)], total);
    }

    public async Task<Result<long>> CreateSupplierAsync(SupplierDraft draft, CancellationToken ct = default)
    {
        try
        {
            var row = new SupplierRow
            {
                Code = draft.Code,
                NameAr = draft.NameAr,
                NameEn = draft.NameEn,
                Phone = draft.Phone,
                Email = draft.Email,
                TaxId = draft.TaxId,
                PaymentTermsDays = draft.PaymentTermsDays,
                CreditLimit = draft.CreditLimit,
                Notes = draft.Notes,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.Set<SupplierRow>().Add(row);
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.SupplierId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return Result.Fail<long>(new(
                pg.SqlState == "23505" ? "SUPPLIER_CODE_EXISTS" : "SUPPLIER_CREATE_FAILED",
                pg.SqlState == "23505" ? $"كود المورد {draft.Code} مستخدم مسبقاً" : "تعذر إنشاء المورد"));
        }
    }

    public async Task<Result<long>> SetSupplierStatusAsync(long supplierId, string status, string reasonAr, long userId, CancellationToken ct = default)
    {
        var rows = await _db.Set<SupplierRow>()
            .Where(s => s.SupplierId == supplierId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.AccountStatus, status).SetProperty(s => s.UpdatedAt, _clock.GetUtcNow()), ct);
        return rows == 0
            ? Result.Fail<long>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"))
            : Result.Ok(supplierId);
    }

    public async Task<SupplierRecord?> FindSupplierAsync(long supplierId, CancellationToken ct = default)
    {
        var row = await _db.Set<SupplierRow>().AsNoTracking().FirstOrDefaultAsync(s => s.SupplierId == supplierId, ct);
        return row is null ? null : ToRecord(row);
    }

    public async Task<SupplierStatement> GetSupplierStatementAsync(long supplierId, DateOnly fromDate, DateOnly toDate, string? docType, CancellationToken ct = default)
    {
        var name = (await _db.Set<SupplierRow>().AsNoTracking().Where(s => s.SupplierId == supplierId).Select(s => s.NameAr).FirstOrDefaultAsync(ct)) ?? string.Empty;

        // الفواتير المرحّلة (+ ما على المورد) — المسودة والملغى ليست التزاماً
        var invoices = await (
            from i in _db.Set<SupplierInvoiceRow>().AsNoTracking()
            where i.SupplierId == supplierId && i.Status != "DRAFT" && i.Status != "CANCELLED"
            select new StatementLine("purch.invoice", i.InvoiceId, i.InvoiceNumber, i.InvoiceDate, i.GrandTotal,
                "فاتورة مورد — استلام " + (i.ReceiptId == null ? "بلا" : i.ReceiptId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            ).ToListAsync(ct);
        // السدادات المرحّلة (− تخفيض) — أثر خزينة واحد لا مصروف
        var payments = await (
            from p in _db.Set<SupplierPaymentRow>().AsNoTracking()
            where p.SupplierId == supplierId && p.Status == "POSTED"
            join t in _db.Set<TreasuryAccountRow>().AsNoTracking() on p.TreasuryAccountId equals t.TreasuryAccountId
            select new StatementLine("purch.payment", p.PaymentId, p.VoucherNumber, p.PaymentDate, -p.Amount,
                $"سداد {p.PaymentMethod} من {t.Code}")
            ).ToListAsync(ct);
        // مرتجعات بإشعار دائن (− تخفيض بلا نقد) — الرد النقدي أثره في الخزينة لا في الذمم
        var creditNotes = await (
            from r in _db.Set<SupplierReturnRow>().AsNoTracking()
            where r.SupplierId == supplierId && r.Status == "POSTED" && r.RefundMethod == "CREDIT_NOTE"
            select new StatementLine("purch.return", r.ReturnId, r.ReturnNumber,
                DateOnly.FromDateTime((r.PostedAt ?? r.CreatedAt).UtcDateTime), -r.Total,
                "مرتجع بإشعار دائن على استلام " + (r.ReceiptId == null ? "بلا" : r.ReceiptId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            ).ToListAsync(ct);

        return BuildStatement(supplierId, name, fromDate, toDate, docType, [.. invoices, .. payments, .. creditNotes]);
    }

    internal static SupplierStatement BuildStatement(long partyId, string name, DateOnly from, DateOnly to, string? docType, List<StatementLine> all)
    {
        if (!string.IsNullOrWhiteSpace(docType))
        {
            all = [.. all.Where(e => e.DocType == docType.Trim())];
        }
        List<StatementLine> before = [.. all.Where(e => e.Date < from)];
        List<StatementLine> within = [.. all.Where(e => e.Date >= from && e.Date <= to)];
        var opening = StatementMath.OpeningBalance(before.Select(e => e.Amount));
        var closing = StatementMath.ClosingBalance(opening, within.Select(e => e.Amount));
        return new SupplierStatement(
            partyId, name, from, to,
            opening,
            within.Where(e => e.Amount > 0).Sum(e => e.Amount),
            -within.Where(e => e.Amount < 0).Sum(e => e.Amount),
            closing, within);
    }

    private static SupplierRecord ToRecord(SupplierRow r) => new(
        r.SupplierId, r.Code, r.NameAr, r.NameEn, r.Phone, r.Email, r.TaxId,
        r.PaymentTermsDays, r.CreditLimit, r.AccountStatus, r.Notes, r.CreatedAt);

    // ── أوامر الشراء (السلة) ──────────────────────────────────────────────

    public async Task<Result<long>> CreatePurchaseOrderAsync(PoDraft draft, CancellationToken ct = default)
    {
        try
        {
            var number = await NextDocNumberAsync(DocNumberPrefixes.PurchaseOrder, ct);
            var row = new PurchaseOrderRow
            {
                PoNumber = number,
                SupplierId = draft.SupplierId,
                WarehouseId = draft.WarehouseId,
                Status = "DRAFT",
                ExpectedDate = draft.ExpectedDate,
                CreatedBy = draft.UserId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Set<PurchaseOrderRow>().Add(row);
            await _db.SaveChangesAsync(ct);
            foreach (var line in draft.Lines)
            {
                _db.Set<PurchaseOrderLineRow>().Add(new PurchaseOrderLineRow
                {
                    PoId = row.PoId,
                    SkuId = line.SkuId,
                    Qty = line.Qty,
                    UomId = line.UomId,
                    UnitCost = line.UnitCost,
                });
            }
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.PoId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23503")
        {
            return Result.Fail<long>(new("PO_FOREIGN_KEY", "مورد أو مخزن أو وحدة غير موجودة في الأمر"));
        }
    }

    public async Task<PoDetail?> FindPurchaseOrderAsync(long poId, CancellationToken ct = default)
    {
        var header = await (from po in _db.Set<PurchaseOrderRow>().AsNoTracking()
                            join sup in _db.Set<SupplierRow>().AsNoTracking() on po.SupplierId equals sup.SupplierId
                            join wh in _db.Warehouses.AsNoTracking() on po.WarehouseId equals wh.WarehouseId
                            join usr in _db.Users.AsNoTracking() on po.CreatedBy equals usr.UserId
                            where po.PoId == poId
                            select new
                            {
                                po.PoId,
                                po.PoNumber,
                                po.SupplierId,
                                sup.Code,
                                SupplierNameAr = sup.NameAr,
                                po.WarehouseId,
                                WarehouseCode = wh.Code,
                                po.Status,
                                po.ExpectedDate,
                                CreatedByName = usr.Username,
                                po.CreatedAt,
                            }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }
        var lines = await (
            from l in _db.Set<PurchaseOrderLineRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on l.SkuId equals sku.SkuId
            join u in _db.Units.AsNoTracking() on l.UomId equals u.UomId
            where l.PoId == poId
            orderby sku.SkuCode
            select new PoLineInfo(l.SkuId, sku.SkuCode, sku.SkuNameAr ?? sku.SkuCode, l.Qty, l.UomId, u.Code, l.UnitCost)
            ).ToListAsync(ct);
        return new PoDetail(
            header.PoId, header.PoNumber, header.SupplierId, header.Code, header.SupplierNameAr,
            header.WarehouseId, header.WarehouseCode, header.Status, header.ExpectedDate,
            header.CreatedByName, header.CreatedAt, lines);
    }

    public async Task<IReadOnlyList<PoSummary>> ListPurchaseOrdersAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query =
            from po in _db.Set<PurchaseOrderRow>().AsNoTracking()
            join sup in _db.Set<SupplierRow>().AsNoTracking() on po.SupplierId equals sup.SupplierId
            join wh in _db.Warehouses.AsNoTracking() on po.WarehouseId equals wh.WarehouseId
            where status == null || po.Status == status
            orderby po.PoId descending
            select new PoSummary(
                po.PoId, po.PoNumber, po.SupplierId, sup.Code, sup.NameAr, po.WarehouseId, wh.Code,
                po.Status, po.ExpectedDate, po.CreatedAt);
        return [.. await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct)];
    }

    public async Task<Result<long>> ReplacePoLinesAsync(long poId, IReadOnlyList<PoLineDraft> lines, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Set<PurchaseOrderLineRow>().Where(l => l.PoId == poId).ExecuteDeleteAsync(ct);
            foreach (var line in lines)
            {
                _db.Set<PurchaseOrderLineRow>().Add(new PurchaseOrderLineRow
                {
                    PoId = poId,
                    SkuId = line.SkuId,
                    Qty = line.Qty,
                    UomId = line.UomId,
                    UnitCost = line.UnitCost,
                });
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return Result.Ok(0L);
    }

    public async Task<Result<long>> SubmitPoAsync(long poId, CancellationToken ct = default)
    {
        var rows = await _db.Set<PurchaseOrderRow>()
            .Where(p => p.PoId == poId && p.Status == "DRAFT")
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "ORDERED").SetProperty(p => p.ApprovedAt, _clock.GetUtcNow()), ct);
        return rows == 0 ? Result.Fail<long>(new("PO_NOT_DRAFT", "الأمر ليس في DRAFT")) : Result.Ok(0L);
    }

    public async Task<Result<long>> ClosePoAsync(long poId, long userId, string reasonAr, CancellationToken ct = default)
    {
        var rows = await _db.Set<PurchaseOrderRow>()
            .Where(p => p.PoId == poId && (p.Status == "ORDERED" || p.Status == "PARTIAL"))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "CLOSED"), ct);
        return rows == 0 ? Result.Fail<long>(new("PO_NOT_CLOSABLE", "الأمر ليس في ORDERED/PARTIAL")) : Result.Ok(0L);
    }

    public async Task<Result<long>> CancelPoAsync(long poId, CancellationToken ct = default)
    {
        var rows = await _db.Set<PurchaseOrderRow>()
            .Where(p => p.PoId == poId && (p.Status == "DRAFT" || p.Status == "ORDERED"))
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "CANCELLED"), ct);
        return rows == 0 ? Result.Fail<long>(new("PO_NOT_CANCELLABLE", "الأمر ليس في DRAFT/ORDERED")) : Result.Ok(0L);
    }

    /// <summary>بعد ترحيل استلام مرتبط: تغطية الأمر بنفس وحداته — كل صنف مغطى = RECEIVED وإلا PARTIAL.</summary>
    public async Task<Result<long>> OnReceiptPostedAsync(long? poId, CancellationToken ct = default)
    {
        if (poId is null)
        {
            return Result.Ok(0L);
        }
        var po = await _db.Set<PurchaseOrderRow>().FirstOrDefaultAsync(p => p.PoId == poId, ct);
        if (po is null || po.Status is not ("ORDERED" or "PARTIAL"))
        {
            return Result.Ok(0L);   // ليس أمراً حياً — لا تغيير حالة
        }
        var poLines = await _db.Set<PurchaseOrderLineRow>().Where(l => l.PoId == po.PoId).ToListAsync(ct);
        var received = await (
            from rl in _db.Set<PurchaseReceiptLineRow>()
            join r in _db.Set<PurchaseReceiptRow>() on rl.ReceiptId equals r.ReceiptId
            where r.PoId == po.PoId && r.Status == "POSTED"
            select new { rl.SkuId, rl.UomId, rl.Qty, rl.BaseQty }).ToListAsync(ct);

        var covered = poLines.All(pl =>
            received.Where(x => x.SkuId == pl.SkuId && x.UomId == pl.UomId).Sum(x => x.Qty) + 0.000001m >= pl.Qty);
        po.Status = covered ? "RECEIVED" : "PARTIAL";
        await _db.SaveChangesAsync(ct);
        return Result.Ok(0L);
    }

    // ── الاستلامات ───────────────────────────────────────────────────────

    public async Task<Result<long>> CreateReceiptAsync(ReceiptDraft draft, CancellationToken ct = default)
    {
        try
        {
            var number = await NextDocNumberAsync(DocNumberPrefixes.Receipt, ct);
            var row = new PurchaseReceiptRow
            {
                ReceiptNumber = number,
                SupplierId = draft.SupplierId,
                PoId = draft.PoId,
                WarehouseId = draft.WarehouseId,
                Status = "DRAFT",
                ReceivedBy = draft.UserId,
                Notes = draft.Notes,
            };
            _db.Set<PurchaseReceiptRow>().Add(row);
            await _db.SaveChangesAsync(ct);
            foreach (var line in draft.Lines)
            {
                _db.Set<PurchaseReceiptLineRow>().Add(new PurchaseReceiptLineRow
                {
                    ReceiptId = row.ReceiptId,
                    SkuId = line.SkuId,
                    Qty = line.Qty,
                    UomId = line.UomId,
                    ConversionFactor = line.ConversionFactor,
                    UnitCost = line.UnitCost,
                    LotCode = line.LotCode,
                    LotExpiry = line.LotExpiry,
                    BaseQty = line.BaseQty,
                    LineTotal = line.LineTotal,
                });
            }
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.ReceiptId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23503")
        {
            return Result.Fail<long>(new("RECEIPT_FOREIGN_KEY", "مورد أو مخزن أو وحدة أو صيغة غير موجودة في الاستلام"));
        }
    }

    public async Task<ReceiptDetail?> FindReceiptAsync(long receiptId, CancellationToken ct = default)
    {
        var header = await (from r in _db.Set<PurchaseReceiptRow>().AsNoTracking()
                            join sup in _db.Set<SupplierRow>().AsNoTracking() on r.SupplierId equals sup.SupplierId
                            join wh in _db.Warehouses.AsNoTracking() on r.WarehouseId equals wh.WarehouseId
                            join usr in _db.Users.AsNoTracking() on r.ReceivedBy equals usr.UserId
                            where r.ReceiptId == receiptId
                            select new
                            {
                                r.ReceiptId,
                                r.ReceiptNumber,
                                r.SupplierId,
                                sup.Code,
                                SupplierNameAr = sup.NameAr,
                                r.PoId,
                                PoNumber = _db.Set<PurchaseOrderRow>().Where(p => p.PoId == r.PoId).Select(p => p.PoNumber).FirstOrDefault(),
                                r.WarehouseId,
                                WarehouseCode = wh.Code,
                                r.Status,
                                r.Notes,
                                ReceivedByName = usr.Username,
                                CreatedAt = (DateTimeOffset?)(r.ReceivedAt ?? r.PostedAt),
                                r.PostedAt,
                            }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }
        var lines = await (
            from l in _db.Set<PurchaseReceiptLineRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on l.SkuId equals sku.SkuId
            join u in _db.Units.AsNoTracking() on l.UomId equals u.UomId
            where l.ReceiptId == receiptId
            orderby sku.SkuCode
            select new ReceiptLineInfo(
                l.LineId, l.SkuId, sku.SkuCode, sku.SkuNameAr ?? sku.SkuCode, l.Qty, l.UomId, u.Code,
                l.ConversionFactor, l.UnitCost, l.LotId, l.LotCode, l.LotExpiry, l.BaseQty, l.LineTotal)
            ).ToListAsync(ct);
        return new ReceiptDetail(
            header.ReceiptId, header.ReceiptNumber, header.SupplierId, header.Code, header.SupplierNameAr,
            header.PoId, header.PoNumber, header.WarehouseId, header.WarehouseCode, header.Status, header.Notes,
            header.ReceivedByName, header.CreatedAt, header.PostedAt, lines);
    }

    public async Task<IReadOnlyList<ReceiptSummary>> ListReceiptsAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query =
            from r in _db.Set<PurchaseReceiptRow>().AsNoTracking()
            join sup in _db.Set<SupplierRow>().AsNoTracking() on r.SupplierId equals sup.SupplierId
            join wh in _db.Warehouses.AsNoTracking() on r.WarehouseId equals wh.WarehouseId
            where status == null || r.Status == status
            orderby r.ReceiptId descending
            select new ReceiptSummary(
                r.ReceiptId, r.ReceiptNumber, r.SupplierId, sup.Code, sup.NameAr, r.WarehouseId, wh.Code,
                r.Status, (DateTimeOffset?)(r.ReceivedAt ?? r.PostedAt), r.PostedAt);
        return [.. await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct)];
    }

    /// <summary>الترحيل الذري للاستلام: حركات RECEIVE + دفعات + WAC + حالة الأمر (بوابة القبول: لا تكرار استلام).</summary>
    public async Task<Result<PostedReceipt>> PostReceiptAsync(ReceiptPostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var receipt = await _db.Set<PurchaseReceiptRow>()
                    .FromSql($"SELECT * FROM purch.purchase_receipts WHERE receipt_id = {plan.ReceiptId} FOR UPDATE")
                    .FirstAsync(ct);
                if (receipt.Status != "DRAFT")
                {
                    throw new PurchasingPolicyException("RECEIPT_ALREADY_POSTED", "الاستلام مرحّل بالفعل — لا تكرار استلام");
                }

                var movementIds = new List<long>();
                var lotIds = new List<long>();
                decimal totalBaseQty = 0, totalCost = 0;

                foreach (var m in plan.Movements)
                {
                    // دفعة اختيارية: تُدرج بتفرد (sku, lot_code) وتُربط بالحركة
                    long? lotId = null;
                    if (!string.IsNullOrWhiteSpace(m.LotCode))
                    {
                        var existing = await _db.Set<LotRow>()
                            .FirstOrDefaultAsync(l => l.SkuId == m.TargetSkuId && l.LotCode == m.LotCode, ct);
                        if (existing is null)
                        {
                            existing = new LotRow
                            {
                                SkuId = m.TargetSkuId,
                                LotCode = m.LotCode,
                                ExpiryDate = m.LotExpiry,
                                ReceivedAt = _clock.GetUtcNow(),
                            };
                            _db.Set<LotRow>().Add(existing);
                            await _db.SaveChangesAsync(ct);
                        }
                        lotId = existing.LotId;
                        lotIds.Add(existing.LotId);
                    }

                    var movement = new StockMovementRow
                    {
                        SkuId = m.TargetSkuId,
                        WarehouseId = receipt.WarehouseId,
                        MovementType = "RECEIVE",
                        Direction = 1,
                        BaseQty = decimal.Round(m.BaseQty, 6),
                        SourceUomId = m.SourceUomId,
                        ConversionFactor = m.ConversionFactor,
                        SourceDocType = "purch.receipt",
                        SourceDocId = plan.ReceiptId,
                        SourceLineRef = m.LineId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        LotId = lotId,
                        Reason = $"استلام {receipt.ReceiptNumber}",
                        BranchId = plan.BranchId,
                        UserId = plan.UserId,
                        OccurredAt = _clock.GetUtcNow(),
                    };
                    _db.StockMovements.Add(movement);
                    await _db.SaveChangesAsync(ct);
                    movementIds.Add(movement.MovementId);
                    totalBaseQty += m.BaseQty;
                    totalCost += m.BaseQty * m.BaseUnitCost;

                    // ربط الدفعة بسطر الاستلام
                    if (lotId is { } lid)
                    {
                        var lineRow = await _db.Set<PurchaseReceiptLineRow>().FirstAsync(l => l.LineId == m.LineId, ct);
                        lineRow.LotId = lid;
                    }

                    // WAC (D-02 أ): يُحدَّث مع كل استلام على صنف الأثر (المحتوى عند PACK)
                    var layer = await _db.Set<CostLayerRow>()
                        .FromSql($"SELECT * FROM finance.cost_layers WHERE sku_id = {m.TargetSkuId} AND warehouse_id = {receipt.WarehouseId} FOR UPDATE")
                        .FirstOrDefaultAsync(ct);
                    var newState = layer is null
                        ? new WacState(0, 0)
                        : new WacState(layer.QtyOnLayer, layer.AvgUnitCost);
                    var updated = WacMath.ApplyReceipt(newState, m.BaseQty, m.BaseUnitCost);
                    if (layer is null)
                    {
                        _db.Set<CostLayerRow>().Add(new CostLayerRow
                        {
                            SkuId = m.TargetSkuId,
                            WarehouseId = receipt.WarehouseId,
                            CostingMethod = "WAC",
                            AvgUnitCost = updated.AvgUnitCost,
                            QtyOnLayer = updated.QtyOnLayer,
                            LastReceiptAt = _clock.GetUtcNow(),
                            UpdatedAt = _clock.GetUtcNow(),
                        });
                    }
                    else
                    {
                        layer.AvgUnitCost = updated.AvgUnitCost;
                        layer.QtyOnLayer = updated.QtyOnLayer;
                        layer.LastReceiptAt = _clock.GetUtcNow();
                        layer.UpdatedAt = _clock.GetUtcNow();
                    }
                    await _db.SaveChangesAsync(ct);
                }

                receipt.Status = "POSTED";
                receipt.ReceivedAt = _clock.GetUtcNow();
                receipt.PostedAt = _clock.GetUtcNow();
                receipt.IdempotencyKey = plan.IdempotencyKey;
                await _db.SaveChangesAsync(ct);

                await OnReceiptPostedAsync(receipt.PoId, ct);
                await tx.CommitAsync(ct);

                return Result.Ok(new PostedReceipt(
                    plan.ReceiptId, receipt.ReceiptNumber, [.. movementIds], [.. lotIds],
                    plan.Movements.Count, decimal.Round(totalBaseQty, 6), decimal.Round(totalCost, 4)));
            });
        }
        catch (PurchasingPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            throw new PurchasingPolicyException("IDEMPOTENCY_KEY_CONFLICT", "مفتاح Idempotency مستخدم في ترحيل آخر — لا يُعاد استخدامه");
        }
    }

    public async Task<Result<long>> CancelReceiptAsync(long receiptId, CancellationToken ct = default)
    {
        var rows = await _db.Set<PurchaseReceiptRow>()
            .Where(r => r.ReceiptId == receiptId && r.Status == "DRAFT")
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "CANCELLED"), ct);
        return rows == 0 ? Result.Fail<long>(new("RECEIPT_NOT_DRAFT", "الإلغاء من DRAFT فقط")) : Result.Ok(0L);
    }

    // ── فواتير الموردين ──────────────────────────────────────────────────

    public async Task<Result<long>> CreateInvoiceAsync(InvoiceDraft draft, CancellationToken ct = default)
    {
        try
        {
            var row = new SupplierInvoiceRow
            {
                InvoiceNumber = draft.InvoiceNumber,
                SupplierId = draft.SupplierId,
                ReceiptId = draft.ReceiptId,
                Subtotal = draft.Subtotal,
                TaxTotal = draft.TaxTotal,
                GrandTotal = decimal.Round(draft.Subtotal + draft.TaxTotal, 4),
                Status = "DRAFT",
                InvoiceDate = draft.InvoiceDate,
                DueDate = draft.DueDate,
                CreatedBy = draft.UserId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Set<SupplierInvoiceRow>().Add(row);
            await _db.SaveChangesAsync(ct);
            foreach (var line in draft.Lines)
            {
                _db.Set<SupplierInvoiceLineRow>().Add(new SupplierInvoiceLineRow
                {
                    InvoiceId = row.InvoiceId,
                    SkuId = line.SkuId,
                    Qty = line.Qty,
                    UomId = line.UomId,
                    UnitCost = line.UnitCost,
                    LineTotal = decimal.Round(line.Qty * line.UnitCost, 4),
                });
            }
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.InvoiceId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            if (pg.SqlState == "23505")
            {
                return Result.Fail<long>(new("INVOICE_NUMBER_EXISTS", $"رقم الفاتورة {draft.InvoiceNumber} مسجل مسبقاً لهذا المورد"));
            }
            return Result.Fail<long>(new("INVOICE_FOREIGN_KEY", "مرجع غير موجود في الفاتورة (مورد/استلام/وحدة)"));
        }
    }

    public async Task<InvoiceDetail?> FindInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        var header = await (from i in _db.Set<SupplierInvoiceRow>().AsNoTracking()
                            join sup in _db.Set<SupplierRow>().AsNoTracking() on i.SupplierId equals sup.SupplierId
                            where i.InvoiceId == invoiceId
                            select new
                            {
                                i.InvoiceId,
                                i.SupplierId,
                                sup.Code,
                                SupplierNameAr = sup.NameAr,
                                i.ReceiptId,
                                ReceiptNumber = _db.Set<PurchaseReceiptRow>().Where(r => r.ReceiptId == i.ReceiptId).Select(r => r.ReceiptNumber).FirstOrDefault(),
                                i.InvoiceNumber,
                                i.InvoiceDate,
                                i.DueDate,
                                i.Subtotal,
                                i.TaxTotal,
                                i.GrandTotal,
                                i.Status,
                                i.PostedAt,
                            }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }
        var paid = await PaidAmountAsync(invoiceId, ct);
        var lines = await (
            from l in _db.Set<SupplierInvoiceLineRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on l.SkuId equals sku.SkuId
            join u in _db.Units.AsNoTracking() on l.UomId equals u.UomId
            where l.InvoiceId == invoiceId
            orderby sku.SkuCode
            select new InvoiceLineInfo(l.SkuId, sku.SkuCode, l.Qty, l.UomId, u.Code, l.UnitCost, l.LineTotal)
            ).ToListAsync(ct);
        return new InvoiceDetail(
            header.InvoiceId, header.SupplierId, header.Code, header.SupplierNameAr,
            header.ReceiptId, header.ReceiptNumber, header.InvoiceNumber, header.InvoiceDate, header.DueDate,
            header.Subtotal, header.TaxTotal, header.GrandTotal, paid, header.GrandTotal - paid,
            header.Status, header.PostedAt, lines);
    }

    private async Task<decimal> PaidAmountAsync(long invoiceId, CancellationToken ct)
    {
        // السداد النقدي المخصص (allocations من سدادات مرحّلة)
        var cash = await (from a in _db.Set<SupplierPaymentAllocationRow>().AsNoTracking()
                          join p in _db.Set<SupplierPaymentRow>().AsNoTracking() on a.PaymentId equals p.PaymentId
                          where a.InvoiceId == invoiceId && p.Status == "POSTED"
                          select a.Amount).SumAsync(ct);
        // الإشعار الدائن المرتبط بهذه الفاتورة يغطي ما تبقى منها — بلا نقد (§12.3 ق3)
        var creditNotes = await (
            from r in _db.Set<SupplierReturnRow>().AsNoTracking()
            where r.InvoiceId == invoiceId && r.Status == "POSTED" && r.RefundMethod == "CREDIT_NOTE"
            select r.Total).SumAsync(ct);
        return cash + creditNotes;
    }

    public async Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(string? status, long? supplierId, int page, int pageSize, CancellationToken ct = default)
    {
        var query =
            from i in _db.Set<SupplierInvoiceRow>().AsNoTracking()
            join sup in _db.Set<SupplierRow>().AsNoTracking() on i.SupplierId equals sup.SupplierId
            where (status == null || i.Status == status) && (supplierId == null || i.SupplierId == supplierId)
            orderby i.InvoiceId descending
            select new InvoiceSummary(i.InvoiceId, i.SupplierId, sup.Code, sup.NameAr, i.InvoiceNumber, i.InvoiceDate, i.GrandTotal, 0m, i.Status);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var result = new List<InvoiceSummary>();
        foreach (var r in rows)
        {
            var paid = await PaidAmountAsync(r.InvoiceId, ct);
            result.Add(new InvoiceSummary(r.InvoiceId, r.SupplierId, r.SupplierCode, r.SupplierNameAr, r.InvoiceNumber, r.InvoiceDate, r.GrandTotal, paid, r.Status));
        }
        return result;
    }

    public async Task<Result<PostedInvoice>> PostInvoiceAsync(long invoiceId, string? idempotencyKey, long userId, CancellationToken ct = default)
    {
        var rows = await _db.Set<SupplierInvoiceRow>()
            .Where(i => i.InvoiceId == invoiceId && i.Status == "DRAFT")
            .ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Status, "POSTED")
                .SetProperty(i => i.PostedAt, _clock.GetUtcNow())
                .SetProperty(i => i.IdempotencyKey, idempotencyKey), ct);
        return rows == 0
            ? Result.Fail<PostedInvoice>(new("INVOICE_ALREADY_POSTED", "الفاتورة مرحّلة بالفعل — لا تكرار التزام"))
            : Result.Ok(new PostedInvoice(invoiceId, "POSTED",
                (await _db.Set<SupplierInvoiceRow>().Where(i => i.InvoiceId == invoiceId).Select(i => i.GrandTotal).FirstAsync(ct))));
    }

    public async Task<Result<long>> CancelInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        var rows = await _db.Set<SupplierInvoiceRow>()
            .Where(i => i.InvoiceId == invoiceId && i.Status == "DRAFT")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, "CANCELLED"), ct);
        return rows == 0 ? Result.Fail<long>(new("INVOICE_NOT_DRAFT", "الإلغاء من DRAFT فقط")) : Result.Ok(0L);
    }

    public async Task<Result<long>> RecomputeInvoiceStatusAsync(long invoiceId, CancellationToken ct = default)
    {
        var invoice = await _db.Set<SupplierInvoiceRow>().FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct);
        if (invoice is null)
        {
            return Result.Fail<long>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"));
        }
        var paid = await PaidAmountAsync(invoiceId, ct);
        invoice.Status = PaymentAllocationRules.InvoiceStatusAfter(invoice.GrandTotal, paid);
        await _db.SaveChangesAsync(ct);
        return Result.Ok(0L);
    }

    // ── السداد ───────────────────────────────────────────────────────────

    public async Task<Result<long>> CreatePaymentAsync(PaymentDraft draft, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var number = await NextDocNumberAsync(DocNumberPrefixes.PaymentVoucher, ct);
                var row = new SupplierPaymentRow
                {
                    VoucherNumber = number,
                    SupplierId = draft.SupplierId,
                    TreasuryAccountId = draft.TreasuryAccountId,
                    Amount = draft.Amount,
                    PaymentMethod = draft.PaymentMethod,
                    PaymentDate = draft.PaymentDate,
                    Status = "DRAFT",
                    Notes = draft.Notes,
                    CreatedBy = draft.UserId,
                    CreatedAt = _clock.GetUtcNow(),
                };
                _db.Set<SupplierPaymentRow>().Add(row);
                await _db.SaveChangesAsync(ct);
                foreach (var alloc in draft.Allocations)
                {
                    _db.Set<SupplierPaymentAllocationRow>().Add(new SupplierPaymentAllocationRow
                    {
                        PaymentId = row.PaymentId,
                        InvoiceId = alloc.InvoiceId,
                        Amount = alloc.Amount,
                    });
                }
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);   // مشغّلا الحماية المؤجلان يفحصان هنا (V004 + V017)
                return Result.Ok(row.PaymentId);
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23514")
        {
            var raw = pg.MessageText ?? string.Empty;
            var code = raw.Contains("PAYMENT_OVER_ALLOCATION") ? "PAYMENT_OVER_ALLOCATION"
                : raw.Contains("INVOICE_OVER_PAYMENT") ? "INVOICE_OVER_PAYMENT" : "ALLOCATION_GUARD";
            var msg = raw.Contains("INVOICE_OVER_PAYMENT")
                ? "المخصص على الفاتورة يتجاوز قيمتها — لا تخصيص بأكثر من المتبقي (§13)"
                : "مجموع التخصيصات يتجاوز قيمة السداد (§13)";
            throw new PurchasingPolicyException(code, msg);
        }
    }

    public async Task<PaymentDetail?> FindPaymentAsync(long paymentId, CancellationToken ct = default)
    {
        var header = await (from p in _db.Set<SupplierPaymentRow>().AsNoTracking()
                            join sup in _db.Set<SupplierRow>().AsNoTracking() on p.SupplierId equals sup.SupplierId
                            join t in _db.Set<TreasuryAccountRow>().AsNoTracking() on p.TreasuryAccountId equals t.TreasuryAccountId
                            where p.PaymentId == paymentId
                            select new
                            {
                                p.PaymentId,
                                p.VoucherNumber,
                                p.SupplierId,
                                sup.Code,
                                SupplierNameAr = sup.NameAr,
                                p.TreasuryAccountId,
                                TreasuryAccountCode = t.Code,
                                p.Amount,
                                p.PaymentMethod,
                                p.PaymentDate,
                                p.Status,
                                p.Notes,
                                p.PostedAt,
                            }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }
        var allocations = await (
            from a in _db.Set<SupplierPaymentAllocationRow>().AsNoTracking()
            join i in _db.Set<SupplierInvoiceRow>().AsNoTracking() on a.InvoiceId equals i.InvoiceId
            where a.PaymentId == paymentId
            select new PaymentAllocationInfo(a.InvoiceId, i.InvoiceNumber, a.Amount)
            ).ToListAsync(ct);
        return new PaymentDetail(
            header.PaymentId, header.VoucherNumber, header.SupplierId, header.Code, header.SupplierNameAr,
            header.TreasuryAccountId, header.TreasuryAccountCode, header.Amount, header.PaymentMethod,
            header.PaymentDate, header.Status, header.Notes, header.PostedAt, allocations);
    }

    public async Task<IReadOnlyList<PaymentSummary>> ListPaymentsAsync(long? supplierId, string? status, CancellationToken ct = default)
    {
        var query =
            from p in _db.Set<SupplierPaymentRow>().AsNoTracking()
            join sup in _db.Set<SupplierRow>().AsNoTracking() on p.SupplierId equals sup.SupplierId
            where (supplierId == null || p.SupplierId == supplierId) && (status == null || p.Status == status)
            orderby p.PaymentId descending
            select new PaymentSummary(p.PaymentId, p.VoucherNumber, p.SupplierId, sup.Code, sup.NameAr, p.Amount, p.PaymentMethod, p.PaymentDate, p.Status);
        return [.. await query.ToListAsync(ct)];
    }

    /// <summary>الترحيل الذري للسداد: أثر خزينة واحد + حالات الفواتير المخصَّصة (بوابة القبول: لا تكرار دفع).</summary>
    public async Task<Result<PostedPayment>> PostPaymentAsync(PaymentPostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var payment = await _db.Set<SupplierPaymentRow>()
                    .FromSql($"SELECT * FROM purch.supplier_payments WHERE payment_id = {plan.PaymentId} FOR UPDATE")
                    .FirstAsync(ct);
                if (payment.Status != "DRAFT")
                {
                    throw new PurchasingPolicyException("PAYMENT_ALREADY_POSTED", "السداد مرحّل بالفعل — لا تكرار دفع");
                }

                // الأثر المالي الواحد: خروج من الخزينة/البنك — لا يُسجل مصروفاً (§12.3 ق3)
                var txn = new TreasuryTxnRow
                {
                    TreasuryAccountId = payment.TreasuryAccountId,
                    Direction = -1,
                    Amount = payment.Amount,
                    SourceDocType = "purch.payment",
                    SourceDocId = plan.PaymentId,
                    Reason = $"سداد للمورد — سند {payment.VoucherNumber}",
                    OccurredAt = _clock.GetUtcNow(),
                    CreatedBy = plan.UserId,
                    BranchId = plan.BranchId,
                };
                _db.Set<TreasuryTxnRow>().Add(txn);

                payment.Status = "POSTED";
                payment.PostedAt = _clock.GetUtcNow();
                payment.IdempotencyKey = plan.IdempotencyKey;
                await _db.SaveChangesAsync(ct);

                // حالات الفواتير المخصَّصة تُعاد حسابها من التخصيصات المرحّلة
                var affected = new List<long>();
                var allocations = await _db.Set<SupplierPaymentAllocationRow>()
                    .Where(a => a.PaymentId == plan.PaymentId).ToListAsync(ct);
                foreach (var alloc in allocations)
                {
                    await RecomputeInvoiceStatusAsync(alloc.InvoiceId, ct);
                    affected.Add(alloc.InvoiceId);
                }
                await tx.CommitAsync(ct);
                return Result.Ok(new PostedPayment(plan.PaymentId, payment.VoucherNumber, txn.TxnId, [.. affected]));
            });
        }
        catch (PurchasingPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            throw new PurchasingPolicyException("IDEMPOTENCY_KEY_CONFLICT", "مفتاح Idempotency مستخدم في ترحيل آخر — لا يُعاد استخدامه");
        }
    }

    public async Task<Result<long>> CancelPaymentAsync(long paymentId, CancellationToken ct = default)
    {
        var rows = await _db.Set<SupplierPaymentRow>()
            .Where(p => p.PaymentId == paymentId && p.Status == "DRAFT")
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "CANCELLED"), ct);
        return rows == 0 ? Result.Fail<long>(new("PAYMENT_NOT_DRAFT", "الإلغاء من DRAFT فقط")) : Result.Ok(0L);
    }

    // ── المرتجعات ────────────────────────────────────────────────────────

    public async Task<Result<long>> CreateReturnAsync(ReturnDraft draft, CancellationToken ct = default)
    {
        try
        {
            var number = await NextDocNumberAsync(DocNumberPrefixes.SupplierReturn, ct);
            var row = new SupplierReturnRow
            {
                ReturnNumber = number,
                SupplierId = draft.SupplierId,
                ReceiptId = draft.ReceiptId,
                InvoiceId = draft.InvoiceId,
                WarehouseId = draft.WarehouseId,
                Status = "DRAFT",
                RefundMethod = draft.RefundMethod,
                Total = decimal.Round(draft.Lines.Sum(l => l.LineTotal), 4),
                CreatedBy = draft.UserId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Set<SupplierReturnRow>().Add(row);
            await _db.SaveChangesAsync(ct);
            foreach (var line in draft.Lines)
            {
                _db.Set<SupplierReturnLineRow>().Add(new SupplierReturnLineRow
                {
                    ReturnId = row.ReturnId,
                    SkuId = line.SkuId,
                    Qty = line.Qty,
                    UomId = line.UomId,
                    ConversionFactor = line.ConversionFactor,
                    UnitCost = line.UnitCost,
                    LineTotal = line.LineTotal,
                    TargetSkuId = line.TargetSkuId,
                    BaseQty = line.BaseQty,
                });
            }
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.ReturnId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23503")
        {
            return Result.Fail<long>(new("RETURN_FOREIGN_KEY", "مرجع غير موجود في المرتجع (مورد/استلام/وحدة)"));
        }
    }

    public async Task<ReturnDetail?> FindReturnAsync(long returnId, CancellationToken ct = default)
    {
        var header = await (from r in _db.Set<SupplierReturnRow>().AsNoTracking()
                            join sup in _db.Set<SupplierRow>().AsNoTracking() on r.SupplierId equals sup.SupplierId
                            join wh in _db.Warehouses.AsNoTracking() on r.WarehouseId equals wh.WarehouseId
                            join usr in _db.Users.AsNoTracking() on r.CreatedBy equals usr.UserId
                            where r.ReturnId == returnId
                            select new
                            {
                                r.ReturnId,
                                r.ReturnNumber,
                                r.SupplierId,
                                sup.Code,
                                SupplierNameAr = sup.NameAr,
                                r.ReceiptId,
                                ReceiptNumber = _db.Set<PurchaseReceiptRow>().Where(x => x.ReceiptId == r.ReceiptId).Select(x => x.ReceiptNumber).FirstOrDefault(),
                                r.InvoiceId,
                                InvoiceNumber = _db.Set<SupplierInvoiceRow>().Where(x => x.InvoiceId == r.InvoiceId).Select(x => x.InvoiceNumber).FirstOrDefault(),
                                r.WarehouseId,
                                WarehouseCode = wh.Code,
                                r.Status,
                                RefundMethod = r.RefundMethod ?? "CREDIT_NOTE",
                                r.Total,
                                CreatedByName = usr.Username,
                                r.CreatedAt,
                                r.PostedAt,
                            }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }
        var lines = await (
            from l in _db.Set<SupplierReturnLineRow>().AsNoTracking()
            join sku in _db.Skus.AsNoTracking() on l.SkuId equals sku.SkuId
            join u in _db.Units.AsNoTracking() on l.UomId equals u.UomId
            where l.ReturnId == returnId
            orderby sku.SkuCode
            select new ReturnLineInfo(l.LineId, l.SkuId, sku.SkuCode, l.Qty, l.UomId, u.Code, l.ConversionFactor, l.UnitCost, l.LineTotal)
            ).ToListAsync(ct);
        return new ReturnDetail(
            header.ReturnId, header.ReturnNumber, header.SupplierId, header.Code, header.SupplierNameAr,
            header.ReceiptId, header.ReceiptNumber, header.InvoiceId, header.InvoiceNumber,
            header.WarehouseId, header.WarehouseCode, header.Status, header.RefundMethod, header.Total,
            header.CreatedByName, header.CreatedAt, header.PostedAt, lines);
    }

    public async Task<IReadOnlyList<ReturnSummary>> ListReturnsAsync(string? status, long? supplierId, CancellationToken ct = default)
    {
        var query =
            from r in _db.Set<SupplierReturnRow>().AsNoTracking()
            join sup in _db.Set<SupplierRow>().AsNoTracking() on r.SupplierId equals sup.SupplierId
            where (status == null || r.Status == status) && (supplierId == null || r.SupplierId == supplierId)
            orderby r.ReturnId descending
            select new ReturnSummary(r.ReturnId, r.ReturnNumber, r.SupplierId, sup.Code, sup.NameAr, r.Status, r.RefundMethod ?? "", r.Total, r.CreatedAt);
        return [.. await query.ToListAsync(ct)];
    }

    /// <summary>الترحيل الذري للمرتجع: حركة SUPPLIER_RETURN بحد ما استُلم من الأصل + أثر الخزينة للرد النقدي.</summary>
    public async Task<Result<PostedReturn>> PostReturnAsync(ReturnPostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var ret = await _db.Set<SupplierReturnRow>()
                    .FromSql($"SELECT * FROM purch.supplier_returns WHERE return_id = {plan.ReturnId} FOR UPDATE")
                    .FirstAsync(ct);
                if (ret.Status != "DRAFT")
                {
                    throw new PurchasingPolicyException("RETURN_ALREADY_POSTED", "المرتجع مرحّل بالفعل");
                }
                if (ret.ReceiptId is not { } originId)
                {
                    throw new PurchasingPolicyException("RETURN_ORIGIN_REQUIRED", "المرتجع يرتبط باستلام أصلي");
                }

                var movementIds = new List<long>();
                long? treasuryTxnId = null;

                foreach (var m in plan.Movements)
                {
                    // الحد: ما رُد على هذا الاستلام + هذا السطر ≤ ما استُلم منه لذات الصنف (بوابة القبول: رد بحد الأصل)
                    var receivedOnSku = await (
                        from rl in _db.Set<PurchaseReceiptLineRow>()
                        where rl.ReceiptId == originId && rl.SkuId == m.LineSkuId
                        select rl.BaseQty).SumAsync(ct);
                    var alreadyReturned = await (
                        from rline in _db.Set<SupplierReturnLineRow>()
                        join r2 in _db.Set<SupplierReturnRow>() on rline.ReturnId equals r2.ReturnId
                        where r2.ReceiptId == originId && r2.Status == "POSTED" && rline.SkuId == m.LineSkuId
                        select rline.BaseQty).SumAsync(ct);
                    if (alreadyReturned + m.BaseQty > receivedOnSku + 0.000001m)
                    {
                        throw new PurchasingPolicyException("RETURN_EXCEEDS_RECEIVED",
                            $"الرد على {m.SkuCode} يتجاوز ما استُلم من الأصل (استُلم {receivedOnSku:0.######} · رُد سابقاً {alreadyReturned:0.######}) — §12.3 ق4");
                    }

                    var movement = new StockMovementRow
                    {
                        SkuId = m.TargetSkuId,
                        WarehouseId = ret.WarehouseId,
                        MovementType = "SUPPLIER_RETURN",
                        Direction = -1,
                        BaseQty = decimal.Round(m.BaseQty, 6),
                        SourceUomId = m.SourceUomId,
                        ConversionFactor = m.ConversionFactor,
                        SourceDocType = "purch.return",
                        SourceDocId = plan.ReturnId,
                        SourceLineRef = m.LineId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Reason = $"مرتجع مورد على استلام {originId}",
                        BranchId = plan.BranchId,
                        UserId = plan.UserId,
                        OccurredAt = _clock.GetUtcNow(),
                    };
                    _db.StockMovements.Add(movement);
                    await _db.SaveChangesAsync(ct);
                    movementIds.Add(movement.MovementId);
                }

                // أثر مرة واحدة (§12.3): رد نقدي = خزينة تدخل بلا مساس بالذمم؛ إشعار دائن = ذمم تنخفض بلا نقد
                if (plan.IsCashRefund)
                {
                    var txn = new TreasuryTxnRow
                    {
                        TreasuryAccountId = 1, // يُستبدل أدناه بحساب الخزينة الافتراضي الأول النشط
                        Direction = 1,
                        Amount = plan.Total,
                        SourceDocType = "purch.return",
                        SourceDocId = plan.ReturnId,
                        Reason = $"رد نقدي لمرتجع {ret.ReturnNumber}",
                        OccurredAt = _clock.GetUtcNow(),
                        CreatedBy = plan.UserId,
                        BranchId = plan.BranchId,
                    };
                    // وجهة الرد النقدي: أول حساب خزينة نشط (BANK/CASH_DRAWER) — G8 يخصصها إدارياً
                    var account = await _db.Set<TreasuryAccountRow>()
                        .Where(t => t.IsActive && (t.AccountKind == "SAFE" || t.AccountKind == "CASH_DRAWER" || t.AccountKind == "BANK"))
                        .OrderBy(t => t.TreasuryAccountId).FirstOrDefaultAsync(ct);
                    if (account is null)
                    {
                        throw new PurchasingPolicyException("TREASURY_ACCOUNT_REQUIRED", "الرد النقدي يوجب حساب خزينة نشطاً — تُنشأ في G8");
                    }
                    txn.TreasuryAccountId = account.TreasuryAccountId;
                    _db.Set<TreasuryTxnRow>().Add(txn);
                    await _db.SaveChangesAsync(ct);
                    treasuryTxnId = txn.TxnId;
                }

                ret.Status = "POSTED";
                ret.PostedAt = _clock.GetUtcNow();
                ret.IdempotencyKey = plan.IdempotencyKey;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                return Result.Ok(new PostedReturn(
                    plan.ReturnId, [.. movementIds], treasuryTxnId, !plan.IsCashRefund, plan.Total));
            });
        }
        catch (PurchasingPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            throw new PurchasingPolicyException("IDEMPOTENCY_KEY_CONFLICT", "مفتاح Idempotency مستخدم في ترحيل آخر");
        }
    }

    public async Task<Result<long>> CancelReturnAsync(long returnId, CancellationToken ct = default)
    {
        var rows = await _db.Set<SupplierReturnRow>()
            .Where(r => r.ReturnId == returnId && r.Status == "DRAFT")
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "CANCELLED"), ct);
        return rows == 0 ? Result.Fail<long>(new("RETURN_NOT_DRAFT", "الإلغاء من DRAFT فقط")) : Result.Ok(0L);
    }

    // ── حسابات الخزينة (قراءة — إنشاؤها في G8) ────────────────────────────

    public async Task<IReadOnlyList<TreasuryAccountRecord>> ListTreasuryAccountsAsync(bool activeOnly, CancellationToken ct = default)
    {
        var rows = await _db.Set<TreasuryAccountRow>().AsNoTracking()
            .Where(t => !activeOnly || t.IsActive)
            .OrderBy(t => t.Code)
            .ToListAsync(ct);
        return [.. rows.Select(t => new TreasuryAccountRecord(
            t.TreasuryAccountId, t.Code, t.NameAr, t.AccountKind, t.BranchId, t.Currency, t.IsActive))];
    }

    // ── المشترك ──────────────────────────────────────────────────────────

    private async Task<string> NextDocNumberAsync(string prefix, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT nextval('purch.g6_doc_seq')";
        var value = Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        return $"{prefix}{value}";
    }
}

/// <summary>مستودع العملاء (G6 — PAGE-29/30): السجل والتعطيل وكشف الحساب من مستنداته.</summary>
public sealed class CustomerRepository : ICustomerRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public CustomerRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<(IReadOnlyList<CustomerRecord> Items, long Total)> SearchCustomersAsync(string? query, int page, int pageSize, CancellationToken ct = default)
    {
        var rows = _db.Set<CustomerRow>().AsNoTracking().OrderBy(c => c.Code).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = rows.Where(c => c.Code.Contains(q) || c.NameAr.Contains(q) || (c.Phone != null && c.Phone.Contains(q)));
        }
        var total = await rows.LongCountAsync(ct);
        var pageRows = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return ([.. pageRows.Select(ToRecord)], total);
    }

    public async Task<Result<long>> CreateCustomerAsync(CustomerDraft draft, CancellationToken ct = default)
    {
        try
        {
            var row = new CustomerRow
            {
                Code = draft.Code,
                NameAr = draft.NameAr,
                Phone = draft.Phone,
                Email = draft.Email,
                TaxId = draft.TaxId,
                CreditLimit = draft.CreditLimit,
                CreditEnabled = draft.CreditEnabled,
                PaymentTermsDays = draft.PaymentTermsDays,
                Notes = draft.Notes,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.Set<CustomerRow>().Add(row);
            await _db.SaveChangesAsync(ct);
            return Result.Ok(row.CustomerId);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return Result.Fail<long>(new(
                pg.SqlState == "23505" ? "CUSTOMER_CODE_EXISTS" : "CUSTOMER_CREATE_FAILED",
                pg.SqlState == "23505" ? $"كود العميل {draft.Code} مستخدم مسبقاً" : "تعذر إنشاء العميل"));
        }
    }

    public async Task<Result<long>> SetCustomerStatusAsync(long customerId, string status, string reasonAr, long userId, CancellationToken ct = default)
    {
        var rows = await _db.Set<CustomerRow>()
            .Where(c => c.CustomerId == customerId)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.AccountStatus, status).SetProperty(c => c.UpdatedAt, _clock.GetUtcNow()), ct);
        return rows == 0
            ? Result.Fail<long>(new("CUSTOMER_NOT_FOUND", "العميل غير موجود"))
            : Result.Ok(customerId);
    }

    public async Task<CustomerRecord?> FindCustomerAsync(long customerId, CancellationToken ct = default)
    {
        var row = await _db.Set<CustomerRow>().AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
        return row is null ? null : ToRecord(row);
    }

    /// <summary>كشف حساب العميل من فواتيره (G7 تملأها) — في G6 البنية جاهزة والرصيد صفر بصدق.</summary>
    public async Task<CustomerStatement> GetCustomerStatementAsync(long customerId, DateOnly fromDate, DateOnly toDate, string? docType, CancellationToken ct = default)
    {
        var name = (await _db.Set<CustomerRow>().AsNoTracking().Where(c => c.CustomerId == customerId).Select(c => c.NameAr).FirstOrDefaultAsync(ct)) ?? string.Empty;
        // الفواتير المرحّلة للعميل (+) — فواتير البيع تأتي في G7؛ البنية تعمل على الجداول الحقيقية
        var sql = """
            SELECT 'sales.invoice', invoice_id, invoice_number,
                   COALESCE((posted_at::date), (created_at::date)) AS doc_date,
                   (gross_total - returned_total) AS amount
              FROM sales.invoices
             WHERE customer_id = @cid AND status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
             ORDER BY doc_date, invoice_id
            """;
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("cid", customerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var all = new List<StatementLine>();
        while (await reader.ReadAsync(ct))
        {
            all.Add(new StatementLine(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                DateOnly.FromDateTime(reader.GetDateTime(3)), reader.GetDecimal(4), "فاتورة بيع"));
        }

        if (!string.IsNullOrWhiteSpace(docType))
        {
            all = [.. all.Where(e => e.DocType == docType.Trim())];
        }
        List<StatementLine> before = [.. all.Where(e => e.Date < fromDate)];
        List<StatementLine> within = [.. all.Where(e => e.Date >= fromDate && e.Date <= toDate)];
        var opening = StatementMath.OpeningBalance(before.Select(e => e.Amount));
        var closing = StatementMath.ClosingBalance(opening, within.Select(e => e.Amount));
        return new CustomerStatement(
            customerId, name, fromDate, toDate, opening,
            within.Where(e => e.Amount > 0).Sum(e => e.Amount),
            -within.Where(e => e.Amount < 0).Sum(e => e.Amount),
            closing, within);
    }

    private static CustomerRecord ToRecord(CustomerRow r) => new(
        r.CustomerId, r.Code, r.NameAr, r.Phone, r.Email, r.TaxId,
        r.CreditLimit, r.CreditEnabled, r.PaymentTermsDays, r.AccountStatus, r.Notes, r.CreatedAt);
}
