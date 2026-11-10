namespace WeraPro.Contracts.V1;

/// <summary>عقود G7 — نقطة البيع: المبيعات والوزن والدفع والمرتجعات (§9 · §5 · §15.4).</summary>
public static class SalesApi
{
    public const string Prefix = ApiRoutes.Prefix + "/pos";
}

// ── سياق المحطة (§9.1A — شريط الجلسة يعرضه عند الفتح) ─────────────────────────

public sealed record PosContextResponse(
    long StationId,
    string StationCode,
    string StationNameAr,
    long BranchId,
    string BranchNameAr,
    string CashierDisplayAr,
    long? ShiftId,
    int? ShiftNumber,
    DateTimeOffset? ShiftOpenedAtUtc,
    decimal? ShiftOpeningFloat,
    long? DrawerTreasuryAccountId,
    string? DrawerTreasuryAccountCode,
    string TaxMode,                  // VAT_INCLUSIVE (لقطة السياسة)
    decimal TaxStandardRate,         // 0.1400
    DiscountLimitsDto DiscountLimits,
    WeighingSettingsDto Weighing,
    CardProviderStatusDto Card,
    ReceiptPrinterStatusDto ReceiptPrinter);

public sealed record DiscountLimitsDto(decimal CashierPct, decimal SupervisorPct, string OverRequires);

public sealed record WeighingSettingsDto(int PrecisionGrams, int StepGrams, string AmountRounding, string WeightRounding);

/// <summary>حالة مزود البطاقة الصادقة (B-02/D-11): NOT_CONFIGURED أو محاكاة موثقة.</summary>
public sealed record CardProviderStatusDto(
    string ProviderName,             // NOT_CONFIGURED | WeraSandbox-SIMULATED | <المزود الحقيقي عند اعتماده>
    bool Enabled,
    string? NoticeAr);

public sealed record ReceiptPrinterStatusDto(
    bool Assigned,                   // هل للمحطة طابعة بدور إيصال العميل
    string? MediaClass,
    bool Verified,                   // معايرة الوسيط (G10)
    bool AutoRoutingEnabled,
    string? HoldReasonCode,
    string StatusTextAr);            // نص حالة صادق للعرض (§9.1A)

// ── الالتقاط: بحث/باركود/PLU + باركود الوزن المتغير (§9.2 · §5.3) ──────────────

public sealed record PosLookupRequest(string Code);

public sealed record PosItemDto(
    long SkuId,
    string SkuCode,
    string ProductNameAr,
    string SaleMode,                 // COUNT | WEIGHT | PACK | SERIALIZED
    decimal UnitPrice,               // السعر الساري (شامل الضريبة عند السياسة)
    string PriceUnitCode,            // PIECE | KG | ...
    long PriceUomId,
    string TaxTreatment,
    decimal TaxRate,
    long? PackageId,
    decimal? UnitsPerPackage,        // عند PACK: كم وحدة أساسية في العبوة
    string? ContentSkuCode,
    decimal AvailableQty,            // المتاح في مخزن POS للمحطة (رصيد − محجوز)
    string AvailableUnitCode,
    string? ImageUrl,
    WeightPolicyDto? Weight);

public sealed record WeightPolicyDto(
    decimal? MinWeightKg,
    decimal? MaxWeightKg,
    int StepGrams,
    int PrecisionGrams,
    bool AllowManualWeight,
    bool AllowAmountTarget,
    decimal DefaultTareKg,
    int[] QuickPickGrams);           // الأزرار المتوافقة مع خطوة الصنف فقط (§9.3 ق4)

/// <summary>نتيجة الالتقاط: صنف جاهز للبيع أو باركود وزن محلول أو رفض موثق.</summary>
public sealed record PosLookupResponse(
    bool Found,
    PosItemDto? Item,
    bool IsWeightLabel,              // باركود ملصق موزون (WEIGHT_LABEL)
    decimal? LabelWeightKg,
    string? LabelProfileName,
    string? ErrorAr);

public sealed record PosSearchResponse(int Page, int PageSize, long TotalCount, int TotalPages, IReadOnlyList<PosItemDto> Items);

// ── معاينة التسعير الخادمية (§9.0 ق10 — المجاميع من الخادم فقط) ───────────────

