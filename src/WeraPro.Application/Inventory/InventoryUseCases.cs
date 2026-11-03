namespace WeraPro.Application.Inventory;

using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Catalog;
using WeraPro.Domain.Inventory;
using WeraPro.Domain.SharedKernel;

// ═══════════════════════════════════════════════════════════════════════════
// حالات استخدام المخزون (G6 — §12.1/§12.2): التحقق كله هنا قبل أي كتابة،
// والتنفيذ الذري في المستودع — نمط G4/G5 المحافظ.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>إدارة المخازن (PAGE-28): قائمة/إنشاء/تعطيل — لا حذف (الأرشيف تعطيل).</summary>
public sealed class ManageWarehousesUseCase
{
    private readonly IInventoryRepository _inventory;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageWarehousesUseCase(IInventoryRepository inventory, IAuditWriter audit, ICurrentUser user)
    {
        _inventory = inventory;
        _audit = audit;
        _user = user;
    }

    public async Task<IReadOnlyList<Contracts.V1.WarehouseDto>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var rows = await _inventory.ListWarehousesAsync(includeInactive, ct);
        return [.. rows.Select(r => new Contracts.V1.WarehouseDto(
            r.WarehouseId, r.BranchId, r.BranchCode, r.Code, r.NameAr, r.WhType, r.IsActive))];
    }

    public async Task<Result<Contracts.V1.WarehouseDto>> CreateAsync(Contracts.V1.CreateWarehouseRequest request, string? correlationId, CancellationToken ct = default)
    {
        var code = request.Code?.Trim() ?? string.Empty;
        if (code.Length is < 2 or > 24)
        {
            return Result.Fail<Contracts.V1.WarehouseDto>(new("WAREHOUSE_CODE_INVALID", "كود المخزن بين محرفين و24"));
        }
        if (string.IsNullOrWhiteSpace(request.NameAr) || request.NameAr.Trim().Length < 2)
        {
            return Result.Fail<Contracts.V1.WarehouseDto>(new("WAREHOUSE_NAME_REQUIRED", "اسم المخزن مطلوب (محرفان على الأقل)"));
        }
        if (!WarehouseTypes.All.Contains(request.WhType))
        {
            return Result.Fail<Contracts.V1.WarehouseDto>(new(
                "WAREHOUSE_TYPE_INVALID",
                "نوع المخزن يجب أن يكون MAIN أو BRANCH أو SUB أو EXTERNAL (§12.2 ق1)"));
        }

        long warehouseId;
        try
        {
            var created = await _inventory.CreateWarehouseAsync(new WarehouseDraft(
                request.BranchId, code.ToUpperInvariant(), request.NameAr.Trim(), request.WhType, _user.UserId), ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<Contracts.V1.WarehouseDto>(created.Error!);
            }
            warehouseId = created.Value;
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.WarehouseDto>(new(ex.Code, ex.UserMessageAr));
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "inv.warehouses", EntityId: warehouseId, Action: "WAREHOUSE_CREATE", Result: "SUCCESS",
            Reason: $"{request.WhType} {code}", CorrelationId: correlationId), ct);
        var rows = await _inventory.ListWarehousesAsync(includeInactive: true, ct);
        var row = rows.First(r => r.WarehouseId == warehouseId);
        return Result.Ok(new Contracts.V1.WarehouseDto(
            row.WarehouseId, row.BranchId, row.BranchCode, row.Code, row.NameAr, row.WhType, row.IsActive));
    }

    public async Task<Result<long>> SetStatusAsync(long warehouseId, Contracts.V1.SetWarehouseStatusRequest request, string? correlationId, CancellationToken ct = default)
    {
        var result = await _inventory.SetWarehouseStatusAsync(warehouseId, request.IsActive, request.ReasonAr ?? string.Empty, _user.UserId, ct);
        if (result.IsSuccess)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "inv.warehouses", EntityId: warehouseId, Action: "WAREHOUSE_STATUS_CHANGE",
                Result: "SUCCESS", Reason: request.IsActive ? "تنشيط" : "تعطيل " + (request.ReasonAr ?? string.Empty),
                CorrelationId: correlationId), ct);
        }
        return result;
    }
}

/// <summary>لوحة المخزون (PAGE-18): أرصدة من الدفتر + المحجوز + المتاح + مستوى إعادة الطلب.</summary>
public sealed class GetStockBalancesUseCase
{
    private readonly IInventoryRepository _inventory;

    public GetStockBalancesUseCase(IInventoryRepository inventory) => _inventory = inventory;

    public async Task<Result<Contracts.V1.StockBalanceSearchResponse>> SearchAsync(
        string? query, long? warehouseId, bool? belowReorder, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (items, total) = await _inventory.SearchBalancesAsync(
            query?.Trim(), warehouseId, belowReorder, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        var size = Math.Clamp(pageSize ?? 25, 1, 200);
        return Result.Ok(new Contracts.V1.StockBalanceSearchResponse(
            page ?? 1, size, total,
            (int)Math.Ceiling(total / (double)size),
            [.. items.Select(i => new Contracts.V1.StockBalanceDto(
                i.SkuId, i.SkuCode, i.ProductId, i.ProductCode, i.ProductNameAr, i.SkuNameAr, i.SaleMode,
                i.WarehouseId, i.WarehouseCode, i.BaseQty, i.ReservedQty, i.AvailableQty,
                i.ReorderLevel, i.BelowReorder, i.AvgUnitCost, i.Valuation))]));
    }
}

/// <summary>كرت حركة المنتج (PAGE-19) + تقييم WAC (§16.2 · D-02).</summary>
public sealed class GetInventoryReportsUseCase
{
    private readonly IInventoryRepository _inventory;

