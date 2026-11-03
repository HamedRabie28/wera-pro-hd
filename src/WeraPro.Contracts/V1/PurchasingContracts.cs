namespace WeraPro.Contracts.V1;

// ── الأطراف: الموردون والعملاء (G6 — §13) ───────────────────────────────────

public sealed record SupplierDto(
    long SupplierId,
    string Code,
    string NameAr,
    string? NameEn,
    string? Phone,
    string? Email,
    string? TaxId,
    int PaymentTermsDays,
    decimal? CreditLimit,
    string AccountStatus,     // ACTIVE | SUSPENDED | ARCHIVED — تعطيل لا حذف (§13)
    string? Notes,
    DateTimeOffset CreatedAt);

public sealed record SupplierSearchResponse(
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<SupplierDto> Items);

public sealed record CreateSupplierRequest(
    string Code,
    string NameAr,
    string? NameEn,
    string? Phone,
    string? Email,
    string? TaxId,
    int PaymentTermsDays,
    decimal? CreditLimit,
    string? Notes);

public sealed record SetPartyStatusRequest(string AccountStatus, string? ReasonAr);

public sealed record CustomerDto(
    long CustomerId,
    string Code,
    string NameAr,
    string? Phone,
    string? Email,
    string? TaxId,
    decimal CreditLimit,
    bool CreditEnabled,       // الآجل معطل افتراضياً بانتظار D-05 (تعليق V005)
    int PaymentTermsDays,
    string AccountStatus,
    string? Notes,
    DateTimeOffset CreatedAt);

public sealed record CustomerSearchResponse(
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<CustomerDto> Items);

public sealed record CreateCustomerRequest(
    string Code,
    string NameAr,
    string? Phone,
    string? Email,
    string? TaxId,
    decimal CreditLimit,
    bool CreditEnabled,
    int PaymentTermsDays,
    string? Notes);

/// <summary>بند كشف حساب طرف (§13): رصيد افتتاحي + حركة + ختامي، قابل للتصفية.</summary>
public sealed record PartyStatementEntryDto(
    string DocType,           // purch.invoice | purch.payment | purch.return
    long DocId,
    string DocNumber,
    DateOnly Date,
    decimal Amount,           // موقعة: + زيادة ما على الطرف، − تخفيض
    string Detail);

public sealed record PartyStatementResponse(
    long PartyId,
    string PartyNameAr,
    DateOnly FromDate,
    DateOnly ToDate,
    decimal OpeningBalance,
    decimal TotalCharges,
    decimal TotalCredits,
    decimal ClosingBalance,
    IReadOnlyList<PartyStatementEntryDto> Entries);

// ── سلة المشتريات وأوامر الشراء (G6 — §12.2 ق4 · PAGE-24) ──────────────────

public sealed record PurchaseOrderDto(
    long PoId,
    string PoNumber,
    long SupplierId,
    string SupplierCode,
    string SupplierNameAr,
    long WarehouseId,
    string WarehouseCode,
    string Status,            // DRAFT (سلة/مسودة) | ORDERED | PARTIAL | RECEIVED | CLOSED | CANCELLED
    DateOnly? ExpectedDate,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    decimal LinesTotal);

public sealed record PurchaseOrderLineDto(
    long SkuId,
    string SkuCode,
    string SkuNameAr,
    decimal Qty,              // بوحدة الشراء
    long UomId,
    string UomCode,
    decimal UnitCost);

public sealed record PurchaseOrderListResponse(
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<PurchaseOrderDto> Items);

public sealed record CreatePurchaseOrderRequest(
    long SupplierId,
    long WarehouseId,
    DateOnly? ExpectedDate,
    IReadOnlyList<CreatePurchaseOrderLineRequest> Lines);
public sealed record CreatePurchaseOrderLineRequest(long SkuId, decimal Qty, long UomId, decimal UnitCost);

public sealed record ReplacePurchaseOrderLinesRequest(IReadOnlyList<CreatePurchaseOrderLineRequest> Lines);

public sealed record PoStatusChangeResponse(long PoId, string NewStatus);

// ── استلامات الموردين (G6 — §12.3 ق1 · PAGE-25) ────────────────────────────

public sealed record ReceiptDto(
    long ReceiptId,
    string ReceiptNumber,
    long SupplierId,
    string SupplierCode,
    string SupplierNameAr,
    long? PoId,
    string? PoNumber,
    long WarehouseId,
    string WarehouseCode,
    string Status,
    string? Notes,
    string ReceivedByName,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? PostedAt,
    IReadOnlyList<ReceiptLineDto> Lines);

public sealed record ReceiptLineDto(
    long LineId,
    long SkuId,
    string SkuCode,
    string SkuNameAr,
    decimal Qty,               // بوحدة الاستلام
    long UomId,
    string UomCode,
    decimal ConversionFactor,  // لقطة التحويل للوحدة الأساسية
    decimal UnitCost,          // تكلفة الوحدة المستلمة (بوحدة الاستلام)
    long? LotId,
    string? LotCode,
    DateOnly? LotExpiry,
    decimal BaseQty,           // qty × conversion_factor
    decimal LineTotal);

public sealed record ReceiptListResponse(
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<ReceiptDto> Items);

public sealed record CreateReceiptRequest(
    long SupplierId,
    long WarehouseId,
    long? PoId,
    string? Notes,
    IReadOnlyList<CreateReceiptLineRequest> Lines);
public sealed record CreateReceiptLineRequest(
    long SkuId,
    decimal Qty,
    long UomId,
    decimal UnitCost,
    string? LotCode,
    DateOnly? LotExpiry);

