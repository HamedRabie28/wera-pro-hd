namespace WeraPro.Application.Inventory;

using WeraPro.Domain.Catalog;
using WeraPro.Domain.SharedKernel;

/// <summary>
/// مستودع المخزون (G6 — §12.1/§12.2): كل عملياته الذرية داخل معاملة واحدة مع
/// ترجمة سياسات القاعدة إلى أخطاء 409 مفهومة لا 500 (نمط G4/G5).
/// </summary>
public interface IInventoryRepository
{
    // ── المخازن (PAGE-28) ───────────────────────────────────────────────
    Task<IReadOnlyList<WarehouseInfo>> ListWarehousesAsync(bool includeInactive, CancellationToken ct = default);
    Task<Result<long>> CreateWarehouseAsync(WarehouseDraft draft, CancellationToken ct = default);
    Task<Result<long>> SetWarehouseStatusAsync(long warehouseId, bool isActive, string reasonAr, long userId, CancellationToken ct = default);
    Task<long?> FindWarehouseBranchAsync(long warehouseId, CancellationToken ct = default);

    // ── الأرصدة وكرت الحركة والتقييم (PAGE-18/19 · §16.2) ───────────────
    Task<(IReadOnlyList<StockBalanceInfo> Items, long TotalCount)> SearchBalancesAsync(
        string? query, long? warehouseId, bool? belowReorder, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<MovementCardEntry>> GetMovementCardAsync(long skuId, long? warehouseId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<ValuationEntry>> GetValuationAsync(long? warehouseId, CancellationToken ct = default);

    // ── الحجز والمتاح (§12.1) ───────────────────────────────────────────
    Task<Result<ReservationInfo>> CreateReservationAsync(ReservationDraft draft, CancellationToken ct = default);
    Task<Result<long>> ReleaseReservationAsync(long reservationId, string? reasonAr, long userId, CancellationToken ct = default);
    Task<IReadOnlyList<ReservationInfo>> ListReservationsAsync(bool activeOnly, CancellationToken ct = default);
    Task<StockBalanceSnap?> FindBalanceAsync(long skuId, long warehouseId, CancellationToken ct = default);

    // ── الجرد والتسويات (PAGE-20 · §12.2 ق9) ────────────────────────────
    Task<Result<long>> OpenCountAsync(CountDraft draft, CancellationToken ct = default);
    Task<Result<long>> RecordCountLinesAsync(long countId, IReadOnlyList<CountLineDraft> lines, CancellationToken ct = default);
    Task<Result<long>> CloseCountAsync(long countId, CancellationToken ct = default);
    Task<Result<long>> ReconcileCountAsync(long countId, CancellationToken ct = default);
    Task<CountDetail?> FindCountAsync(long countId, CancellationToken ct = default);
    Task<IReadOnlyList<CountSummary>> ListCountsAsync(int page, int pageSize, CancellationToken ct = default);
    /// <summary>الاعتماد الذري: صف اعتماد (الجارّ≠المعتمد) + مستند تسوية + حركات الدفتر + حالة APPROVED.</summary>
    Task<Result<ApprovedCountResult>> ApproveCountAsync(ApproveCountPlan plan, CancellationToken ct = default);
    Task<Result<long>> CancelCountAsync(long countId, CancellationToken ct = default);

    // ── التسوية السريعة/الهالك/التلف (PAGE-23 · §12.2 ق10) ─────────────
    Task<Result<PostedAdjustment>> PostAdjustmentAsync(AdjustmentPlan plan, CancellationToken ct = default);
    Task<IReadOnlyList<AdjustmentEntry>> ListAdjustmentsAsync(long? warehouseId, int limit, CancellationToken ct = default);

    // ── التحويلات (PAGE-21 · §12.2 ق7) ─────────────────────────────────
    Task<Result<long>> CreateTransferAsync(TransferDraft draft, CancellationToken ct = default);
    Task<Result<TransferAction>> SendTransferAsync(SendTransferPlan plan, CancellationToken ct = default);
    Task<Result<TransferAction>> ReceiveTransferAsync(long transferId, long userId, long branchId, CancellationToken ct = default);
    Task<Result<long>> CancelTransferAsync(long transferId, CancellationToken ct = default);
    Task<TransferDetail?> FindTransferAsync(long transferId, CancellationToken ct = default);
    Task<IReadOnlyList<TransferSummary>> ListTransfersAsync(string? status, int page, int pageSize, CancellationToken ct = default);

    // ── النواقص والدفعات (PAGE-22/23) ──────────────────────────────────
    Task<IReadOnlyList<ReorderEntry>> GetReorderAsync(long? warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<LotEntry>> GetLotsAsync(int? expiringInDays, CancellationToken ct = default);

    /// <summary>وحدة الصيغة الأساسية ولقطة معلوماتها — لحركات التحويل (المصدر = الوحدة الأساسية بعامل 1).</summary>
    Task<SkuUomInfo?> FindSkuBaseUomAsync(long skuId, CancellationToken ct = default);
}

/// <summary>معلومات صيغة للوحدة الأساسية (من catalog.skus+units) — يغذي مسودات الحركات.</summary>
public sealed record SkuUomInfo(long SkuId, string SkuCode, long BaseUomId, string BaseUomCode, string SaleMode);

// ── السجلات المشتركة بين المستودع وحالات الاستخدام ──────────────────────────

public sealed record WarehouseInfo(
    long WarehouseId, long BranchId, string BranchCode, string Code, string NameAr, string WhType, bool IsActive);

public sealed record WarehouseDraft(
    long BranchId, string Code, string NameAr, string WhType, long UserId);

/// <summary>رصيد SKU في مخزن: من الدفتر (المشغّل) + المحجوز + المتاح.</summary>
public sealed record StockBalanceInfo(
    long SkuId, string SkuCode, long? ProductId, string ProductCode, string ProductNameAr, string SkuNameAr,
    string SaleMode, long WarehouseId, string WarehouseCode,
    decimal BaseQty, decimal ReservedQty, decimal AvailableQty, decimal ReorderLevel, bool BelowReorder,
    decimal? AvgUnitCost, decimal? Valuation);

public sealed record StockBalanceSnap(decimal BaseQty, decimal ReservedQty);

public sealed record MovementCardEntry(
    long MovementId, long SkuId, string SkuCode, long WarehouseId, string WarehouseCode,
    string MovementType, int Direction, decimal BaseQty, long SourceUomId, string SourceUomCode,
    decimal ConversionFactor, string SourceDocType, long SourceDocId, string? SourceLineRef,
    string? Reason, long UserId, string Username, DateTimeOffset OccurredAt, decimal RunningBalance);

public sealed record ValuationEntry(
    long SkuId, string SkuCode, long WarehouseId, string WarehouseCode,
    decimal BaseQty, decimal AvgUnitCost, decimal Valuation);

public sealed record ReservationInfo(
    long ReservationId, long SkuId, string SkuCode, long WarehouseId, string WarehouseCode,
    decimal QtyBase, string SourceDocType, long SourceDocId,
    DateTimeOffset ReservedAt, DateTimeOffset? ReleasedAt);

public sealed record ReservationDraft(
    long SkuId, long WarehouseId, decimal QtyBase,
    string SourceDocType, long SourceDocId, string? SourceLineRef, long UserId);

public sealed record CountDraft(long WarehouseId, string? Notes, long CountedBy);

public sealed record CountLineDraft(long SkuId, decimal CountedQty);

public sealed record CountLineInfo(
    long SkuId, string SkuCode, string SkuNameAr, decimal SystemQty, decimal CountedQty, decimal VarianceQty);

public sealed record CountDetail(
    long CountId, long WarehouseId, string WarehouseCode, string Status,
    long CountedBy, string CountedByName, long? ApprovedBy, string? ApprovedByName,
    DateTimeOffset? ApprovedAt, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string? Notes,
    IReadOnlyList<CountLineInfo> Lines);

public sealed record CountSummary(
    long CountId, long WarehouseId, string WarehouseCode, string Status,
    long CountedBy, string CountedByName, long? ApprovedBy, string? ApprovedByName, DateTimeOffset? ApprovedAt,
    DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string? Notes,
    int LineCount, decimal TotalVariance);

/// <summary>سطر خطة اعتماد الجرد: حركة واحدة لكل صنف ذي فرق (الفرق الصفر بلا حركة).</summary>
public sealed record CountAdjustmentLinePlan(
    long SkuId, string SkuCode,
    decimal SystemQty, decimal CountedQty, decimal VarianceQty,
    string? MovementType, decimal BaseQty, int Direction);

public sealed record ApproveCountPlan(
    long CountId, long CountedBy, long ApprovedBy, long BranchId,
    string? Notes,
    IReadOnlyList<CountAdjustmentLinePlan> Lines);

public sealed record ApprovedCountResult(
    long CountId, long ApprovalId, long[] AdjustmentDocIds, long[] MovementIds,
    int AdjustedLines, int ZeroVarianceLines);

public sealed record AdjustmentPlan(
    long SkuId, long WarehouseId, string AdjustmentKind, decimal NewQty,
    string ReasonAr, long ApprovalId, long? CountId,
    string? MovementType, decimal BaseQty, int Direction,
    long BranchId, long UserId);

public sealed record PostedAdjustment(
    long AdjustmentId, long? MovementId, string? MovementType, decimal BaseQty, int Direction);

public sealed record AdjustmentEntry(
    long AdjustmentId, string AdjustmentKind, long SkuId, string SkuCode,
    long WarehouseId, string WarehouseCode, decimal NewQty, string ReasonAr,
    long ApprovalId, long? CountId, DateTimeOffset? PostedAt, string PostedByName);

public sealed record TransferDraft(
    long FromWarehouseId, long ToWarehouseId,
    IReadOnlyList<TransferLineDraft> Lines, long UserId);

public sealed record TransferLineDraft(long SkuId, decimal QtyBase);

public sealed record SendTransferPlan(
    long TransferId, long? ApprovalId, string? ReasonAr, long BranchId, long UserId,
    IReadOnlyList<TransferMovementPlan> Movements);

/// <summary>حركة خروج تحويل واحدة — لقطة محسوبة في حالة الاستخدام قبل المعاملة.</summary>
public sealed record TransferMovementPlan(long SkuId, decimal BaseQty, long SourceUomId, decimal ConversionFactor);

public sealed record TransferAction(long TransferId, string NewStatus, long[] MovementIds);

public sealed record TransferDetail(
    long TransferId, long FromWarehouseId, string FromWarehouseCode,
    long ToWarehouseId, string ToWarehouseCode, string Status,
    DateTimeOffset? SentAt, DateTimeOffset? ReceivedAt, string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<TransferLineInfo> Lines);

public sealed record TransferLineInfo(long SkuId, string SkuCode, decimal QtyBase);

public sealed record TransferSummary(
    long TransferId, long FromWarehouseId, string FromWarehouseCode,
    long ToWarehouseId, string ToWarehouseCode, string Status,
    DateTimeOffset? SentAt, DateTimeOffset? ReceivedAt, DateTimeOffset CreatedAt);

public sealed record ReorderEntry(
    long SkuId, string SkuCode, string SkuNameAr, string ProductNameAr,
    long WarehouseId, string WarehouseCode,
    decimal AvailableQty, decimal ReorderLevel, decimal SuggestedQty);

public sealed record LotEntry(
    long LotId, long SkuId, string SkuCode, string LotCode, DateOnly? ExpiryDate,
    DateTimeOffset ReceivedAt, long? ReceiptId, string? ReceiptNumber,
    bool IsExpiringSoon, decimal OnHandQty);

/// <summary>
/// خرق سياسة قاعدة حاسم من مستودع المخزون (سالب بلا اعتماد / انتقال غير مسموح /
/// حجز فوق المتاح) — رسالة عربية جاهزة برمز مسمى لعرض 409 لا 500.
/// </summary>
public sealed class InventoryPolicyException(string code, string userMessageAr) : Exception(userMessageAr)
{
    public string Code { get; } = code;
    public string UserMessageAr { get; } = userMessageAr;
}