    public GetInventoryReportsUseCase(IInventoryRepository inventory) => _inventory = inventory;

    public async Task<Result<Contracts.V1.MovementCardResponse>> MovementCardAsync(long skuId, long? warehouseId, int? limit, CancellationToken ct = default)
    {
        var entries = await _inventory.GetMovementCardAsync(skuId, warehouseId, Math.Clamp(limit ?? 200, 1, 1000), ct);
        if (entries.Count == 0)
        {
            return Result.Fail<Contracts.V1.MovementCardResponse>(new(
                "MOVEMENT_CARD_EMPTY", "لا حركات لهذه الصيغة (بالمرشحات المحددة) — تحقق من الكود والمخزن"));
        }
        return Result.Ok(new Contracts.V1.MovementCardResponse(skuId, warehouseId,
            [.. entries.Select(e => new Contracts.V1.StockMovementCardDto(
                e.MovementId, e.SkuId, e.SkuCode, e.WarehouseId, e.WarehouseCode, e.MovementType, e.Direction,
                e.BaseQty, e.SourceUomId, e.SourceUomCode, e.ConversionFactor,
                e.SourceDocType, e.SourceDocId, e.SourceLineRef, e.Reason,
                e.UserId, e.Username, e.OccurredAt, e.RunningBalance))]));
    }

    public async Task<Contracts.V1.InventoryValuationResponse> ValuationAsync(long? warehouseId, CancellationToken ct = default)
    {
        var lines = await _inventory.GetValuationAsync(warehouseId, ct);
        return new Contracts.V1.InventoryValuationResponse(
            warehouseId ?? -1, lines.Sum(l => l.Valuation),
            [.. lines.Select(l => new Contracts.V1.ValuationLineDto(
                l.SkuId, l.SkuCode, l.WarehouseId, l.WarehouseCode, l.BaseQty, l.AvgUnitCost, l.Valuation))]);
    }
}

/// <summary>الحجز والمتاح (§12.1): حجز لمستند مفتوح وتحريره — المتاح = الرصيد − المحجوز.</summary>
public sealed class ManageReservationsUseCase
{
    private readonly IInventoryRepository _inventory;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageReservationsUseCase> _logger;

    public ManageReservationsUseCase(
        IInventoryRepository inventory, IAuditWriter audit, ICurrentUser user, ILogger<ManageReservationsUseCase> logger)
    {
        _inventory = inventory;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.ReservationDto>> ReserveAsync(Contracts.V1.ReserveStockRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request.QtyBase <= 0)
        {
            return Result.Fail<Contracts.V1.ReservationDto>(new("RESERVATION_QTY_INVALID", "كمية الحجز يجب أن تكون موجبة"));
        }
        if (string.IsNullOrWhiteSpace(request.SourceDocType) || request.SourceDocId <= 0)
        {
            return Result.Fail<Contracts.V1.ReservationDto>(new(
                "RESERVATION_SOURCE_REQUIRED", "الحجز يوثق بمستند مصدر مفتوح (نوع ومعرف)"));
        }

        // المتاح يُفحص داخل معاملة المستودع (الرصيد + المحجوز الحي) — لا سباق هنا
        try
        {
            var result = await _inventory.CreateReservationAsync(new ReservationDraft(
                request.SkuId, request.WarehouseId, request.QtyBase,
                request.SourceDocType.Trim(), request.SourceDocId, request.SourceLineRef?.Trim(), _user.UserId), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ReservationDto>(result.Error!);
            }
            var r = result.Value!;
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "inv.stock_reservations", EntityId: r.ReservationId, Action: "RESERVATION_CREATE",
                Result: "SUCCESS", Reason: $"{r.QtyBase} على {r.SkuCode} لمستند {r.SourceDocType}/{r.SourceDocId}",
                CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(r));
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.ReservationDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<long>> ReleaseAsync(long reservationId, Contracts.V1.ReleaseReservationRequest request, string? correlationId, CancellationToken ct = default)
    {
        var result = await _inventory.ReleaseReservationAsync(reservationId, request.ReasonAr, _user.UserId, ct);
        if (result.IsSuccess)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "inv.stock_reservations", EntityId: reservationId, Action: "RESERVATION_RELEASE",
                Result: "SUCCESS", Reason: request.ReasonAr, CorrelationId: correlationId), ct);
        }
        return result;
    }

    public async Task<Contracts.V1.ReservationListResponse> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        var rows = await _inventory.ListReservationsAsync(activeOnly, ct);
        return new Contracts.V1.ReservationListResponse([.. rows.Select(ToDto)]);
    }

    private static Contracts.V1.ReservationDto ToDto(ReservationInfo r) => new(
        r.ReservationId, r.SkuId, r.SkuCode, r.WarehouseId, r.WarehouseCode,
        r.QtyBase, r.SourceDocType, r.SourceDocId, r.ReservedAt, r.ReleasedAt);
}

