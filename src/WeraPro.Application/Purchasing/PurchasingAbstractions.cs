namespace WeraPro.Application.Purchasing;

using WeraPro.Domain.SharedKernel;

/// <summary>
/// مستودع المشتريات (G6 — §12.3): أوامر الشراء والاستلامات والفواتير والسداد
/// والمرتجعات — كل ترحيل ذري داخل معاملة واحدة، والسداد بأثر خزينة واحد.
/// </summary>
public interface IPurchasingRepository
{
    // ── الموردون (PAGE-31/32) ───────────────────────────────────────────
    Task<(IReadOnlyList<SupplierRecord> Items, long Total)> SearchSuppliersAsync(string? query, int page, int pageSize, CancellationToken ct = default);
    Task<Result<long>> CreateSupplierAsync(SupplierDraft draft, CancellationToken ct = default);
    Task<Result<long>> SetSupplierStatusAsync(long supplierId, string status, string reasonAr, long userId, CancellationToken ct = default);
    Task<SupplierRecord?> FindSupplierAsync(long supplierId, CancellationToken ct = default);
    /// <summary>كشف حساب المورد (§13): من المستندات المرحّلة فقط — لا رقم حر.</summary>
    Task<SupplierStatement> GetSupplierStatementAsync(long supplierId, DateOnly fromDate, DateOnly toDate, string? docType, CancellationToken ct = default);

    // ── سلة المشتريات وأوامر الشراء (PAGE-24) ───────────────────────────
    Task<Result<long>> CreatePurchaseOrderAsync(PoDraft draft, CancellationToken ct = default);
    Task<PoDetail?> FindPurchaseOrderAsync(long poId, CancellationToken ct = default);
    Task<IReadOnlyList<PoSummary>> ListPurchaseOrdersAsync(string? status, int page, int pageSize, CancellationToken ct = default);
    Task<Result<long>> ReplacePoLinesAsync(long poId, IReadOnlyList<PoLineDraft> lines, CancellationToken ct = default);
    Task<Result<long>> SubmitPoAsync(long poId, CancellationToken ct = default);
    Task<Result<long>> ClosePoAsync(long poId, long userId, string reasonAr, CancellationToken ct = default);
    Task<Result<long>> CancelPoAsync(long poId, CancellationToken ct = default);
    /// <summary>بعد ترحيل استلام مرتبط بأمر: PARTIAL أو RECEIVED — لا مساس بلا مرجع.</summary>
    Task<Result<long>> OnReceiptPostedAsync(long? poId, CancellationToken ct = default);

    // ── الاستلامات (PAGE-25) ────────────────────────────────────────────
    Task<Result<long>> CreateReceiptAsync(ReceiptDraft draft, CancellationToken ct = default);
    Task<ReceiptDetail?> FindReceiptAsync(long receiptId, CancellationToken ct = default);
    Task<IReadOnlyList<ReceiptSummary>> ListReceiptsAsync(string? status, int page, int pageSize, CancellationToken ct = default);
    /// <summary>الترحيل الذري: حركات RECEIVE + دفعات + WAC + حالة الأمر (§12.3 ق1).</summary>
    Task<Result<PostedReceipt>> PostReceiptAsync(ReceiptPostPlan plan, CancellationToken ct = default);
    Task<Result<long>> CancelReceiptAsync(long receiptId, CancellationToken ct = default);

