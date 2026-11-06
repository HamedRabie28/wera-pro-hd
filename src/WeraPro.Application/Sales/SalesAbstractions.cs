using WeraPro.Domain.Sales;
using WeraPro.Domain.SharedKernel;

namespace WeraPro.Application.Sales;

/// <summary>خرق سياسة مبيعات موثق برمز ثابت ورسالة عربية — يرميه المستودع وتحوله حالة الاستخدام إلى Result.Fail.</summary>
public sealed class SalesPolicyException(string code, string userMessageAr) : Exception(userMessageAr)
{
    public string Code { get; } = code;
    public string UserMessageAr { get; } = userMessageAr;
}

// ── بيانات قراءة يشترك فيها حالات الاستخدام ──────────────────────────────────

public sealed record PosStationData(
    long StationId, string StationCode, string StationNameAr,
    long BranchId, string BranchNameAr);

public sealed record DrawerAccountData(long TreasuryAccountId, string Code, string NameAr);

/// <summary>عميل للبيع الآجل (D-05) — الرصيد القائم يُشتق من الدفتر لا يُخزن.</summary>
public sealed record CustomerCreditData(
    long CustomerId, string NameAr, bool CreditEnabled, decimal CreditLimit, string AccountStatus);

/// <summary>تعريف ملف باركود وزن متغير نشط (catalog.vw_barcode_profiles).</summary>
public sealed record VwProfileData(
    long ProfileId, string Name, string Prefix, int TotalLength,
    int PluStartPos, int PluLength, string ValueType,
    int ValueStartPos, int ValueLength, int ValueDecimals,
    long? ValueUnitId, string CheckDigitRule);

/// <summary>
/// كل ما يلزم لتسعير سطر بيع من طرف الخادم (§9.0 ق10: الواجهة ليست مصدر الحقيقة):
/// السعر الساري وصيغة البيع والتحويلات والعبوة والمتاح وWAC.
/// </summary>
public sealed record SaleItemData(
    long SkuId, string SkuCode, long ProductId, string ProductNameAr, string SaleMode,
    long SaleUomId, string SaleUomCode, long BaseUomId, string BaseUomCode,
    decimal UnitPrice, long? PriceListId, long PriceUomId, string PriceUomCode,
    string TaxTreatment, decimal TaxRate,
    bool? AllowNegative,
    long? PackageId, decimal? UnitsPerPackage, long? ContentSkuId, string? ContentSkuCode,
    long? ContentBaseUomId, decimal ContentCostAvg,
    decimal BalanceQty, decimal ReservedQty, decimal AvailableQty,
    string? ImageUrl,
    int? WeightPrecisionG, int? WeightStepG, bool? AllowManualWeight, bool? AllowAmountTarget,
    decimal? MinWeightKg, decimal? MaxWeightKg, decimal DefaultTareKg);

/// <summary>تعيين طابعة إيصال المحطة (print.printer_document_roles + printer_profiles).</summary>
public sealed record StationReceiptPrinterData(
    long PrinterProfileId, int PrinterProfileVersion, string MediaClass,
    decimal PrintableWidthMm, int Dpi, decimal? VerifiedMediaWidthMm,
    bool IsCalibrated, bool IsAutoRoutingEnabled);

// ── خطط الترحيل (يبنيها حال الاستخدام وينفذها المستودع ذرياً — نمط G6) ─────────

public sealed record SaleLinePostPlan(
    int LineNo,
    long SkuId, string SkuCode, string ProductNameAr, string SaleMode,
    long? PackageId, decimal Qty, long UomId, string UomCode, decimal ConversionFactor, decimal BaseQty,
    decimal? WeightKg, string? WeightSource, decimal? AmountTarget, decimal? TareKg,
    decimal UnitPrice, long? PriceListId, string TaxTreatment, decimal TaxRate,
    decimal LineDiscount, decimal InvoiceDiscountShare, decimal TaxAmount, decimal NetAmount,
    decimal CostSnapshot, string CostMethod,
    long MovementSkuId,            // صنف المحتوى عند PACK — الأثر الواحد (§4.3 ق5)
    string? Notes);

/// <summary>دفعة واحدة في الفاتورة — أثر خزينة واحد للنقد وأثر مزود للبطاقة وذمة للآجل.</summary>
public sealed record SalePaymentPostPlan(
    string Method, decimal Amount, string Status,
    string? ProviderName, string? ProviderReference, long? TreasuryAccountId);

public sealed record SalePostPlan(
    long BranchId, long StationId, long? ShiftId, long CashierUserId, long? CustomerId,
    string Currency, string TaxMode,
    decimal GrossTotal, decimal LineDiscountTotal, decimal InvoiceDiscount, decimal TaxTotal,
    decimal NetTotal, decimal DueAmount, decimal PaidTotal,
    IReadOnlyList<SaleLinePostPlan> Lines,
    IReadOnlyList<SalePaymentPostPlan> Payments,
    decimal? CashTendered,
    string? IdempotencyKey,
    long? NegativeApprovalId);      // اعتماد D-03 عند بيع يُنقص الرصيد تحت الصفر