/// <summary>الجرد والتسويات (PAGE-20): فتح/تسجيل/إغلاق/مطابقة/اعتماد (المعتمد ≠ الجارّ).</summary>
public sealed partial class ManageStocktakesUseCase
{
    private readonly IInventoryRepository _inventory;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageStocktakesUseCase> _logger;

    public ManageStocktakesUseCase(
        IInventoryRepository inventory, IAuditWriter audit, ICurrentUser user, ILogger<ManageStocktakesUseCase> logger)
    {
        _inventory = inventory;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.StocktakeDto>> OpenAsync(Contracts.V1.OpenStocktakeRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("BRANCH_SCOPE_REQUIRED", "فتح الجرد يتطلب تسجيل دخول بنطاق فرع"));
        }
        var branchOfWarehouse = await _inventory.FindWarehouseBranchAsync(request.WarehouseId, ct);
        if (branchOfWarehouse is null)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("WAREHOUSE_NOT_FOUND", "المخزن غير موجود"));
        }
        if (branchOfWarehouse.Value != branchId)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("WAREHOUSE_BRANCH_MISMATCH", "المخزن يتبع فرعاً آخر غير نطاق فرعك"));
        }

        long countId;
        try
        {
            var result = await _inventory.OpenCountAsync(new CountDraft(request.WarehouseId, request.Notes?.Trim(), _user.UserId), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.StocktakeDto>(result.Error!);
            }
            countId = result.Value;
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new(ex.Code, ex.UserMessageAr));
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "inv.count_sessions", EntityId: countId, Action: "COUNT_OPEN", Result: "SUCCESS",
            Reason: request.Notes, CorrelationId: correlationId), ct);
        return Result.Ok((await FindDtoAsync(countId, ct))!);
    }

    public async Task<Result<Contracts.V1.StocktakeDetailsResponse>> RecordAsync(long countId, Contracts.V1.RecordCountLinesRequest request, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindCountAsync(countId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.StocktakeDetailsResponse>(new("COUNT_NOT_FOUND", "جلسة الجرد غير موجودة"));
        }
        if (!CountStates.CanRecord(detail.Status))
        {
            return Result.Fail<Contracts.V1.StocktakeDetailsResponse>(new(
                "COUNT_NOT_RECORDABLE", "تسجيل العد يحدث في حالة OPEN فقط (اللقطة تتجمد بعد الإغلاق للمقارنة)"));
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.StocktakeDetailsResponse>(new("COUNT_LINES_REQUIRED", "سطر عد واحد على الأقل"));
        }
        var seen = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (line.CountedQty < 0)
            {
                return Result.Fail<Contracts.V1.StocktakeDetailsResponse>(new("COUNT_QTY_INVALID", "الكمية المعدودة لا تكون سالبة"));
            }
            if (!seen.Add(line.SkuId))
            {
                return Result.Fail<Contracts.V1.StocktakeDetailsResponse>(new(
                    "COUNT_LINE_DUPLICATED", $"الصيغة {line.SkuId} مكررة في نفس الطلب — سطر واحد لكل صنف"));
            }
        }

        await _inventory.RecordCountLinesAsync(countId, [.. request.Lines.Select(l => new CountLineDraft(l.SkuId, l.CountedQty))], ct);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "inv.count_sessions", EntityId: countId, Action: "COUNT_RECORD", Result: "SUCCESS",
            Reason: $"{request.Lines.Count} سطراً", CorrelationId: correlationId), ct);
        return Result.Ok((await FindDetailsAsync(countId, ct))!);
    }

    public async Task<Result<Contracts.V1.StocktakeDto>> CloseAsync(long countId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindCountAsync(countId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("COUNT_NOT_FOUND", "جلسة الجرد غير موجودة"));
        }
        if (detail.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("COUNT_EMPTY", "لا يمكن إغلاق جرد بلا أسطر عدّ"));
        }
        if (!CountStates.CanClose(detail.Status))
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("COUNT_NOT_CLOSABLE", "الإغلاق من OPEN فقط"));
        }
        var result = await _inventory.CloseCountAsync(countId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "inv.count_sessions", EntityId: countId, Action: "COUNT_CLOSE", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok((await FindDtoAsync(countId, ct))!);
    }

    public async Task<Result<Contracts.V1.StocktakeDto>> ReconcileAsync(long countId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindCountAsync(countId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("COUNT_NOT_FOUND", "جلسة الجرد غير موجودة"));
        }
        if (!CountStates.CanReconcile(detail.Status))
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(new("COUNT_NOT_RECONCILABLE", "المطابقة من COUNTED فقط"));
        }
        var result = await _inventory.ReconcileCountAsync(countId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.StocktakeDto>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "inv.count_sessions", EntityId: countId, Action: "COUNT_RECONCILE", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok((await FindDtoAsync(countId, ct))!);
    }

    /// <summary>الاعتماد (§12.2 ق9): المعتمد ≠ الجارّ — ينشئ مستند التسوية وحركة لكل صنف ذي فرق.</summary>
    public async Task<Result<Contracts.V1.ApproveStocktakeResponse>> ApproveAsync(long countId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindCountAsync(countId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.ApproveStocktakeResponse>(new("COUNT_NOT_FOUND", "جلسة الجرد غير موجودة"));
        }
        if (!CountStates.CanApprove(detail.Status))
        {
            return Result.Fail<Contracts.V1.ApproveStocktakeResponse>(new("COUNT_NOT_APPROVABLE", "الاعتماد من RECONCILED فقط"));
        }
        if (detail.ApprovedBy == _user.UserId || detail.CountedBy == _user.UserId)
        {
            // قيد V003 count_approval_distinct — نفس الفحص برسالة أوضح هنا
            return Result.Fail<Contracts.V1.ApproveStocktakeResponse>(new(
                "COUNT_APPROVER_MUST_DIFFER", "المعتمد يجب أن يكون غير من قام بالجرد (فصل واجبات — قيد V003)"));
        }
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.ApproveStocktakeResponse>(new("BRANCH_SCOPE_REQUIRED", "الاعتماد يتطلب نطاق فرع"));
        }

        // خطة التسوية تحمل كل الأسطر: ذو الفرق حركته محسوبة، والفرق الصفر حركته null
        // (يبقى في التقرير بلا حركة ولا مستند — لا ضجيج في الدفتر)
        var plans = new List<CountAdjustmentLinePlan>();
        foreach (var line in detail.Lines)
        {
            var outcome = AdjustmentMath.Resolve(AdjustmentKinds.Stocktake, line.SystemQty, line.CountedQty);
            plans.Add(new CountAdjustmentLinePlan(
                line.SkuId, line.SkuCode, line.SystemQty, line.CountedQty, line.VarianceQty,
                outcome.MovementType, outcome.BaseQty, outcome.Direction));
        }

        ApprovedCountResult result;
        try
        {
            var approved = await _inventory.ApproveCountAsync(new ApproveCountPlan(
                countId, detail.CountedBy, _user.UserId, branchId, detail.Notes, plans), ct);
            if (!approved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ApproveStocktakeResponse>(approved.Error!);
            }
            result = approved.Value!;
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.ApproveStocktakeResponse>(new(ex.Code, ex.UserMessageAr));
        }

        LogApproved(countId, result.MovementIds.Length, result.AdjustedLines, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "inv.count_sessions", EntityId: countId, Action: "COUNT_APPROVE", Result: "SUCCESS",
            Reason: $"أسطر معدلة={result.AdjustedLines} · حركات={result.MovementIds.Length}",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.ApproveStocktakeResponse(
            result.CountId, result.ApprovalId, result.AdjustmentDocIds,
            result.AdjustedLines, result.ZeroVarianceLines, result.MovementIds));
    }

    public async Task<Result<long>> CancelAsync(long countId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindCountAsync(countId, ct);
        if (detail is null)
        {
            return Result.Fail<long>(new("COUNT_NOT_FOUND", "جلسة الجرد غير موجودة"));
        }
        if (!CountStates.CanCancel(detail.Status))
        {
            return Result.Fail<long>(new("COUNT_NOT_CANCELLABLE", "الإلغاء من OPEN أو COUNTED فقط"));
        }
        var result = await _inventory.CancelCountAsync(countId, ct);
        if (result.IsSuccess)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "inv.count_sessions", EntityId: countId, Action: "COUNT_CANCEL", Result: "SUCCESS",
                CorrelationId: correlationId), ct);
        }
        return result;
    }

    public async Task<Contracts.V1.StocktakeListResponse> ListAsync(int? page, int? pageSize, CancellationToken ct = default)
    {
        var rows = await _inventory.ListCountsAsync(page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        return new Contracts.V1.StocktakeListResponse(
            page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), rows.Count,
            [.. rows.Select(r => new Contracts.V1.StocktakeDto(
                r.CountId, r.WarehouseId, r.WarehouseCode, r.Status, r.CountedBy, r.CountedByName,
                r.ApprovedBy, r.ApprovedByName, r.ApprovedAt, r.OpenedAt, r.ClosedAt, r.Notes,
                r.LineCount, r.TotalVariance))]);
    }

    public async Task<Result<Contracts.V1.StocktakeDetailsResponse>> GetAsync(long countId, CancellationToken ct = default)
    {
        var details = await FindDetailsAsync(countId, ct);
        return details is null
            ? Result.Fail<Contracts.V1.StocktakeDetailsResponse>(new("COUNT_NOT_FOUND", "جلسة الجرد غير موجودة"))
            : Result.Ok(details);
    }

    private async Task<Contracts.V1.StocktakeDto?> FindDtoAsync(long countId, CancellationToken ct)
    {
        var rows = await _inventory.ListCountsAsync(1, 500, ct);
        return rows.FirstOrDefault(r => r.CountId == countId) is { } r
            ? new Contracts.V1.StocktakeDto(
                r.CountId, r.WarehouseId, r.WarehouseCode, r.Status, r.CountedBy, r.CountedByName,
                r.ApprovedBy, r.ApprovedByName, r.ApprovedAt, r.OpenedAt, r.ClosedAt, r.Notes, r.LineCount, r.TotalVariance)
            : null;
    }

    private async Task<Contracts.V1.StocktakeDetailsResponse?> FindDetailsAsync(long countId, CancellationToken ct)
    {
        var detail = await _inventory.FindCountAsync(countId, ct);
        if (detail is null)
        {
            return null;
        }
        return new Contracts.V1.StocktakeDetailsResponse(
            new Contracts.V1.StocktakeDto(
                detail.CountId, detail.WarehouseId, detail.WarehouseCode, detail.Status,
                detail.CountedBy, detail.CountedByName, detail.ApprovedBy, detail.ApprovedByName,
                detail.ApprovedAt, detail.OpenedAt, detail.ClosedAt, detail.Notes,
                detail.Lines.Count, detail.Lines.Sum(l => l.VarianceQty)),
            [.. detail.Lines.Select(l => new Contracts.V1.StocktakeLineDto(
                l.SkuId, l.SkuCode, l.SkuNameAr, l.SystemQty, l.CountedQty, l.VarianceQty))]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "اعتماد جرد {CountId}: {Movements} حركة لـ{Lines} سطراً معدلاً — correlation={CorrelationId}")]
    private partial void LogApproved(long countId, int movements, int lines, string? correlationId);
}