    // ── فواتير الموردين (PAGE-26) ───────────────────────────────────────
    Task<Result<long>> CreateInvoiceAsync(InvoiceDraft draft, CancellationToken ct = default);
    Task<InvoiceDetail?> FindInvoiceAsync(long invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(string? status, long? supplierId, int page, int pageSize, CancellationToken ct = default);
    Task<Result<PostedInvoice>> PostInvoiceAsync(long invoiceId, string? idempotencyKey, long userId, CancellationToken ct = default);
    Task<Result<long>> CancelInvoiceAsync(long invoiceId, CancellationToken ct = default);
    /// <summary>إعادة حساب حالة الفاتورة بعد سداد مرحّل (PAID/PARTIALLY_PAID) — أثر واحد.</summary>
    Task<Result<long>> RecomputeInvoiceStatusAsync(long invoiceId, CancellationToken ct = default);

    // ── السداد (PAGE-26) ────────────────────────────────────────────────
    Task<Result<long>> CreatePaymentAsync(PaymentDraft draft, CancellationToken ct = default);
    Task<PaymentDetail?> FindPaymentAsync(long paymentId, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentSummary>> ListPaymentsAsync(long? supplierId, string? status, CancellationToken ct = default);
    /// <summary>الترحيل الذري: أثر خزينة واحد + تحديث حالات الفواتير المخصَّصة (§12.3 ق3).</summary>
    Task<Result<PostedPayment>> PostPaymentAsync(PaymentPostPlan plan, CancellationToken ct = default);
    Task<Result<long>> CancelPaymentAsync(long paymentId, CancellationToken ct = default);

    // ── مرتجعات الموردين (PAGE-27) ──────────────────────────────────────
    Task<Result<long>> CreateReturnAsync(ReturnDraft draft, CancellationToken ct = default);
    Task<ReturnDetail?> FindReturnAsync(long returnId, CancellationToken ct = default);
    Task<IReadOnlyList<ReturnSummary>> ListReturnsAsync(string? status, long? supplierId, CancellationToken ct = default);
    /// <summary>الترحيل الذري: حركة SUPPLIER_RETURN لكل سطر (بحد ما استُلم من الأصل) + أثر الخزينة إن كان رداً نقدياً.</summary>
    Task<Result<PostedReturn>> PostReturnAsync(ReturnPostPlan plan, CancellationToken ct = default);
    Task<Result<long>> CancelReturnAsync(long returnId, CancellationToken ct = default);

    // ── حسابات الخزينة (قراءة فقط — إنشاؤها وإدارتها في G8) ─────────────
    Task<IReadOnlyList<TreasuryAccountRecord>> ListTreasuryAccountsAsync(bool activeOnly, CancellationToken ct = default);
}

/// <summary>
/// مستودع العملاء (G6 — PAGE-29/30): السجل والتعطيل؛ كشف الحساب من مستنداته
/// (فواتير آجلة G7 + سندات G8) — في G6 تُبنى البنية وتعرض «لا حركات بعد» بصدق.
/// </summary>
public interface ICustomerRepository
{
    Task<(IReadOnlyList<CustomerRecord> Items, long Total)> SearchCustomersAsync(string? query, int page, int pageSize, CancellationToken ct = default);
    Task<Result<long>> CreateCustomerAsync(CustomerDraft draft, CancellationToken ct = default);
    Task<Result<long>> SetCustomerStatusAsync(long customerId, string status, string reasonAr, long userId, CancellationToken ct = default);
    Task<CustomerRecord?> FindCustomerAsync(long customerId, CancellationToken ct = default);
    Task<CustomerStatement> GetCustomerStatementAsync(long customerId, DateOnly fromDate, DateOnly toDate, string? docType, CancellationToken ct = default);
}

// ── سجلات المشتريات ─────────────────────────────────────────────────────────

public sealed record SupplierRecord(
    long SupplierId, string Code, string NameAr, string? NameEn, string? Phone, string? Email, string? TaxId,
    int PaymentTermsDays, decimal? CreditLimit, string AccountStatus, string? Notes, DateTimeOffset CreatedAt);

public sealed record SupplierDraft(
    string Code, string NameAr, string? NameEn, string? Phone, string? Email, string? TaxId,
    int PaymentTermsDays, decimal? CreditLimit, string? Notes);

public sealed record SupplierStatement(
    long PartyId, string PartyNameAr, DateOnly From, DateOnly To,
    decimal Opening, decimal Charges, decimal Credits, decimal Closing,
    IReadOnlyList<StatementLine> Entries);

public sealed record StatementLine(
    string DocType, long DocId, string DocNumber, DateOnly Date, decimal Amount, string Detail);

public sealed record PoDraft(
    long SupplierId, long WarehouseId, DateOnly? ExpectedDate,
    IReadOnlyList<PoLineDraft> Lines, long UserId);

public sealed record PoLineDraft(long SkuId, decimal Qty, long UomId, decimal UnitCost);

public sealed record PoDetail(
    long PoId, string PoNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    long WarehouseId, string WarehouseCode, string Status, DateOnly? ExpectedDate,
    string CreatedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<PoLineInfo> Lines);

public sealed record PoLineInfo(
    long SkuId, string SkuCode, string SkuNameAr, decimal Qty, long UomId, string UomCode, decimal UnitCost);

public sealed record PoSummary(
    long PoId, string PoNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    long WarehouseId, string WarehouseCode, string Status, DateOnly? ExpectedDate, DateTimeOffset CreatedAt);

public sealed record ReceiptDraft(
    long SupplierId, long WarehouseId, long? PoId, string? Notes,
    IReadOnlyList<ReceiptLineDraft> Lines, long UserId);

/// <summary>سطر استلام محسوب في حالة الاستخدام (لقطة التحويل محسوبة قبل المعاملة).</summary>
public sealed record ReceiptLineDraft(
    long SkuId, string SkuCode, decimal Qty, long UomId, string UomCode,
    decimal UnitCost, string? LotCode, DateOnly? LotExpiry,
    long TargetSkuId, decimal ConversionFactor, decimal BaseQty, decimal BaseUnitCost, decimal LineTotal);

public sealed record ReceiptDetail(
    long ReceiptId, string ReceiptNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    long? PoId, string? PoNumber, long WarehouseId, string WarehouseCode, string Status, string? Notes,
    string ReceivedByName, DateTimeOffset? CreatedAt, DateTimeOffset? PostedAt,
    IReadOnlyList<ReceiptLineInfo> Lines);

public sealed record ReceiptLineInfo(
    long LineId, long SkuId, string SkuCode, string SkuNameAr,
    decimal Qty, long UomId, string UomCode, decimal ConversionFactor, decimal UnitCost,
    long? LotId, string? LotCode, DateOnly? LotExpiry, decimal BaseQty, decimal LineTotal);

public sealed record ReceiptSummary(
    long ReceiptId, string ReceiptNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    long WarehouseId, string WarehouseCode, string Status, DateTimeOffset? CreatedAt, DateTimeOffset? PostedAt);

/// <summary>خطة ترحيل الاستلام — الحركات محسوبة في حالة الاستخدام قبل المعاملة (نمط ApproveCountPlan).</summary>
public sealed record ReceiptPostPlan(
    long ReceiptId, long? PoId, string? IdempotencyKey, long BranchId, long UserId,
    IReadOnlyList<ReceiptMovementPlan> Movements);

/// <summary>حركة استلام واحدة: الأثر على صنف المحتوى عند PACK بلقطة العامل (§4.3 ق5).</summary>
public sealed record ReceiptMovementPlan(
    long LineId, long LineSkuId, string SkuCode, long TargetSkuId,
    decimal BaseQty, long SourceUomId, decimal ConversionFactor, decimal BaseUnitCost,
    string? LotCode, DateOnly? LotExpiry, decimal LineTotal);

public sealed record PostedReceipt(
    long ReceiptId, string ReceiptNumber, long[] MovementIds, long[] LotIds,
    int LineCount, decimal TotalBaseQty, decimal TotalCost);

public sealed record InvoiceDraft(
    long SupplierId, long? ReceiptId, string InvoiceNumber, DateOnly InvoiceDate, DateOnly? DueDate,
    decimal Subtotal, decimal TaxTotal,
    IReadOnlyList<InvoiceLineDraft> Lines, long UserId);

public sealed record InvoiceLineDraft(long SkuId, decimal Qty, long UomId, decimal UnitCost);

public sealed record InvoiceDetail(
    long InvoiceId, long SupplierId, string SupplierCode, string SupplierNameAr,
    long? ReceiptId, string? ReceiptNumber, string InvoiceNumber,
    DateOnly InvoiceDate, DateOnly? DueDate,
    decimal Subtotal, decimal TaxTotal, decimal GrandTotal, decimal PaidAmount, decimal RemainingAmount,
    string Status, DateTimeOffset? PostedAt,
    IReadOnlyList<InvoiceLineInfo> Lines);

public sealed record InvoiceLineInfo(long SkuId, string SkuCode, decimal Qty, long UomId, string UomCode, decimal UnitCost, decimal LineTotal);

public sealed record InvoiceSummary(
    long InvoiceId, long SupplierId, string SupplierCode, string SupplierNameAr,
    string InvoiceNumber, DateOnly InvoiceDate, decimal GrandTotal, decimal PaidAmount, string Status);

public sealed record PostedInvoice(long InvoiceId, string NewStatus, decimal GrandTotal);

public sealed record PaymentDraft(
    long SupplierId, long TreasuryAccountId, decimal Amount, string PaymentMethod,
    DateOnly PaymentDate, string? Notes,
    IReadOnlyList<PaymentAllocationDraft> Allocations, long UserId);

public sealed record PaymentAllocationDraft(long InvoiceId, decimal Amount);

public sealed record PaymentDetail(
    long PaymentId, string VoucherNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    long TreasuryAccountId, string TreasuryAccountCode, decimal Amount, string PaymentMethod,
    DateOnly PaymentDate, string Status, string? Notes, DateTimeOffset? PostedAt,
    IReadOnlyList<PaymentAllocationInfo> Allocations);

public sealed record PaymentAllocationInfo(long InvoiceId, string InvoiceNumber, decimal Amount);

public sealed record PaymentSummary(
    long PaymentId, string VoucherNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    decimal Amount, string PaymentMethod, DateOnly PaymentDate, string Status);

public sealed record PaymentPostPlan(long PaymentId, string? IdempotencyKey, long BranchId, long UserId);

public sealed record PostedPayment(
    long PaymentId, string VoucherNumber, long TreasuryTxnId, long[] AffectedInvoiceIds);

public sealed record ReturnDraft(
    long SupplierId, long? ReceiptId, long? InvoiceId, long WarehouseId, string RefundMethod,
    IReadOnlyList<ReturnLineDraft> Lines, long UserId);

public sealed record ReturnLineDraft(
    long SkuId, string SkuCode, decimal Qty, long UomId, string UomCode, decimal UnitCost,
    long TargetSkuId, decimal ConversionFactor, decimal BaseQty, decimal LineTotal);

public sealed record ReturnDetail(
    long ReturnId, string ReturnNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    long? ReceiptId, string? ReceiptNumber, long? InvoiceId, string? InvoiceNumber,
    long WarehouseId, string WarehouseCode, string Status, string RefundMethod, decimal Total,
    string CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? PostedAt,
    IReadOnlyList<ReturnLineInfo> Lines);

public sealed record ReturnLineInfo(
    long LineId, long SkuId, string SkuCode, decimal Qty, long UomId, string UomCode, decimal ConversionFactor,
    decimal UnitCost, decimal LineTotal);

public sealed record ReturnSummary(
    long ReturnId, string ReturnNumber, long SupplierId, string SupplierCode, string SupplierNameAr,
    string Status, string RefundMethod, decimal Total, DateTimeOffset CreatedAt);

/// <summary>خطة ترحيل المرتجع — الحركات محسوبة سلفاً وأثر الخزينة إن كان رداً نقدياً.</summary>
public sealed record ReturnPostPlan(
    long ReturnId, string? IdempotencyKey, long BranchId, long UserId, bool IsCashRefund, decimal Total,
    IReadOnlyList<ReturnMovementPlan> Movements);

/// <summary>حركة مرتجع واحدة: SUPPLIER_RETURN على صنف المحتوى عند PACK.</summary>
public sealed record ReturnMovementPlan(
    long LineId, long LineSkuId, string SkuCode, long TargetSkuId,
    decimal BaseQty, long SourceUomId, decimal ConversionFactor, decimal LineTotal);

public sealed record PostedReturn(
    long ReturnId, long[] MovementIds, long? TreasuryTxnId, bool ReducesPayable, decimal Total);

public sealed record TreasuryAccountRecord(
    long TreasuryAccountId, string Code, string NameAr, string AccountKind, long? BranchId, string Currency, bool IsActive);

// ── سجلات العملاء ───────────────────────────────────────────────────────────

public sealed record CustomerRecord(
    long CustomerId, string Code, string NameAr, string? Phone, string? Email, string? TaxId,
    decimal CreditLimit, bool CreditEnabled, int PaymentTermsDays, string AccountStatus, string? Notes, DateTimeOffset CreatedAt);

public sealed record CustomerDraft(
    string Code, string NameAr, string? Phone, string? Email, string? TaxId,
    decimal CreditLimit, bool CreditEnabled, int PaymentTermsDays, string? Notes);

public sealed record CustomerStatement(
    long PartyId, string PartyNameAr, DateOnly From, DateOnly To,
    decimal Opening, decimal Charges, decimal Credits, decimal Closing,
    IReadOnlyList<StatementLine> Entries);

/// <summary>
/// خرق سياسة مشتريات (تخصيص زائد / تكرار رقم مستند / رد فوق ما استُلم /
/// انتقال حالة غير مسموح) — رسالة عربية برمز مسمى لعرض 409.
/// </summary>
public sealed class PurchasingPolicyException(string code, string userMessageAr) : Exception(userMessageAr)
{
    public string Code { get; } = code;
    public string UserMessageAr { get; } = userMessageAr;
}