public sealed record PostedSaleResult(
    long InvoiceId, string InvoiceNumber, long[] MovementIds, long[] TreasuryTxnIds,
    long[] PaymentIds, long? DrawerEventId,
    PrintOutcome Receipt,
    bool IdempotentReplay);

public sealed record RefundLinePostPlan(
    long OriginalLineId, decimal Qty, decimal UnitRefund, decimal LineTotal);

public sealed record RefundPostPlan(
    long OriginalInvoiceId, long BranchId, long CreatedByUserId, long ApprovalId,
    string RefundMethod, bool Restock, string ReasonAr,
    decimal Total, IReadOnlyList<RefundLinePostPlan> Lines,
    string? IdempotencyKey,
    long? ShiftId = null,    // G8/V019: وردية الرد الفعلي — النقد خرج من درجها (مطابقة الإقفال)
    long? StationId = null); // G8/V019: محطة الرد — درج المحطة أولاً

public sealed record PostedRefundResult(
    long RefundId, string RefundNumber, long[] MovementIds, long[] TreasuryTxnIds,
    string InvoiceStatusAfter, bool IdempotentReplay);

public sealed record HoldCartPlan(
    long StationId, long HeldByUserId, long? CustomerId, string? NoteAr, string CartJson);

public sealed record HeldCartInfo(
    long CartId, DateTimeOffset HeldAtUtc, string HeldByDisplayAr, string? NoteAr,
    long? CustomerId, bool Restored);

public sealed record RestoredCart(long CartId, string CartJson, long? CustomerId);

public sealed record ShiftInfo(
    long ShiftId, int ShiftNumber, long StationId, string OpenedByDisplayAr,
    DateTimeOffset OpenedAtUtc, decimal OpeningFloat, string Status);

public sealed record ShiftSummaryData(
    ShiftInfo Shift, int InvoiceCount, decimal NetSales, decimal CashCollected,
    decimal CardCollected, decimal CreditIssued, decimal CashRefunded, decimal ReturnsTotal,
    decimal ExpectedCashDrawer);

public sealed record DrawerEventData(
    long EventId, string EventType, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? RespondedAtUtc, string ResultStatus);

public sealed record PrintOutcome(
    bool JobCreated, long? JobId, string? JobStatus, string? MediaClass,
    string? HoldReasonCode, string NoticeAr);

public sealed record PrintJobInfo(
    long JobId, long DocumentId, string DocumentType, string MediaClass, string CopyType,
    string Status, int AttemptCount, string? FailureReasonAr, long? RelatedReprintOf,
    DateTimeOffset CreatedAtUtc);

public sealed record CardChargeResult(bool Approved, string Reference, string Status, string? DeclineReasonAr);

// ── بوابة الدفع بالبطاقة (§9.6) — التجربة الوحيدة الصادقة المتاحة قبل اعتماد D-11
//    هي محاكاة SANDBOX موسومة بوضوح؛ المزود الحقيقي يُوصل عند حسم B-02/D-11 ──

public interface ICardPaymentGateway
{
    /// <summary>اسم المزود كما يُخزن في sales.payments.provider_name — يوسم المحاكاة بوضوح.</summary>
    string ProviderName { get; }

    Task<CardChargeResult> ChargeAsync(decimal amount, string idempotencyKey, CancellationToken ct = default);

    /// <summary>تعويض عند فشل ترحيل الفاتورة بعد نجاح الشحن — لا يبقى شحن بلا فاتورة.</summary>
    Task VoidAsync(string reference, CancellationToken ct = default);
}

// ── مستودع المبيعات — كل الترحيلات ذرية وتعيد النتيجة أو ترمي SalesPolicyException ──

public interface ISalesRepository
{
    // القراءات
    Task<PosStationData?> GetStationAsync(long stationId, CancellationToken ct = default);
    Task<DrawerAccountData?> GetDrawerAccountAsync(long branchId, long? stationId = null, CancellationToken ct = default);   // G8: درج المحطة أولاً
    Task<DrawerAccountData?> GetProviderAccountAsync(CancellationToken ct = default);
    Task<long?> GetPosWarehouseIdAsync(long branchId, CancellationToken ct = default);
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task<SaleItemData?> GetSaleItemDataAsync(long skuId, long warehouseId, long? priceListId, CancellationToken ct = default);
    Task<IReadOnlyList<VwProfileData>> GetActiveVwProfilesAsync(CancellationToken ct = default);
    Task<CustomerCreditData?> GetCustomerCreditAsync(long customerId, CancellationToken ct = default);
    Task<decimal> GetCustomerOutstandingCreditAsync(long customerId, CancellationToken ct = default);
    Task<StationReceiptPrinterData?> GetStationReceiptPrinterAsync(long stationId, CancellationToken ct = default);
    Task<long?> FindInvoiceIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<long?> FindRefundIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    // الالتقاط والبحث
    Task<long?> FindSkuIdByBarcodeAsync(string barcodeNormalized, CancellationToken ct = default);
    Task<long?> FindSkuIdBySkuCodeAsync(string skuCode, CancellationToken ct = default);
    Task<long?> FindSkuIdByPluAsync(string pluCode, CancellationToken ct = default);
    Task<IReadOnlyList<SaleItemData>> SearchSaleItemsAsync(string query, int page, int pageSize, long warehouseId, CancellationToken ct = default);
    Task<(IReadOnlyList<SaleItemData> Items, long TotalCount)> SearchSaleItemsPagedAsync(string query, int page, int pageSize, long warehouseId, CancellationToken ct = default);