/// <summary>التسوية السريعة والهالك والتلف (PAGE-23 · §12.2 ق10) — اعتماد مشرف إلزامي (V003).</summary>
public sealed partial class PostQuickAdjustmentUseCase
{
    private readonly IInventoryRepository _inventory;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<PostQuickAdjustmentUseCase> _logger;

    public PostQuickAdjustmentUseCase(
        IInventoryRepository inventory, IAuditWriter audit, ICurrentUser user, ILogger<PostQuickAdjustmentUseCase> logger)
    {
        _inventory = inventory;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.PostAdjustmentResponse>> ExecuteAsync(Contracts.V1.PostAdjustmentRequest request, string? correlationId, CancellationToken ct = default)
    {
        var kind = request.AdjustmentKind?.Trim() ?? string.Empty;
        if (kind is not (AdjustmentKinds.Quick or AdjustmentKinds.Damage or AdjustmentKinds.Expiry))
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new(
                "ADJUSTMENT_KIND_INVALID", "النوع يجب أن يكون QUICK أو DAMAGE أو EXPIRY (جرد الجلسات يُعتمد من مسار الجرد)"));
        }
        if (request.NewQty < 0)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new("ADJUSTMENT_NEW_QTY_INVALID", "الرصيد المعتمد لا يكون سالباً"));
        }
        if (request.ReasonAr is null || request.ReasonAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new("ADJUSTMENT_REASON_REQUIRED", "سبب موثق (≥ 3 محارف) إلزامي للتسوية"));
        }
        if (request.ApprovalId <= 0)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new(
                "ADJUSTMENT_APPROVAL_REQUIRED", "التسوية تلزم اعتماداً معتمداً — أنشئ طلب اعتماد ودع المشرف يبت فيه (D-03)"));
        }
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new("BRANCH_SCOPE_REQUIRED", "التسوية تتطلب نطاق فرع"));
        }
        var warehouseBranch = await _inventory.FindWarehouseBranchAsync(request.WarehouseId, ct);
        if (warehouseBranch is null)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new("WAREHOUSE_NOT_FOUND", "المخزن غير موجود"));
        }
        if (warehouseBranch.Value != branchId)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new("WAREHOUSE_BRANCH_MISMATCH", "المخزن يتبع فرعاً آخر غير نطاق فرعك"));
        }

        var balance = await _inventory.FindBalanceAsync(request.SkuId, request.WarehouseId, ct);
        var currentQty = balance?.BaseQty ?? 0;   // لا رصيد بعد = صفر في الدفتر (لا رقم حر)

        var outcome = AdjustmentMath.Resolve(kind, currentQty, request.NewQty);

        PostedAdjustment posted;
        try
        {
            var result = await _inventory.PostAdjustmentAsync(new AdjustmentPlan(
                request.SkuId, request.WarehouseId, kind, request.NewQty, request.ReasonAr.Trim(),
                request.ApprovalId, CountId: null,
                outcome.MovementType, outcome.BaseQty, outcome.Direction,
                branchId, _user.UserId), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostAdjustmentResponse>(result.Error!);
            }
            posted = result.Value!;
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.PostAdjustmentResponse>(new(ex.Code, ex.UserMessageAr));
        }

        LogPosted(posted.AdjustmentId, kind, request.NewQty, currentQty, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "inv.stock_adjustments", EntityId: posted.AdjustmentId, Action: "ADJUSTMENT_POST",
            Result: "SUCCESS", Reason: $"{kind}: {currentQty:0.######} → {request.NewQty:0.######} · {request.ReasonAr}",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PostAdjustmentResponse(
            posted.AdjustmentId, posted.MovementId, posted.MovementType, posted.BaseQty, posted.Direction));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "تسوية {AdjustmentId} ({Kind}) إلى {NewQty} من {CurrentQty} — correlation={CorrelationId}")]
    private partial void LogPosted(long adjustmentId, string kind, decimal newQty, decimal currentQty, string? correlationId);
}

