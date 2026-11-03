namespace WeraPro.Contracts.V1;

// ── المخزون (G6 — §12.1/§12.2 دفتر الحركة والأرصدة والحجز والمتاح) ─────────

public sealed record WarehouseDto(
    long WarehouseId,
    long BranchId,
    string BranchCode,
    string Code,
    string NameAr,
    string WhType,          // MAIN | BRANCH | SUB | EXTERNAL
    bool IsActive);

public sealed record CreateWarehouseRequest(
    long BranchId,
    string Code,
    string NameAr,
    string WhType);

public sealed record SetWarehouseStatusRequest(bool IsActive, string? ReasonAr);

public sealed record StockBalanceDto(
    long SkuId,
    string SkuCode,
    long? ProductId,
    string ProductCode,
    string ProductNameAr,
    string SkuNameAr,
    string SaleMode,
    long WarehouseId,
    string WarehouseCode,
    decimal BaseQty,        // من الدفتر (مشغّل الرصين V003)
    decimal ReservedQty,    // المحجوز لمستندات مفتوحة
    decimal AvailableQty,   // = الرصيد − المحجوز (§12.1)
    decimal ReorderLevel,
    bool BelowReorder,
    decimal? AvgUnitCost,   // WAC للوحدة الأساسية (D-02) — null قبل أول استلام
    decimal? Valuation);    // base_qty × WAC

public sealed record StockBalanceSearchResponse(
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages,
    IReadOnlyList<StockBalanceDto> Items);

/// <summary>تقييم المخزون بطبقات التكلفة الحالية (§16.2 — D-02 WAC).</summary>
public sealed record InventoryValuationResponse(
    long WarehouseIdFilter,     // null = كل المخازن
    decimal TotalValuation,
    IReadOnlyList<ValuationLineDto> Lines);

public sealed record ValuationLineDto(
    long SkuId,
    string SkuCode,
    long WarehouseId,
    string WarehouseCode,
    decimal BaseQty,
    decimal AvgUnitCost,
    decimal Valuation);

/// <summary>سطر كرت حركة المنتج (PAGE-19) — حقيقة موثقة بمصدرها ومعامل تحويلها.</summary>
public sealed record StockMovementCardDto(
    long MovementId,
    long SkuId,
    string SkuCode,
    long WarehouseId,
    string WarehouseCode,
    string MovementType,
    int Direction,
    decimal BaseQty,
    long SourceUomId,
    string SourceUomCode,
    decimal ConversionFactor,
    string SourceDocType,
    long SourceDocId,
    string? SourceLineRef,
    string? Reason,
    long UserId,
    string Username,
    DateTimeOffset OccurredAt,
    decimal RunningBalance);   // الرصيد بعد هذه الحركة (من الدفتر — مطابقة التقارير)

public sealed record MovementCardResponse(
    long SkuId,
    long? WarehouseId,
    IReadOnlyList<StockMovementCardDto> Movements);

public sealed record ReserveStockRequest(
    long SkuId,
    long WarehouseId,
    decimal QtyBase,
    string SourceDocType,
    long SourceDocId,
    string? SourceLineRef);

public sealed record ReservationDto(
    long ReservationId,
    long SkuId,
    string SkuCode,
    long WarehouseId,
    string WarehouseCode,
    decimal QtyBase,
    string SourceDocType,
    long SourceDocId,
    DateTimeOffset ReservedAt,
    DateTimeOffset? ReleasedAt);

public sealed record ReservationListResponse(IReadOnlyList<ReservationDto> Items);

public sealed record ReleaseReservationRequest(string? ReasonAr);

// ── الجرد والتسويات (G6 — §12.2 ق9) ─────────────────────────────────────────

public sealed record StocktakeDto(
    long CountId,
    long WarehouseId,
    string WarehouseCode,
    string Status,
    long CountedBy,
    string CountedByName,
    long? ApprovedBy,
    string? ApprovedByName,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string? Notes,
    int LineCount,
    decimal TotalVariance);

public sealed record StocktakeListResponse(
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<StocktakeDto> Items);

public sealed record OpenStocktakeRequest(long WarehouseId, string? Notes);

public sealed record RecordCountLinesRequest(IReadOnlyList<RecordCountLineRequest> Lines);
public sealed record RecordCountLineRequest(long SkuId, decimal CountedQty);

public sealed record StocktakeLineDto(
    long SkuId,
    string SkuCode,
    string SkuNameAr,
    decimal SystemQty,
    decimal CountedQty,
    decimal VarianceQty);