/// <summary>نتيجة ترحيل الاستلام — الحدث الذي زاد المخزون (بوابة القبول: تحويل صحيح).</summary>
public sealed record PostReceiptResponse(
    long ReceiptId,
    string ReceiptNumber,
    long[] MovementIds,
    int LineCount,
    decimal TotalBaseQty,
    decimal TotalCost);

public sealed record PostDocumentRequest(string? IdempotencyKey);

// ── فواتير الموردين (G6 — §12.3 · PAGE-26: التزام مالي لا حركة مخزون) ───────

public sealed record SupplierInvoiceDto(
    long InvoiceId,
    long SupplierId,
    string SupplierCode,
    string SupplierNameAr,
    long? ReceiptId,
    string? ReceiptNumber,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    decimal Subtotal,
    decimal TaxTotal,
    decimal GrandTotal,
    decimal PaidAmount,        // Σ تخصيصات السدادات المرحّلة
    decimal RemainingAmount,
    string Status,
    DateTimeOffset? PostedAt,
    IReadOnlyList<SupplierInvoiceLineDto> Lines);

public sealed record SupplierInvoiceLineDto(
    long SkuId,
    string SkuCode,
    decimal Qty,
    long UomId,
    string UomCode,
    decimal UnitCost,
    decimal LineTotal);

public sealed record SupplierInvoiceListResponse(
    int Page,
    int PageSize,
    long TotalCount,
    IReadOnlyList<SupplierInvoiceDto> Items);

public sealed record CreateSupplierInvoiceRequest(
    long SupplierId,
    long? ReceiptId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    decimal Subtotal,
    decimal TaxTotal,
    IReadOnlyList<CreateSupplierInvoiceLineRequest> Lines);
public sealed record CreateSupplierInvoiceLineRequest(long SkuId, decimal Qty, long UomId, decimal UnitCost);

public sealed record PostInvoiceResponse(long InvoiceId, string NewStatus, decimal GrandTotal);

// ── سداد الموردين (G6 — §12.3 ق3: أثر خزينة واحد لا مصروف) ─────────────────

public sealed record TreasuryAccountDto(
    long TreasuryAccountId,
    string Code,
    string NameAr,
    string AccountKind,     // CASH_DRAWER | SAFE | BANK | PAYMENT_PROVIDER
    long? BranchId,
    string Currency,
    bool IsActive);

public sealed record TreasuryAccountListResponse(IReadOnlyList<TreasuryAccountDto> Items);

public sealed record SupplierPaymentDto(
    long PaymentId,
    string VoucherNumber,
    long SupplierId,
    string SupplierCode,
    string SupplierNameAr,
    long TreasuryAccountId,
    string TreasuryAccountCode,
    decimal Amount,
    string PaymentMethod,     // CASH | BANK_TRANSFER | CHEQUE | CARD
    DateOnly PaymentDate,
    string Status,
    string? Notes,
    DateTimeOffset? PostedAt,
    IReadOnlyList<PaymentAllocationDto> Allocations);

public sealed record PaymentAllocationDto(long InvoiceId, string InvoiceNumber, decimal Amount);

public sealed record SupplierPaymentListResponse(IReadOnlyList<SupplierPaymentDto> Items);

public sealed record CreateSupplierPaymentRequest(
    long SupplierId,
    long TreasuryAccountId,
    decimal Amount,
    string PaymentMethod,
    DateOnly PaymentDate,
    string? Notes,
    IReadOnlyList<CreatePaymentAllocationRequest> Allocations);
public sealed record CreatePaymentAllocationRequest(long InvoiceId, decimal Amount);

public sealed record PostPaymentResponse(
    long PaymentId,
    string VoucherNumber,
    long TreasuryTxnId,       // الأثر الواحد على الخزينة
    long[] AffectedInvoiceIds);

// ── مرتجعات الموردين (G6 — §12.3 ق4 · PAGE-27) ─────────────────────────────

public sealed record SupplierReturnDto(
    long ReturnId,
    string ReturnNumber,
    long SupplierId,
    string SupplierCode,
    string SupplierNameAr,
    long? ReceiptId,
    string? ReceiptNumber,
    long? InvoiceId,
    string? InvoiceNumber,
    long WarehouseId,
    string WarehouseCode,
    string Status,
    string RefundMethod,     // CASH | CREDIT_NOTE | BANK_TRANSFER
    decimal Total,
    string CreatedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PostedAt,
    IReadOnlyList<SupplierReturnLineDto> Lines);

public sealed record SupplierReturnLineDto(
    long SkuId,
    string SkuCode,
    decimal Qty,
    long UomId,
    string UomCode,
    decimal ConversionFactor,
    decimal UnitCost,
    decimal LineTotal);

public sealed record SupplierReturnListResponse(IReadOnlyList<SupplierReturnDto> Items);

public sealed record CreateSupplierReturnRequest(
    long SupplierId,
    long? ReceiptId,
    long? InvoiceId,
    long WarehouseId,
    string RefundMethod,
    IReadOnlyList<CreateSupplierReturnLineRequest> Lines);
public sealed record CreateSupplierReturnLineRequest(long SkuId, decimal Qty, long UomId, decimal UnitCost);

public sealed record PostReturnResponse(
    long ReturnId,
    long[] MovementIds,
    long? TreasuryTxnId,      // رد نقدي/تحويل فقط — الإشعار الدائن بلا خزينة
    bool ReducesPayable);