/// <summary>التحويلات بين المخازن (PAGE-21): مرحلتان مستقلتان — إرسال ثم استلام.</summary>
public sealed partial class ManageTransfersUseCase
{
    private readonly IInventoryRepository _inventory;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageTransfersUseCase> _logger;

    public ManageTransfersUseCase(
        IInventoryRepository inventory, IAuditWriter audit, ICurrentUser user, ILogger<ManageTransfersUseCase> logger)
    {
        _inventory = inventory;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.TransferDto>> CreateAsync(Contracts.V1.CreateTransferRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request.FromWarehouseId == request.ToWarehouseId)
        {
            return Result.Fail<Contracts.V1.TransferDto>(new("TRANSFER_SAME_WAREHOUSE", "لا تحويل من مخزن إلى نفسه (قيد V003)"));
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.TransferDto>(new("TRANSFER_LINES_REQUIRED", "سطر تحويل واحد على الأقل"));
        }
        var seen = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (line.QtyBase <= 0)
            {
                return Result.Fail<Contracts.V1.TransferDto>(new("TRANSFER_QTY_INVALID", "كمية التحويل يجب أن تكون موجبة"));
            }
            if (!seen.Add(line.SkuId))
            {
                return Result.Fail<Contracts.V1.TransferDto>(new("TRANSFER_LINE_DUPLICATED", $"الصيغة {line.SkuId} مكررة — سطر واحد لكل صنف"));
            }
        }
        var fromBranch = await _inventory.FindWarehouseBranchAsync(request.FromWarehouseId, ct);
        var toBranch = await _inventory.FindWarehouseBranchAsync(request.ToWarehouseId, ct);
        if (fromBranch is null || toBranch is null)
        {
            return Result.Fail<Contracts.V1.TransferDto>(new("WAREHOUSE_NOT_FOUND", "أحد المخزنين غير موجود"));
        }