    // الترحيل
    Task<PostedSaleResult> PostSaleAsync(SalePostPlan plan, CancellationToken ct = default);
    Task<PostedRefundResult> PostRefundAsync(RefundPostPlan plan, CancellationToken ct = default);
    Task<Result<long>> VoidDraftInvoiceAsync(long invoiceId, long userId, string reasonAr, CancellationToken ct = default);

    // السلال المعلقة (§9.5)
    Task<long> HoldCartAsync(HoldCartPlan plan, CancellationToken ct = default);
    Task<IReadOnlyList<HeldCartInfo>> ListHeldCartsAsync(long stationId, bool openOnly, CancellationToken ct = default);
    Task<RestoredCart?> RestoreCartAsync(long cartId, long restoredByUserId, CancellationToken ct = default);

    // الورديات (§9.9 — الفتح والملخص؛ الإغلاق التام في G8)
    Task<Result<ShiftInfo>> OpenShiftAsync(long stationId, long userId, decimal openingFloat, CancellationToken ct = default);
    Task<ShiftInfo?> GetCurrentShiftAsync(long stationId, CancellationToken ct = default);
    Task<ShiftSummaryData?> GetShiftSummaryAsync(long shiftId, CancellationToken ct = default);

    // درج النقد
    Task<DrawerEventData> RecordDrawerEventAsync(
        long stationId, long? shiftId, long userId, string eventType, string? reasonAr, CancellationToken ct = default);

    // الطباعة (§29.4/§29.9)
    Task<PrintOutcome> CreateReceiptJobAsync(
        long invoiceId, long stationId, long userId, string copyType, long? reprintOfJobId, CancellationToken ct = default);
    Task<PrintJobInfo?> GetPrintJobAsync(long jobId, CancellationToken ct = default);
    Task<IReadOnlyList<PrintJobInfo>> ListPrintJobsAsync(long invoiceId, CancellationToken ct = default);

    // عرض الفواتير (لمسار المرتجع والإيصال)
    Task<SaleDetailData?> GetSaleAsync(long invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<SaleSummaryData>> ListRecentSalesAsync(long stationId, long? shiftId, int limit, CancellationToken ct = default);

    /// <summary>بيانات مرتجع مُرحّل — لإعادة Idempotency الصادقة.</summary>
    Task<RefundReplayData?> GetRefundAsync(long refundId, CancellationToken ct = default);
}

public sealed record RefundReplayData(
    long RefundId, string RefundNumber, decimal Total, string RefundMethod,
    bool Restock, string InvoiceStatusAfter);

/// <summary>فاتورة مفصلة للعرض ومسار المرتجع — السطور مع المتبقي القابل للرد.</summary>
public sealed record SaleDetailData(
    long InvoiceId, string InvoiceNumber, string Status, DateTimeOffset? PostedAtUtc,
    long BranchId, long? CustomerId, string? CustomerNameAr, long? ShiftId, string CashierDisplayAr,
    decimal GrossTotal, decimal LineDiscountTotal, decimal InvoiceDiscount,
    decimal TaxTotal, decimal NetTotal, decimal DueAmount, decimal PaidTotal, decimal ReturnedTotal,
    IReadOnlyList<SaleLineDetail> Lines,
    IReadOnlyList<SalePaymentDetail> Payments);

public sealed record SaleLineDetail(
    long LineId, int LineNo, long SkuId, string SkuCode, string ProductNameAr, string SaleMode,
    decimal Qty, string UomCode, decimal ConversionFactor, decimal BaseQty,
    decimal? WeightKg, string? WeightSource, decimal? AmountTarget,
    decimal UnitPrice, decimal LineDiscount, decimal InvoiceDiscountShare,
    decimal TaxAmount, decimal NetAmount, decimal LineTotal,
    decimal RefundedQty);           // المردود سابقاً من هذا السطر (الحد: qty − refunded)

public sealed record SalePaymentDetail(string Method, decimal Amount, string Status, string? ProviderName, string? ProviderReference);

public sealed record SaleSummaryData(
    long InvoiceId, string InvoiceNumber, DateTimeOffset PostedAtUtc, string Status,
    decimal NetTotal, decimal TaxTotal, decimal DueAmount, string? CustomerNameAr, int LineCount);