public sealed record StocktakeDetailsResponse(
    StocktakeDto Stocktake,
    IReadOnlyList<StocktakeLineDto> Lines);

/// <summary>اعتماد التسوية: يوثق الاعتماد (المعتمد ≠ الجارّ بقيد V003) ويرحّل الحركات.</summary>
public sealed record ApproveStocktakeResponse(
    long CountId,
    long ApprovalId,
    long[] AdjustmentDocIds,
    int AdjustedLines,
    int ZeroVarianceLines,
    long[] MovementIds);

// ── التسوية السريعة/الهالك/التلف (G6 — §12.2 ق10 · صفحة 23) ────────────────

public sealed record PostAdjustmentRequest(
    long SkuId,
    long WarehouseId,
    decimal NewQty,          // الرصيد الصحيح المعتمد (بعد الهالك عند DAMAGE/EXPIRY)
    string AdjustmentKind,   // QUICK | DAMAGE | EXPIRY
    string ReasonAr,
    long ApprovalId);        // اعتماد مطلوب (قيد V003 adjust_approval_required)

public sealed record AdjustmentDocDto(
    long AdjustmentId,
    string AdjustmentKind,
    long SkuId,
    string SkuCode,
    long WarehouseId,
    string WarehouseCode,
    decimal NewQty,
    string ReasonAr,
    long ApprovalId,
    long? CountId,
    DateTimeOffset? PostedAt,
    string PostedByName);

public sealed record PostAdjustmentResponse(
    long AdjustmentId,
    long? MovementId,        // null عند فرق صفر — لا حركة (لا ضجيج)
    string? MovementType,
    decimal BaseQty,
    int Direction);

// ── التحويلات بين المخازن (G6 — §12.2 ق7) ──────────────────────────────────

public sealed record TransferDto(
    long TransferId,
    long FromWarehouseId,
    string FromWarehouseCode,
    long ToWarehouseId,
    string ToWarehouseCode,
    string Status,
    DateTimeOffset? SentAt,
    DateTimeOffset? ReceivedAt,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TransferLineDto> Lines);

public sealed record TransferLineDto(long SkuId, string SkuCode, decimal QtyBase);

public sealed record CreateTransferRequest(
    long FromWarehouseId,
    long ToWarehouseId,
    IReadOnlyList<CreateTransferLineRequest> Lines);
public sealed record CreateTransferLineRequest(long SkuId, decimal QtyBase);

public sealed record SendTransferRequest(long? ApprovalId, string? ReasonAr);

public sealed record TransferActionResponse(long TransferId, string NewStatus, long[] MovementIds);

// ── النواقص وإعادة الطلب (G6 — PAGE-22) والدفعات (PAGE-23) ─────────────────

public sealed record ReorderSuggestionDto(
    long SkuId,
    string SkuCode,
    string SkuNameAr,
    string ProductNameAr,
    long WarehouseId,
    string WarehouseCode,
    decimal AvailableQty,
    decimal ReorderLevel,
    decimal SuggestedQty);   // قاعدة حتمية موثقة: max(reorder_level − available, 0) — التنبؤ في G9

public sealed record ReorderReportResponse(IReadOnlyList<ReorderSuggestionDto> Items);

public sealed record LotDto(
    long LotId,
    long SkuId,
    string SkuCode,
    string LotCode,
    DateOnly? ExpiryDate,
    DateTimeOffset ReceivedAt,
    long? ReceiptId,
    string? ReceiptNumber,
    bool IsExpiringSoon,
    decimal OnHandQty);      // مجموع ما استُلم على الدفعة − ما خرج منها (من الدفتر)

public sealed record LotsReportResponse(IReadOnlyList<LotDto> Items);

// ── الاعتمادات (G6 — D-03 ب: سالب/تسوية بموافقة مشرف) ───────────────────────

public sealed record ApprovalDto(
    long ApprovalId,
    string EntityType,
    long EntityId,
    long RequestedBy,
    string RequestedByName,
    string Decision,         // PENDING | APPROVED | REJECTED
    long? DecidedBy,
    string? DecidedByName,
    string? Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt);

public sealed record CreateApprovalRequest(string EntityType, long EntityId, string ReasonAr);

public sealed record DecideApprovalRequest(string Decision, string ReasonAr);

public sealed record ApprovalListResponse(IReadOnlyList<ApprovalDto> Items);