        long transferId;
        try
        {
            var result = await _inventory.CreateTransferAsync(new TransferDraft(
                request.FromWarehouseId, request.ToWarehouseId,
                [.. request.Lines.Select(l => new TransferLineDraft(l.SkuId, l.QtyBase))], _user.UserId), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.TransferDto>(result.Error!);
            }
            transferId = result.Value;
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.TransferDto>(new(ex.Code, ex.UserMessageAr));
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "inv.transfer_orders", EntityId: transferId, Action: "TRANSFER_CREATE", Result: "SUCCESS",
            Reason: $"{request.Lines.Count} سطراً", CorrelationId: correlationId), ct);
        return Result.Ok((await FindDtoAsync(transferId, ct))!);
    }

    /// <summary>الإرسال (§12.2 ق7): يرحّل TRANSFER_OUT — ومخزون المصدر لا ينزل تحت الصفر إلا باعتماد D-03.</summary>
    public async Task<Result<Contracts.V1.TransferActionResponse>> SendAsync(long transferId, Contracts.V1.SendTransferRequest request, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindTransferAsync(transferId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new("TRANSFER_NOT_FOUND", "التحويل غير موجود"));
        }
        if (!TransferStates.CanSend(detail.Status))
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new("TRANSFER_NOT_SENDABLE", "الإرسال من DRAFT فقط"));
        }
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new("BRANCH_SCOPE_REQUIRED", "الإرسال يتطلب نطاق فرع"));
        }
        var fromBranch = await _inventory.FindWarehouseBranchAsync(detail.FromWarehouseId, ct);
        if (fromBranch is null || fromBranch.Value != branchId)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new(
                "WAREHOUSE_BRANCH_MISMATCH", "الإرسال من مخزن فرعك فقط — حركة الخروج توثق بفرع مخزن المصدر"));
        }

        // فحص الرصيد المتوقع: هل ينزل أي سطر تحت الصفر؟ (فوق المتاح مسموح — الحجز شأن مستندات البيع)
        var movementPlans = new List<TransferMovementPlan>();
        var shortageLines = new List<string>();
        foreach (var line in detail.Lines)
        {
            var snap = await _inventory.FindBalanceAsync(line.SkuId, detail.FromWarehouseId, ct);
            var currentQty = snap?.BaseQty ?? 0;
            if (currentQty < line.QtyBase)
            {
                shortageLines.Add($"{line.SkuCode}: المتاح {currentQty:0.######} < المطلوب {line.QtyBase:0.######}");
            }
            var baseUom = await _inventory.FindSkuBaseUomAsync(line.SkuId, ct);
            if (baseUom is null)
            {
                return Result.Fail<Contracts.V1.TransferActionResponse>(new("SKU_NOT_FOUND", $"الصيغة {line.SkuCode} غير موجودة"));
            }
            movementPlans.Add(new TransferMovementPlan(line.SkuId, line.QtyBase, baseUom.BaseUomId, 1m));
        }
        if (shortageLines.Count > 0 && request.ApprovalId is null)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new(
                "NEGATIVE_STOCK_NEEDS_APPROVAL",
                "التحويل ينزل مخزون المصدر تحت الصفر: " + string.Join("؛ ", shortageLines) +
                " — الدفع باعتماد مشرف معتمد (D-03) عبر approvals ثم أعد الإرسال بمفتاح approvalId"));
        }

        TransferAction action;
        try
        {
            var result = await _inventory.SendTransferAsync(new SendTransferPlan(
                transferId, request.ApprovalId, request.ReasonAr, branchId, _user.UserId, movementPlans), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.TransferActionResponse>(result.Error!);
            }
            action = result.Value!;
        }
        catch (InventoryPolicyException ex)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new(ex.Code, ex.UserMessageAr));
        }

        LogSent(transferId, action.MovementIds.Length, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "inv.transfer_orders", EntityId: transferId, Action: "TRANSFER_SEND", Result: "SUCCESS",
            Reason: request.ReasonAr, CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.TransferActionResponse(action.TransferId, action.NewStatus, action.MovementIds));
    }

    /// <summary>الاستلام: يرحّل TRANSFER_IN في مخزن الوجهة — يتطلب نطاق فرع الوجهة (تعدد الفروع §11.1).</summary>
    public async Task<Result<Contracts.V1.TransferActionResponse>> ReceiveAsync(long transferId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindTransferAsync(transferId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new("TRANSFER_NOT_FOUND", "التحويل غير موجود"));
        }
        if (!TransferStates.CanReceive(detail.Status))
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new("TRANSFER_NOT_RECEIVABLE", "الاستلام من SENT فقط"));
        }
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new("BRANCH_SCOPE_REQUIRED", "الاستلام يتطلب نطاق فرع"));
        }
        var toBranch = await _inventory.FindWarehouseBranchAsync(detail.ToWarehouseId, ct);
        if (toBranch is null || toBranch.Value != branchId)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(new(
                "WAREHOUSE_BRANCH_MISMATCH", "الاستلام في مخزن فرعك فقط — حركة الدخول توثق بفرع مخزن الوجهة"));
        }

        var result = await _inventory.ReceiveTransferAsync(transferId, _user.UserId, branchId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.TransferActionResponse>(result.Error!);
        }
        var action = result.Value!;

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "inv.transfer_orders", EntityId: transferId, Action: "TRANSFER_RECEIVE", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.TransferActionResponse(action.TransferId, action.NewStatus, action.MovementIds));
    }

    public async Task<Result<long>> CancelAsync(long transferId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _inventory.FindTransferAsync(transferId, ct);
        if (detail is null)
        {
            return Result.Fail<long>(new("TRANSFER_NOT_FOUND", "التحويل غير موجود"));
        }
        if (!TransferStates.CanCancel(detail.Status))
        {
            return Result.Fail<long>(new("TRANSFER_NOT_CANCELLABLE", "الإلغاء من DRAFT فقط — بعد الإرسال وقعت حركة خروج في الدفتر"));
        }
        var result = await _inventory.CancelTransferAsync(transferId, ct);
        if (result.IsSuccess)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "inv.transfer_orders", EntityId: transferId, Action: "TRANSFER_CANCEL", Result: "SUCCESS",
                CorrelationId: correlationId), ct);
        }
        return result;
    }

    public async Task<Contracts.V1.TransferDto?> GetAsync(long transferId, CancellationToken ct = default)
        => await FindDtoAsync(transferId, ct);

    public async Task<IReadOnlyList<Contracts.V1.TransferDto>> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        var rows = await _inventory.ListTransfersAsync(status, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        var result = new List<Contracts.V1.TransferDto>();
        foreach (var row in rows)
        {
            var detail = await FindDtoAsync(row.TransferId, ct);
            if (detail is not null)
            {
                result.Add(detail);
            }
        }
        return result;
    }

    private async Task<Contracts.V1.TransferDto?> FindDtoAsync(long transferId, CancellationToken ct)
    {
        var detail = await _inventory.FindTransferAsync(transferId, ct);
        if (detail is null)
        {
            return null;
        }
        return new Contracts.V1.TransferDto(
            detail.TransferId, detail.FromWarehouseId, detail.FromWarehouseCode,
            detail.ToWarehouseId, detail.ToWarehouseCode, detail.Status,
            detail.SentAt, detail.ReceivedAt, detail.CreatedByName, detail.CreatedAt,
            [.. detail.Lines.Select(l => new Contracts.V1.TransferLineDto(l.SkuId, l.SkuCode, l.QtyBase))]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "إرسال تحويل {TransferId}: {Movements} حركة خروج — correlation={CorrelationId}")]
    private partial void LogSent(long transferId, int movements, string? correlationId);
}