public sealed record PosLineRequest(
    long SkuId,
    decimal Qty,                     // الكمية بوحدة البيع (أو الوزن بوحدة البيع للوزني)
    long? UomId,                     // NULL = وحدة البيع الافتراضية للسعر
    decimal? WeightKg,               // صيغة WEIGHT فقط
    string? WeightSource,            // SCALE_READING | QUICK_PICK | MANUAL_WEIGHT | AMOUNT_DERIVED | WEIGHT_LABEL | PACK_PREWEIGHED
    decimal? AmountTarget,
    decimal? TareKg,
    decimal? LineDiscount,           // مبلغ (لا نسبة) — يخضع لحدود الصلاحية
    decimal? PriceOverride,          // تعديل سعر يحتاج sales.price.override + سبباً
    string? PriceOverrideReason,
    string? Notes);

public sealed record PosPreviewRequest(
    IReadOnlyList<PosLineRequest> Lines,
    long? CustomerId,
    decimal? InvoiceDiscount,
    string? InvoiceDiscountReason);

public sealed record PosPreviewLine(
    int LineNo,
    long SkuId,
    string SkuCode,
    string ProductNameAr,
    string SaleMode,
    decimal Qty,
    string UomCode,
    decimal? WeightKg,
    string? WeightSource,
    decimal UnitPrice,
    decimal LineDiscount,
    decimal InvoiceDiscountShare,
    decimal TaxAmount,
    decimal NetAmount,
    decimal LineTotal,
    long LineId = 0,              // معرف السطر الحقيقي (0 في معاينة سلة غير مرحّلة) — لربط المرتجع بالأصل
    decimal RefundedQty = 0m);    // المردود سابقاً من هذا السطر (0 في المعاينة) — حد المرتجع = qty − refunded

public sealed record PosPreviewResponse(
    IReadOnlyList<PosPreviewLine> Lines,
    SaleTotalsDto Totals);

public sealed record SaleTotalsDto(
    decimal GrossTotal,
    decimal LineDiscountTotal,
    decimal InvoiceDiscount,
    decimal TaxTotal,
    decimal NetTotal,
    decimal DueAmount,
    decimal PaidTotal,
    decimal ChangeDue);

// ── إتمام البيع (§9.7 — 12 خطوة، مع Idempotency §9.5/§15.4) ───────────────────

public sealed record PosPaymentRequest(
    string Method,                   // CASH | CARD | CREDIT
    decimal Amount,                  // المبلغ المطبق (النقد يشمل الفكة: المطبق = المستحق أو نصيبه)
    decimal? CashTendered);          // المسلم نقدياً (للفكة) — يخزن في سبب أثر الخزينة

public sealed record PostSaleRequest(
    IReadOnlyList<PosLineRequest> Lines,
    IReadOnlyList<PosPaymentRequest> Payments,
    long? CustomerId,
    decimal? InvoiceDiscount,
    string? InvoiceDiscountReason,
    decimal? CashTendered,
    string? IdempotencyKey,
    long? ApprovalId);               // اعتماد D-03 عند بيع يجعل الرصيد سالباً

public sealed record PostedSaleResponse(
    long InvoiceId,
    string InvoiceNumber,
    string Status,
    SaleTotalsDto Totals,
    long[] MovementIds,
    long[] TreasuryTxnIds,
    long[] PaymentIds,
    long? DrawerEventId,
    PrintOutcomeDto Receipt,
    bool IdempotentReplay);          // true = إعادة إرسال مفتاح Idempotency أعادت الفاتورة الأصلية

/// <summary>نتيجة توجيه الإيصال (§29.4): مهمة أنشئت أو HOLD موثق — لا فشل صامت.</summary>
public sealed record PrintOutcomeDto(
    bool JobCreated,
    long? JobId,
    string? JobStatus,               // QUEUED | HELD_CONFIGURATION_MISMATCH | ...
    string? MediaClass,
    string? HoldReasonCode,
    string NoticeAr);

public sealed record PosSaleListResponse(IReadOnlyList<PosSaleSummaryDto> Items);

public sealed record PosSaleSummaryDto(
    long InvoiceId,
    string InvoiceNumber,
    DateTimeOffset PostedAtUtc,
    string Status,
    decimal NetTotal,
    decimal TaxTotal,
    decimal DueAmount,
    string? CustomerNameAr,
    int LineCount);

public sealed record PosSaleDetailResponse(
    long InvoiceId,
    string InvoiceNumber,
    string Status,
    DateTimeOffset? PostedAtUtc,
    long? CustomerId,
    string? CustomerNameAr,
    long? ShiftId,
    string CashierDisplayAr,
    SaleTotalsDto Totals,
    decimal ReturnedTotal,
    IReadOnlyList<PosPreviewLine> Lines,
    IReadOnlyList<PosPaymentDto> Payments,
    bool CanReturn);

public sealed record PosPaymentDto(string Method, decimal Amount, string Status, string? ProviderName, string? ProviderReference);