/// <summary>النواقص وإعادة الطلب (PAGE-22) والدفعات/الصلاحية (PAGE-23) — تقارير من الدفتر.</summary>
public sealed class GetInventoryAdvisoriesUseCase
{
    private readonly IInventoryRepository _inventory;

    public GetInventoryAdvisoriesUseCase(IInventoryRepository inventory) => _inventory = inventory;

    public async Task<Contracts.V1.ReorderReportResponse> ReorderAsync(long? warehouseId, CancellationToken ct = default)
    {
        var rows = await _inventory.GetReorderAsync(warehouseId, ct);
        return new Contracts.V1.ReorderReportResponse(
            [.. rows.Select(r => new Contracts.V1.ReorderSuggestionDto(
                r.SkuId, r.SkuCode, r.SkuNameAr, r.ProductNameAr, r.WarehouseId, r.WarehouseCode,
                r.AvailableQty, r.ReorderLevel, r.SuggestedQty))]);
    }

    public async Task<Contracts.V1.LotsReportResponse> LotsAsync(int? expiringInDays, CancellationToken ct = default)
    {
        var rows = await _inventory.GetLotsAsync(expiringInDays, ct);
        return new Contracts.V1.LotsReportResponse(
            [.. rows.Select(l => new Contracts.V1.LotDto(
                l.LotId, l.SkuId, l.SkuCode, l.LotCode, l.ExpiryDate, l.ReceivedAt, l.ReceiptId, l.ReceiptNumber,
                l.IsExpiringSoon, l.OnHandQty))]);
    }
}

/// <summary>الاعتمادات (D-03 ب): طلب من المنفذ وبَت من غيره — البت يوثق بقرار من المشرف.</summary>
public sealed partial class ManageApprovalsUseCase
{
    private readonly IApprovalRepository _approvals;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageApprovalsUseCase> _logger;

    public ManageApprovalsUseCase(
        IApprovalRepository approvals, IAuditWriter audit, ICurrentUser user, ILogger<ManageApprovalsUseCase> logger)
    {
        _approvals = approvals;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.ApprovalDto>> CreateAsync(Contracts.V1.CreateApprovalRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType) || request.EntityType.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.ApprovalDto>(new("APPROVAL_ENTITY_REQUIRED", "نوع الكيان المطلوب اعتماده مطلوب"));
        }
        if (request.EntityId < 0)
        {
            return Result.Fail<Contracts.V1.ApprovalDto>(new("APPROVAL_ENTITY_ID_INVALID", "معرف الكيان لا يكون سالباً (0 = طلب مسبق قبل إنشاء المستند)"));
        }
        if (request.ReasonAr is null || request.ReasonAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.ApprovalDto>(new("APPROVAL_REASON_REQUIRED", "سبب الطلب موثق (≥ 3 محارف)"));
        }
        var info = await _approvals.CreateAsync(new ApprovalDraft(
            request.EntityType.Trim(), request.EntityId, request.ReasonAr.Trim(), _user.UserId), ct);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "core.approvals", EntityId: info.ApprovalId, Action: "APPROVAL_REQUEST", Result: "SUCCESS",
            Reason: request.ReasonAr, CorrelationId: correlationId), ct);
        return Result.Ok(ToDto(info));
    }

    public async Task<Result<Contracts.V1.ApprovalDto>> DecideAsync(long approvalId, Contracts.V1.DecideApprovalRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request.Decision is not ("APPROVED" or "REJECTED"))
        {
            return Result.Fail<Contracts.V1.ApprovalDto>(new("APPROVAL_DECISION_INVALID", "القرار APPROVED أو REJECTED"));
        }
        if (request.ReasonAr is null || request.ReasonAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.ApprovalDto>(new("APPROVAL_REASON_REQUIRED", "سبب القرار موثق (≥ 3 محارف)"));
        }
        ApprovalInfo info;
        try
        {
            var result = await _approvals.DecideAsync(
                approvalId, request.Decision, request.ReasonAr.Trim(), _user.UserId, ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ApprovalDto>(result.Error!);
            }
            info = result.Value!;
        }
        catch (ApprovalPolicyException ex)
        {
            return Result.Fail<Contracts.V1.ApprovalDto>(new(ex.Code, ex.UserMessageAr));
        }

        LogDecided(approvalId, request.Decision, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "core.approvals", EntityId: approvalId, Action: "APPROVAL_DECIDE", Result: "SUCCESS",
            Reason: $"{request.Decision} · {request.ReasonAr}", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto(info));
    }

    public async Task<Contracts.V1.ApprovalListResponse> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = await _approvals.ListAsync(status, ct);
        return new Contracts.V1.ApprovalListResponse([.. rows.Select(ToDto)]);
    }

    private static Contracts.V1.ApprovalDto ToDto(ApprovalInfo a) => new(
        a.ApprovalId, a.EntityType, a.EntityId, a.RequestedBy, a.RequestedByName, a.Decision,
        a.DecidedBy, a.DecidedByName, a.Reason, a.RequestedAt, a.DecidedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "بت اعتماد {ApprovalId} بـ{Decision} — correlation={CorrelationId}")]
    private partial void LogDecided(long approvalId, string decision, string? correlationId);
}

// ── عقود الاعتمادات (G6 — D-03) ─────────────────────────────────────────────

public sealed record ApprovalDraft(string EntityType, long EntityId, string ReasonAr, long RequestedBy);

public sealed record ApprovalInfo(
    long ApprovalId, string EntityType, long EntityId,
    long RequestedBy, string RequestedByName, string Decision,
    long? DecidedBy, string? DecidedByName, string? Reason,
    DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt);

/// <summary>مستودع الاعتمادات (core.approvals) — البت من غير الطالب (قيد V003 approvals_no_self).</summary>
public interface IApprovalRepository
{
    Task<ApprovalInfo> CreateAsync(ApprovalDraft draft, CancellationToken ct = default);
    Task<Result<ApprovalInfo>> DecideAsync(long approvalId, string decision, string reasonAr, long decidedBy, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalInfo>> ListAsync(string? status, CancellationToken ct = default);
    Task<ApprovalInfo?> FindAsync(long approvalId, CancellationToken ct = default);
}

/// <summary>خرق سياسة اعتماد (البت على طلبك/البت على غير المعلق) — 409 برسالة عربية.</summary>
public sealed class ApprovalPolicyException(string code, string userMessageAr) : Exception(userMessageAr)
{
    public string Code { get; } = code;
    public string UserMessageAr { get; } = userMessageAr;
}