// ── تعليق واستعادة السلال (§9.5 — لا تنشئ بيعاً ولا حركة) ─────────────────────

public sealed record HoldCartRequest(
    long? CustomerId,
    string? NoteAr,
    string CartJson);                // لقطة السلة كاملة (سطور العرض الحالية) — JSON من العميل

public sealed record HeldCartDto(
    long CartId,
    DateTimeOffset HeldAtUtc,
    string HeldByDisplayAr,
    string? NoteAr,
    bool Restored,
    string? CartJson = null);      // لقطة السلة للعرض فقط — الاستعادة الفعلية تتم من POS (§9.5)

public sealed record RestoreCartResponse(long CartId, string CartJson, long? CustomerId);

// ── المرتجعات والاستبدال (§9.8 · D-04) ────────────────────────────────────────

public sealed record PostRefundRequest(
    long OriginalInvoiceId,
    IReadOnlyList<RefundLineRequest> Lines,
    string RefundMethod,             // CASH | CARD | CREDIT_REVERSAL — بوسيلة الدفع الأصلية (D-04)
    bool Restock,
    string ReasonAr,
    long ApprovalId,                 // اعتماد مشرف إلزامي (قيد V005 refund_approval_required)
    string? IdempotencyKey);

public sealed record RefundLineRequest(long OriginalLineId, decimal Qty);

public sealed record PostedRefundResponse(
    long RefundId,
    string RefundNumber,
    decimal Total,
    string RefundMethod,
    bool Restock,
    long[] MovementIds,
    long[] TreasuryTxnIds,
    string InvoiceStatusAfter,       // PARTIAL_RETURN | FULL_RETURN
    bool IdempotentReplay);

// ── الوردية ودرج النقد (§9.9 — الفتح والملخص؛ الإغلاق التام في G8) ────────────

public sealed record OpenShiftRequest(decimal OpeningFloat);

public sealed record ShiftDto(long ShiftId, int ShiftNumber, long StationId, string OpenedByDisplayAr,
    DateTimeOffset OpenedAtUtc, decimal OpeningFloat, string Status);

public sealed record ShiftSummaryDto(
    ShiftDto Shift,
    int InvoiceCount,
    decimal NetSales,
    decimal CashCollected,
    decimal CardCollected,
    decimal CreditIssued,
    decimal CashRefunded,
    decimal ReturnsTotal,
    decimal ExpectedCashDrawer);     // افتتاحي + نقدي محصل − مرتجعات نقدية

// ── أحداث الدرج (§9.9 — فتح بصلاحية وسبب وأزمنة صادقة) ────────────────────────

public sealed record DrawerOpenRequest(string ReasonAr);

public sealed record DrawerEventResponse(
    long EventId,
    string EventType,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? RespondedAtUtc,  // NULL عند UNKNOWN — لا ندّعي استجابة (B-01)
    string ResultStatus);            // UNKNOWN حتى تكامل الأجهزة (G10)

// ── الطباعة (§29.4/§29.9 — مهمة مستقلة، فشلها لا يعيد البيع) ──────────────────

public sealed record PrintReceiptRequest(string CopyType);   // CUSTOMER | REPRINT

public sealed record PrintJobDto(
    long JobId,
    long DocumentId,
    string DocumentType,
    string MediaClass,
    string CopyType,
    string Status,
    int AttemptCount,
    string? FailureReasonAr,
    long? RelatedReprintOf,
    DateTimeOffset CreatedAtUtc);

// ── نافذة الوزن خادمياً (§9.3 — الحساب من خدمة المجال دائماً) ─────────────────

public sealed record WeightPreviewRequest(
    long SkuId,
    string Mode,                     // SCALE_READING | QUICK_PICK | MANUAL_WEIGHT | AMOUNT_DERIVED
    decimal? WeightKg,               // للقراءة/اليدوي/السريع
    decimal? AmountTarget,           // للمبلغ المستهدف
    decimal? ScaleReadingKg,
    bool? ScaleStable,
    bool? ScaleConnected,
    DateTimeOffset? ScaleReadAtUtc,
    decimal? TareKg);

public sealed record WeightPreviewResponse(
    bool Accepted,
    decimal WeightKg,                // الوزن المعتمد (بعد الحدود والخطوة)
    string WeightSource,
    decimal? ExactWeightGrams,       // للمبلغ المستهدف: الدقيق مع المعروض معاً (D-07)
    decimal? DisplayWeightGrams,
    decimal UnitPricePerKg,
    decimal Amount,                  // المبلغ النهائي بعد التقريب
    string? ErrorAr,
    string? ErrorCode);
