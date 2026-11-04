namespace WeraPro.Application.Purchasing;

using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Catalog;
using WeraPro.Domain.Catalog;
using WeraPro.Domain.Purchasing;
using WeraPro.Domain.SharedKernel;

// ═══════════════════════════════════════════════════════════════════════════
// حالات استخدام المشتريات (G6 — §12.3): كل التحقق هنا قبل أي كتابة، والتنفيذ
// الذري في المستودع — الاستلام هو الذي يزيد المخزون (لا الفاتورة) §12.3 ق1،
// والسداد أثر خزينة واحد لا يُسجل مصروفاً §12.3 ق3.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>الموردون (PAGE-31/32): سجل وتعطيل (لا حذف) وكشف حساب من المستندات.</summary>
public sealed class ManageSuppliersUseCase
{
    private readonly IPurchasingRepository _purchasing;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageSuppliersUseCase(IPurchasingRepository purchasing, IAuditWriter audit, ICurrentUser user)
    {
        _purchasing = purchasing;
        _audit = audit;
        _user = user;
    }

    public async Task<Contracts.V1.SupplierSearchResponse> ListAsync(string? query, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (items, total) = await _purchasing.SearchSuppliersAsync(query?.Trim(), page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        return new Contracts.V1.SupplierSearchResponse(page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), total,
            [.. items.Select(ToDto)]);
    }

    public async Task<Result<Contracts.V1.SupplierDto>> CreateAsync(Contracts.V1.CreateSupplierRequest request, string? correlationId, CancellationToken ct = default)
    {
        var code = request.Code?.Trim() ?? string.Empty;
        if (code.Length is < 2 or > 24)
        {
            return Result.Fail<Contracts.V1.SupplierDto>(new("SUPPLIER_CODE_INVALID", "كود المورد بين محرفين و24"));
        }
        if (string.IsNullOrWhiteSpace(request.NameAr) || request.NameAr.Trim().Length < 2)
        {
            return Result.Fail<Contracts.V1.SupplierDto>(new("SUPPLIER_NAME_REQUIRED", "اسم المورد مطلوب"));
        }
        if (request.PaymentTermsDays < 0)
        {
            return Result.Fail<Contracts.V1.SupplierDto>(new("SUPPLIER_TERMS_INVALID", "أيام السداد لا تكون سالبة"));
        }
        if (request.CreditLimit is { } limit && limit < 0)
        {
            return Result.Fail<Contracts.V1.SupplierDto>(new("SUPPLIER_CREDIT_INVALID", "حد الائتمان لا يكون سالباً"));
        }

        Result<long> created;
        try
        {
            created = await _purchasing.CreateSupplierAsync(new SupplierDraft(
                code.ToUpperInvariant(), request.NameAr.Trim(), request.NameEn?.Trim(), request.Phone?.Trim(),
                request.Email?.Trim(), request.TaxId?.Trim(), request.PaymentTermsDays, request.CreditLimit,
                request.Notes?.Trim()), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.SupplierDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierDto>(created.Error!);
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.suppliers", EntityId: created.Value, Action: "SUPPLIER_CREATE", Result: "SUCCESS",
            Reason: code, CorrelationId: correlationId), ct);
        var row = await _purchasing.FindSupplierAsync(created.Value, ct);
        return Result.Ok(ToDto(row!));
    }

    public async Task<Result<long>> SetStatusAsync(long supplierId, Contracts.V1.SetPartyStatusRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!PartyStatuses.All.Contains(request.AccountStatus))
        {
            return Result.Fail<long>(new("PARTY_STATUS_INVALID", "الحالة ACTIVE أو SUSPENDED أو ARCHIVED"));
        }
        var current = await _purchasing.FindSupplierAsync(supplierId, ct);
        if (current is null)
        {
            return Result.Fail<long>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (!PartyStatuses.CanSet(current.AccountStatus, request.AccountStatus))
        {
            return Result.Fail<long>(new(
                "PARTY_STATUS_TRANSITION_INVALID", "الأرشيف نهائي — لا تنشيط ولا تعليق بعده (§13: تعطيل لا حذف)"));
        }
        var result = await _purchasing.SetSupplierStatusAsync(supplierId, request.AccountStatus, request.ReasonAr ?? string.Empty, _user.UserId, ct);
        if (result.IsSuccess)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "purch.suppliers", EntityId: supplierId, Action: "SUPPLIER_STATUS_CHANGE",
                Result: "SUCCESS", Reason: $"{request.AccountStatus} · {request.ReasonAr}", CorrelationId: correlationId), ct);
        }
        return result;
    }

    public async Task<Result<Contracts.V1.SupplierDto>> GetAsync(long supplierId, CancellationToken ct = default)
    {
        var row = await _purchasing.FindSupplierAsync(supplierId, ct);
        return row is null
            ? Result.Fail<Contracts.V1.SupplierDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"))
            : Result.Ok(ToDto(row));
    }

    /// <summary>كشف حساب المورد (§13): افتتاحي + حركة + ختامي قابل للتصفية — من المستندات المرحّلة فقط.</summary>
    public async Task<Result<Contracts.V1.PartyStatementResponse>> StatementAsync(
        long supplierId, DateOnly? from, DateOnly? to, string? docType, CancellationToken ct = default)
    {
        var supplier = await _purchasing.FindSupplierAsync(supplierId, ct);
        if (supplier is null)
        {
            return Result.Fail<Contracts.V1.PartyStatementResponse>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var start = from ?? end.AddDays(-90);
        var s = await _purchasing.GetSupplierStatementAsync(supplierId, start, end, docType?.Trim(), ct);
        return Result.Ok(ToStatement(s));
    }

    internal static Contracts.V1.PartyStatementResponse ToStatement(SupplierStatement s) => new(
        s.PartyId, s.PartyNameAr, s.From, s.To, s.Opening, s.Charges, s.Credits, s.Closing,
        [.. s.Entries.Select(e => new Contracts.V1.PartyStatementEntryDto(e.DocType, e.DocId, e.DocNumber, e.Date, e.Amount, e.Detail))]);

    private static Contracts.V1.SupplierDto ToDto(SupplierRecord r) => new(
        r.SupplierId, r.Code, r.NameAr, r.NameEn, r.Phone, r.Email, r.TaxId,
        r.PaymentTermsDays, r.CreditLimit, r.AccountStatus, r.Notes, r.CreatedAt);
}

/// <summary>العملاء (PAGE-29/30): السجل والتعطيل؛ كشف الحساب من مستنداته (فواتير آجلة G7 + سندات G8).</summary>
public sealed class ManageCustomersUseCase
{
    private readonly ICustomerRepository _customers;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageCustomersUseCase(ICustomerRepository customers, IAuditWriter audit, ICurrentUser user)
    {
        _customers = customers;
        _audit = audit;
        _user = user;
    }

    public async Task<Contracts.V1.CustomerSearchResponse> ListAsync(string? query, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (items, total) = await _customers.SearchCustomersAsync(query?.Trim(), page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        return new Contracts.V1.CustomerSearchResponse(page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), total,
            [.. items.Select(ToDto)]);
    }

    public async Task<Result<Contracts.V1.CustomerDto>> CreateAsync(Contracts.V1.CreateCustomerRequest request, string? correlationId, CancellationToken ct = default)
    {
        var code = request.Code?.Trim() ?? string.Empty;
        if (code.Length is < 2 or > 24)
        {
            return Result.Fail<Contracts.V1.CustomerDto>(new("CUSTOMER_CODE_INVALID", "كود العميل بين محرفين و24"));
        }
        if (string.IsNullOrWhiteSpace(request.NameAr) || request.NameAr.Trim().Length < 2)
        {
            return Result.Fail<Contracts.V1.CustomerDto>(new("CUSTOMER_NAME_REQUIRED", "اسم العميل مطلوب"));
        }
        if (request.CreditLimit < 0)
        {
            return Result.Fail<Contracts.V1.CustomerDto>(new("CUSTOMER_CREDIT_INVALID", "حد الائتمان لا يكون سالباً"));
        }
        if (request.CreditEnabled && request.CreditLimit <= 0)
        {
            return Result.Fail<Contracts.V1.CustomerDto>(new(
                "CUSTOMER_CREDIT_NEEDS_LIMIT", "تمكين الآجل يوجب حداً أكبر من صفر (قيد V005 cust_credit_needs_limit — الآجل معطل بانتظار D-05)"));
        }

        Result<long> created;
        try
        {
            created = await _customers.CreateCustomerAsync(new CustomerDraft(
                code.ToUpperInvariant(), request.NameAr.Trim(), request.Phone?.Trim(), request.Email?.Trim(),
                request.TaxId?.Trim(), request.CreditLimit, request.CreditEnabled, request.PaymentTermsDays,
                request.Notes?.Trim()), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.CustomerDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.CustomerDto>(created.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "sales.customers", EntityId: created.Value, Action: "CUSTOMER_CREATE", Result: "SUCCESS",
            Reason: code, CorrelationId: correlationId), ct);
        var row = await _customers.FindCustomerAsync(created.Value, ct);
        return Result.Ok(ToDto(row!));
    }

    public async Task<Result<long>> SetStatusAsync(long customerId, Contracts.V1.SetPartyStatusRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!PartyStatuses.All.Contains(request.AccountStatus))
        {
            return Result.Fail<long>(new("PARTY_STATUS_INVALID", "الحالة ACTIVE أو SUSPENDED أو ARCHIVED"));
        }
        var current = await _customers.FindCustomerAsync(customerId, ct);
        if (current is null)
        {
            return Result.Fail<long>(new("CUSTOMER_NOT_FOUND", "العميل غير موجود"));
        }
        if (!PartyStatuses.CanSet(current.AccountStatus, request.AccountStatus))
        {
            return Result.Fail<long>(new("PARTY_STATUS_TRANSITION_INVALID", "الأرشيف نهائي — لا تنشيط ولا تعليق بعده (§13)"));
        }
        var result = await _customers.SetCustomerStatusAsync(customerId, request.AccountStatus, request.ReasonAr ?? string.Empty, _user.UserId, ct);
        if (result.IsSuccess)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "sales.customers", EntityId: customerId, Action: "CUSTOMER_STATUS_CHANGE",
                Result: "SUCCESS", Reason: $"{request.AccountStatus} · {request.ReasonAr}", CorrelationId: correlationId), ct);
        }
        return result;
    }

    public async Task<Result<Contracts.V1.CustomerDto>> GetAsync(long customerId, CancellationToken ct = default)
    {
        var row = await _customers.FindCustomerAsync(customerId, ct);
        return row is null
            ? Result.Fail<Contracts.V1.CustomerDto>(new("CUSTOMER_NOT_FOUND", "العميل غير موجود"))
            : Result.Ok(ToDto(row));
    }

    /// <summary>كشف حساب العميل — في G6 تُبنى البنية وتعرض «لا حركات بعد» بصدق (فواتير G7 وسندات G8 تغذيها).</summary>
    public async Task<Result<Contracts.V1.PartyStatementResponse>> StatementAsync(
        long customerId, DateOnly? from, DateOnly? to, string? docType, CancellationToken ct = default)
    {
        var customer = await _customers.FindCustomerAsync(customerId, ct);
        if (customer is null)
        {
            return Result.Fail<Contracts.V1.PartyStatementResponse>(new("CUSTOMER_NOT_FOUND", "العميل غير موجود"));
        }
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var start = from ?? end.AddDays(-90);
        var s = await _customers.GetCustomerStatementAsync(customerId, start, end, docType?.Trim(), ct);
        return Result.Ok(new Contracts.V1.PartyStatementResponse(
            s.PartyId, s.PartyNameAr, s.From, s.To, s.Opening, s.Charges, s.Credits, s.Closing,
            [.. s.Entries.Select(e => new Contracts.V1.PartyStatementEntryDto(e.DocType, e.DocId, e.DocNumber, e.Date, e.Amount, e.Detail))]));
    }

    private static Contracts.V1.CustomerDto ToDto(CustomerRecord r) => new(
        r.CustomerId, r.Code, r.NameAr, r.Phone, r.Email, r.TaxId, r.CreditLimit, r.CreditEnabled,
        r.PaymentTermsDays, r.AccountStatus, r.Notes, r.CreatedAt);
}

/// <summary>
/// حلال سطر استلام/مرتجع (بوابة التحويل الموحدة): يطبق StockQuantityCalculator
/// من G4 على سطر مستند شراء — الأثر على صنف المحتوى عند PACK بلقطة العامل،
/// وتكلفة الوحدة الأساسية = unit_cost ÷ العامل (لـWAC).
/// </summary>
public sealed class PurchasingLineResolver
{
    private readonly ICatalogRepository _catalog;
    private readonly IUnitCatalog _units;

    public PurchasingLineResolver(ICatalogRepository catalog, IUnitCatalog units)
    {
        _catalog = catalog;
        _units = units;
    }

    public sealed record ResolvedLine(
        long LineSkuId, string SkuCode, long TargetSkuId,
        decimal Qty, long SourceUomId, string UomCode, decimal UnitCost,
        decimal ConversionFactor, decimal BaseQty, decimal BaseUnitCost, decimal LineTotal);

    public async Task<Result<ResolvedLine>> ResolveAsync(long skuId, decimal qty, long uomId, decimal unitCost, CancellationToken ct = default)
    {
        if (qty <= 0)
        {
            return Result.Fail<ResolvedLine>(new("LINE_QTY_INVALID", "كمية السطر يجب أن تكون موجبة"));
        }
        if (unitCost < 0)
        {
            return Result.Fail<ResolvedLine>(new("LINE_COST_INVALID", "تكلفة الوحدة لا تكون سالبة"));
        }
        var graph = (await _units.LoadGraphAsync(ct)).Value!;
        if (!graph.HasUnit(uomId))
        {
            return Result.Fail<ResolvedLine>(new("UNIT_UNKNOWN", "وحدة القياس غير معرفة في المخطط"));
        }
        var uomCode = graph.Unit(uomId).Code;

        var sku = await _catalog.FindSkuSaleInfoAsync(skuId, ct);
        if (sku is null)
        {
            return Result.Fail<ResolvedLine>(new("SKU_NOT_FOUND", "صيغة البيع غير موجودة"));
        }
        if (sku.SaleMode == SaleMode.Serialized)
        {
            return Result.Fail<ResolvedLine>(new(
                "SERIALIZED_RECEIVING_NOT_SUPPORTED",
                "استلام أصناف SERIALIZED يتطلب تسجيل السيريالات عند التفعيل — خارج نطاق G6 الصادق"));
        }

        PackageInfo? package = null;
        SkuSaleInfo? contentSku = null;
        if (sku.SaleMode == SaleMode.Pack)
        {
            package = await _catalog.FindActivePackageAsync(skuId, ct);
            if (package is null)
            {
                return Result.Fail<ResolvedLine>(new("PACKAGE_DEFINITION_REQUIRED", "صيغة PACK بلا تعريف عبوة ساري"));
            }
            contentSku = await _catalog.FindSkuSaleInfoAsync(package.ContentSkuId, ct);
            if (contentSku is null)
            {
                return Result.Fail<ResolvedLine>(new("PACKAGE_CONTENT_NOT_FOUND", "صنف محتوى العبوة غير موجود"));
            }
            if (contentSku.SaleMode == SaleMode.Weight)
            {
                return Result.Fail<ResolvedLine>(new(
                    "PACK_PREWEIGHED_NOT_SUPPORTED",
                    "عبوة من صنف وزني (موزونة مسبقاً) تُستلم بوزنها الثابت — غير مدعومة في G6 (رفض صريح لا تخمين)"));
            }
        }

        // وحدة المصدر: عند COUNT بوحدة غير أساسية يقبل المخطط؛ الوزني بوحدات كتلة فقط
        var sourceUom = package is null ? uomId : uomId;
        var snap = StockQuantityCalculator.Resolve(sku, qty, sourceUom, graph, package, contentSku);
        if (!snap.IsSuccess)
        {
            return Result.Fail<ResolvedLine>(snap.Error!);
        }
        var s = snap.Value!;

        var baseUnitCost = decimal.Round(unitCost / s.ConversionFactor, 4, MidpointRounding.AwayFromZero);
        return Result.Ok(new ResolvedLine(
            skuId, sku.SkuCode, s.SkuId,
            qty, uomId, uomCode, unitCost,
            s.ConversionFactor, s.BaseQty, baseUnitCost,
            decimal.Round(qty * unitCost, 4, MidpointRounding.AwayFromZero)));
    }
}

/// <summary>سلة المشتريات وأوامر الشراء (PAGE-24): مسودات قابلة للاستعادة — لا مخزون ولا مال قبل الاستلام والسداد.</summary>
public sealed class ManagePurchaseOrdersUseCase
{
    private readonly IPurchasingRepository _purchasing;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManagePurchaseOrdersUseCase> _logger;

    public ManagePurchaseOrdersUseCase(
        IPurchasingRepository purchasing, IAuditWriter audit, ICurrentUser user, ILogger<ManagePurchaseOrdersUseCase> logger)
    {
        _purchasing = purchasing;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.PurchaseOrderDto>> CreateAsync(Contracts.V1.CreatePurchaseOrderRequest request, string? correlationId, CancellationToken ct = default)
    {
        var supplier = await _purchasing.FindSupplierAsync(request.SupplierId, ct);
        if (supplier is null)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (supplier.AccountStatus != PartyStatuses.Active)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("SUPPLIER_NOT_ACTIVE", "المورد غير نشط — لا أوامر لمورد معطل/مؤرشف"));
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_LINES_REQUIRED", "سطر سلة واحد على الأقل"));
        }
        var seen = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (line.Qty <= 0 || line.UnitCost < 0)
            {
                return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_LINE_INVALID", "كمية السطر موجبة والتكلفة غير سالبة"));
            }
            if (!seen.Add(line.SkuId))
            {
                return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_LINE_DUPLICATED", $"الصيغة {line.SkuId} مكررة — سطر واحد لكل صنف"));
            }
        }

        Result<long> created;
        try
        {
            created = await _purchasing.CreatePurchaseOrderAsync(new PoDraft(
                request.SupplierId, request.WarehouseId, request.ExpectedDate,
                [.. request.Lines.Select(l => new PoLineDraft(l.SkuId, l.Qty, l.UomId, l.UnitCost))], _user.UserId), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(created.Error!);
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.purchase_orders", EntityId: created.Value, Action: "PO_CREATE", Result: "SUCCESS",
            Reason: $"{request.Lines.Count} سطراً للمورد {supplier.Code}", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindPurchaseOrderAsync(created.Value, ct))!));
    }

    /// <summary>استعادة المسودة (بوابة القبول): قراءة فقط — لا تكرر أي حركة (الحركة عند الترحيل مرة واحدة).</summary>
    public async Task<Result<Contracts.V1.PurchaseOrderDto>> GetAsync(long poId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindPurchaseOrderAsync(poId, ct);
        return detail is null
            ? Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_NOT_FOUND", "أمر الشراء غير موجود"))
            : Result.Ok(ToDto(detail));
    }

    public async Task<IReadOnlyList<Contracts.V1.PurchaseOrderDto>> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        var rows = await _purchasing.ListPurchaseOrdersAsync(status, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        var result = new List<Contracts.V1.PurchaseOrderDto>();
        foreach (var row in rows)
        {
            var detail = await _purchasing.FindPurchaseOrderAsync(row.PoId, ct);
            if (detail is not null)
            {
                result.Add(ToDto(detail));
            }
        }
        return result;
    }

    /// <summary>تعديل أسطر السلة — DRAFT فقط (المسودة الحية).</summary>
    public async Task<Result<Contracts.V1.PurchaseOrderDto>> ReplaceLinesAsync(long poId, Contracts.V1.ReplacePurchaseOrderLinesRequest request, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindPurchaseOrderAsync(poId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_NOT_FOUND", "أمر الشراء غير موجود"));
        }
        if (detail.Status != PurchaseOrderStates.Draft)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_NOT_DRAFT", "تعديل الأسطر في DRAFT فقط (سلة/مسودة حية)"));
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.PurchaseOrderDto>(new("PO_LINES_REQUIRED", "سطر واحد على الأقل"));
        }
        await _purchasing.ReplacePoLinesAsync(poId,
            [.. request.Lines.Select(l => new PoLineDraft(l.SkuId, l.Qty, l.UomId, l.UnitCost))], ct);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.purchase_orders", EntityId: poId, Action: "PO_LINES_REPLACE", Result: "SUCCESS",
            Reason: $"{request.Lines.Count} سطراً", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindPurchaseOrderAsync(poId, ct))!));
    }

    public async Task<Result<Contracts.V1.PoStatusChangeResponse>> SubmitAsync(long poId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindPurchaseOrderAsync(poId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_NOT_FOUND", "أمر الشراء غير موجود"));
        }
        if (!PurchaseOrderStates.CanSubmit(detail.Status))
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_NOT_SUBMITTABLE", "الإصدار من DRAFT فقط"));
        }
        if (detail.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_EMPTY", "لا إصدار لأمر بلا أسطر"));
        }
        var result = await _purchasing.SubmitPoAsync(poId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.purchase_orders", EntityId: poId, Action: "PO_SUBMIT", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PoStatusChangeResponse(poId, PurchaseOrderStates.Ordered));
    }

    public async Task<Result<Contracts.V1.PoStatusChangeResponse>> CloseAsync(long poId, string reasonAr, string? correlationId, CancellationToken ct = default)
    {
        if (reasonAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_CLOSE_REASON_REQUIRED", "إقفال الأمر قبل استلامه كاملاً يلزم سبباً موثقاً (≥ 3 محارف)"));
        }
        var detail = await _purchasing.FindPurchaseOrderAsync(poId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_NOT_FOUND", "أمر الشراء غير موجود"));
        }
        if (!PurchaseOrderStates.CanClose(detail.Status))
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_NOT_CLOSABLE", "الإقفال من ORDERED أو PARTIAL فقط"));
        }
        var result = await _purchasing.ClosePoAsync(poId, _user.UserId, reasonAr.Trim(), ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.purchase_orders", EntityId: poId, Action: "PO_CLOSE", Result: "SUCCESS",
            Reason: reasonAr, CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PoStatusChangeResponse(poId, PurchaseOrderStates.Closed));
    }

    public async Task<Result<Contracts.V1.PoStatusChangeResponse>> CancelAsync(long poId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindPurchaseOrderAsync(poId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new("PO_NOT_FOUND", "أمر الشراء غير موجود"));
        }
        if (!PurchaseOrderStates.CanCancel(detail.Status))
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(new(
                "PO_NOT_CANCELLABLE", "الإلغاء من DRAFT أو ORDERED فقط — بعد أي استلام يُقفل بقصور موثق"));
        }
        var result = await _purchasing.CancelPoAsync(poId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PoStatusChangeResponse>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.purchase_orders", EntityId: poId, Action: "PO_CANCEL", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PoStatusChangeResponse(poId, PurchaseOrderStates.Cancelled));
    }

    internal static Contracts.V1.PurchaseOrderDto ToDto(PoDetail d) => new(
        d.PoId, d.PoNumber, d.SupplierId, d.SupplierCode, d.SupplierNameAr, d.WarehouseId, d.WarehouseCode,
        d.Status, d.ExpectedDate, d.CreatedByName, d.CreatedAt,
        [.. d.Lines.Select(l => new Contracts.V1.PurchaseOrderLineDto(l.SkuId, l.SkuCode, l.SkuNameAr, l.Qty, l.UomId, l.UomCode, l.UnitCost))],
        d.Lines.Sum(l => decimal.Round(l.Qty * l.UnitCost, 4, MidpointRounding.AwayFromZero)));
}

/// <summary>استلامات الموردين (PAGE-25): الاستلام المعتمد هو الذي يزيد المخزون (§12.3 ق1).</summary>
public sealed partial class ManageReceiptsUseCase
{
    private readonly IPurchasingRepository _purchasing;
    private readonly PurchasingLineResolver _resolver;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageReceiptsUseCase> _logger;

    public ManageReceiptsUseCase(
        IPurchasingRepository purchasing, PurchasingLineResolver resolver,
        IAuditWriter audit, ICurrentUser user, ILogger<ManageReceiptsUseCase> logger)
    {
        _purchasing = purchasing;
        _resolver = resolver;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.ReceiptDto>> CreateAsync(Contracts.V1.CreateReceiptRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new("BRANCH_SCOPE_REQUIRED", "الاستلام يتطلب نطاق فرع"));
        }
        var supplier = await _purchasing.FindSupplierAsync(request.SupplierId, ct);
        if (supplier is null)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (supplier.AccountStatus != PartyStatuses.Active)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new("SUPPLIER_NOT_ACTIVE", "المورد غير نشط"));
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new("RECEIPT_LINES_REQUIRED", "سطر استلام واحد على الأقل"));
        }

        // لو مرتبط بأمر شراء: الأمر ORDERED/PARTIAL من نفس المورد وبنفس مخزن الأمر
        PoDetail? po = null;
        if (request.PoId is { } poId)
        {
            po = await _purchasing.FindPurchaseOrderAsync(poId, ct);
            if (po is null)
            {
                return Result.Fail<Contracts.V1.ReceiptDto>(new("PO_NOT_FOUND", "أمر الشراء غير موجود"));
            }
            if (po.SupplierId != request.SupplierId)
            {
                return Result.Fail<Contracts.V1.ReceiptDto>(new("PO_SUPPLIER_MISMATCH", "الأمر لمورد آخر — لا خلط مستندات"));
            }
            if (po.WarehouseId != request.WarehouseId)
            {
                return Result.Fail<Contracts.V1.ReceiptDto>(new("PO_WAREHOUSE_MISMATCH", "الاستلام في مخزن الأمر نفسه"));
            }
            if (po.Status is not (PurchaseOrderStates.Ordered or PurchaseOrderStates.Partial))
            {
                return Result.Fail<Contracts.V1.ReceiptDto>(new(
                    "PO_NOT_RECEIVABLE", "الأمر ليس في حالة ORDERED/PARTIAL — لا استلام على سلة أو أمر مقفل/ملغى"));
            }
        }

        var lines = new List<ReceiptLineDraft>();
        var seen = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (!seen.Add(line.SkuId))
            {
                return Result.Fail<Contracts.V1.ReceiptDto>(new("RECEIPT_LINE_DUPLICATED", $"الصيغة {line.SkuId} مكررة — سطر واحد لكل صنف"));
            }
            var resolved = await _resolver.ResolveAsync(line.SkuId, line.Qty, line.UomId, line.UnitCost, ct);
            if (!resolved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ReceiptDto>(resolved.Error!);
            }
            var r = resolved.Value!;
            lines.Add(new ReceiptLineDraft(
                line.SkuId, r.SkuCode, line.Qty, line.UomId, r.UomCode, line.UnitCost,
                line.LotCode?.Trim(), line.LotExpiry,
                r.TargetSkuId, r.ConversionFactor, r.BaseQty, r.BaseUnitCost, r.LineTotal));
        }

        Result<long> created;
        try
        {
            created = await _purchasing.CreateReceiptAsync(new ReceiptDraft(
                request.SupplierId, request.WarehouseId, request.PoId, request.Notes?.Trim(), lines, _user.UserId), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(created.Error!);
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "purch.purchase_receipts", EntityId: created.Value, Action: "RECEIPT_CREATE", Result: "SUCCESS",
            Reason: $"{lines.Count} سطراً من {supplier.Code}", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindReceiptAsync(created.Value, ct))!));
    }

    /// <summary>الترحيل (بوابة القبول: لا تكرار استلام) — ذري: حركات + دفعات + WAC + حالة الأمر + مفتاح Idempotency اختياري.</summary>
    public async Task<Result<Contracts.V1.PostReceiptResponse>> PostAsync(long receiptId, Contracts.V1.PostDocumentRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.PostReceiptResponse>(new("BRANCH_SCOPE_REQUIRED", "الترحيل يتطلب نطاق فرع"));
        }
        var detail = await _purchasing.FindReceiptAsync(receiptId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PostReceiptResponse>(new("RECEIPT_NOT_FOUND", "الاستلام غير موجود"));
        }
        if (!ReceiptStates.CanPost(detail.Status))
        {
            return Result.Fail<Contracts.V1.PostReceiptResponse>(new(
                "RECEIPT_ALREADY_POSTED", "الاستلام مرحّل بالفعل — لا تكرار استلام (الحركة Append-only وترحيل ثانٍ مرفوض)"));
        }

        var movements = new List<ReceiptMovementPlan>();
        foreach (var line in detail.Lines)
        {
            var resolved = await _resolver.ResolveAsync(line.SkuId, line.Qty, line.UomId, line.UnitCost, ct);
            if (!resolved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostReceiptResponse>(resolved.Error!);
            }
            var r = resolved.Value!;
            movements.Add(new ReceiptMovementPlan(
                line.LineId, line.SkuId, r.SkuCode, r.TargetSkuId,
                r.BaseQty, line.UomId, r.ConversionFactor, r.BaseUnitCost,
                line.LotCode, line.LotExpiry, r.LineTotal));
        }

        PostedReceipt posted;
        try
        {
            var result = await _purchasing.PostReceiptAsync(new ReceiptPostPlan(
                receiptId, detail.PoId, request.IdempotencyKey?.Trim(), branchId, _user.UserId, movements), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostReceiptResponse>(result.Error!);
            }
            posted = result.Value!;
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.PostReceiptResponse>(new(ex.Code, ex.UserMessageAr));
        }

        LogPosted(receiptId, posted.MovementIds.Length, posted.TotalBaseQty, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "purch.purchase_receipts", EntityId: receiptId, Action: "RECEIPT_POST", Result: "SUCCESS",
            Reason: $"حركات={posted.MovementIds.Length} كمية قاعدية={posted.TotalBaseQty:0.######}",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PostReceiptResponse(
            posted.ReceiptId, posted.ReceiptNumber, posted.MovementIds,
            posted.LineCount, posted.TotalBaseQty, posted.TotalCost));
    }

    public async Task<Result<Contracts.V1.ReceiptDto>> CancelAsync(long receiptId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindReceiptAsync(receiptId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new("RECEIPT_NOT_FOUND", "الاستلام غير موجود"));
        }
        if (!ReceiptStates.CanCancel(detail.Status))
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(new(
                "RECEIPT_NOT_CANCELLABLE", "الإلغاء من DRAFT فقط — POSTED وقع في الدفتر وتصحيحه بمرتجع موثق"));
        }
        var result = await _purchasing.CancelReceiptAsync(receiptId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.ReceiptDto>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.purchase_receipts", EntityId: receiptId, Action: "RECEIPT_CANCEL", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindReceiptAsync(receiptId, ct))!));
    }

    public async Task<Result<Contracts.V1.ReceiptDto>> GetAsync(long receiptId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindReceiptAsync(receiptId, ct);
        return detail is null
            ? Result.Fail<Contracts.V1.ReceiptDto>(new("RECEIPT_NOT_FOUND", "الاستلام غير موجود"))
            : Result.Ok(ToDto(detail));
    }

    public async Task<IReadOnlyList<Contracts.V1.ReceiptDto>> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        var rows = await _purchasing.ListReceiptsAsync(status, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        var result = new List<Contracts.V1.ReceiptDto>();
        foreach (var row in rows)
        {
            var detail = await _purchasing.FindReceiptAsync(row.ReceiptId, ct);
            if (detail is not null)
            {
                result.Add(ToDto(detail));
            }
        }
        return result;
    }

    internal static Contracts.V1.ReceiptDto ToDto(ReceiptDetail d) => new(
        d.ReceiptId, d.ReceiptNumber, d.SupplierId, d.SupplierCode, d.SupplierNameAr,
        d.PoId, d.PoNumber, d.WarehouseId, d.WarehouseCode, d.Status, d.Notes,
        d.ReceivedByName, d.CreatedAt, d.PostedAt,
        [.. d.Lines.Select(l => new Contracts.V1.ReceiptLineDto(
            l.LineId, l.SkuId, l.SkuCode, l.SkuNameAr, l.Qty, l.UomId, l.UomCode,
            l.ConversionFactor, l.UnitCost, l.LotId, l.LotCode, l.LotExpiry, l.BaseQty, l.LineTotal))]);

    [LoggerMessage(Level = LogLevel.Information, Message = "ترحيل استلام {ReceiptId}: {Movements} حركة بإجمالي قاعدي {BaseQty} — correlation={CorrelationId}")]
    private partial void LogPosted(long receiptId, int movements, decimal baseQty, string? correlationId);
}

/// <summary>فواتير الموردين (PAGE-26): التزام مالي — لا حركة مخزون (§12.3 ق1).</summary>
public sealed class ManageSupplierInvoicesUseCase
{
    private readonly IPurchasingRepository _purchasing;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageSupplierInvoicesUseCase(IPurchasingRepository purchasing, IAuditWriter audit, ICurrentUser user)
    {
        _purchasing = purchasing;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<Contracts.V1.SupplierInvoiceDto>> CreateAsync(Contracts.V1.CreateSupplierInvoiceRequest request, string? correlationId, CancellationToken ct = default)
    {
        var supplier = await _purchasing.FindSupplierAsync(request.SupplierId, ct);
        if (supplier is null)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (supplier.AccountStatus != PartyStatuses.Active)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("SUPPLIER_NOT_ACTIVE", "المورد غير نشط"));
        }
        if (string.IsNullOrWhiteSpace(request.InvoiceNumber) || request.InvoiceNumber.Trim().Length < 2)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("INVOICE_NUMBER_REQUIRED", "رقم فاتورة المورد مطلوب (كما هو على فاتورتهم)"));
        }
        if (request.Subtotal < 0 || request.TaxTotal < 0)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("INVOICE_AMOUNTS_INVALID", "القيم لا تكون سالبة"));
        }

        ReceiptDetail? receipt = null;
        if (request.ReceiptId is { } receiptId)
        {
            receipt = await _purchasing.FindReceiptAsync(receiptId, ct);
            if (receipt is null)
            {
                return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("RECEIPT_NOT_FOUND", "الاستلام غير موجود"));
            }
            if (receipt.Status != ReceiptStates.Posted)
            {
                return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new(
                    "RECEIPT_NOT_POSTED", "الفاتورة تربط باستلام مرحّل — الاستلام المبدئي (DRAFT) لا يفتح التزاماً مالياً"));
            }
            if (receipt.SupplierId != request.SupplierId)
            {
                return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("RECEIPT_SUPPLIER_MISMATCH", "الاستلام لمورد آخر"));
            }
        }

        Result<long> created;
        try
        {
            created = await _purchasing.CreateInvoiceAsync(new InvoiceDraft(
                request.SupplierId, request.ReceiptId, request.InvoiceNumber.Trim(), request.InvoiceDate, request.DueDate,
                request.Subtotal, request.TaxTotal,
                [.. request.Lines.Select(l => new InvoiceLineDraft(l.SkuId, l.Qty, l.UomId, l.UnitCost))], _user.UserId), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(created.Error!);
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_invoices", EntityId: created.Value, Action: "INVOICE_CREATE", Result: "SUCCESS",
            Reason: $"رقم المورد {request.InvoiceNumber}", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindInvoiceAsync(created.Value, ct))!));
    }

    /// <summary>ترحيل الفاتورة: التزام مالي فقط — الإيصال المادي سبق في الاستلام (لا ازدواج §12.3 ق1).</summary>
    public async Task<Result<Contracts.V1.PostInvoiceResponse>> PostAsync(long invoiceId, Contracts.V1.PostDocumentRequest request, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindInvoiceAsync(invoiceId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PostInvoiceResponse>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"));
        }
        if (!InvoiceStates.CanPost(detail.Status))
        {
            return Result.Fail<Contracts.V1.PostInvoiceResponse>(new("INVOICE_ALREADY_POSTED", "الفاتورة مرحّلة بالفعل — لا تكرار التزام"));
        }
        var result = await _purchasing.PostInvoiceAsync(invoiceId, request.IdempotencyKey?.Trim(), _user.UserId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PostInvoiceResponse>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_invoices", EntityId: invoiceId, Action: "INVOICE_POST", Result: "SUCCESS",
            Reason: $"التزام {result.Value!.GrandTotal:0.####}", CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PostInvoiceResponse(result.Value!.InvoiceId, result.Value!.NewStatus, result.Value!.GrandTotal));
    }

    public async Task<Result<Contracts.V1.SupplierInvoiceDto>> CancelAsync(long invoiceId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindInvoiceAsync(invoiceId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"));
        }
        if (!InvoiceStates.CanCancel(detail.Status))
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(new(
                "INVOICE_NOT_CANCELLABLE", "الإلغاء من DRAFT فقط — التزام مرحّل يُعالج بمرتجع/إشعار دائن لا بالحذف"));
        }
        var result = await _purchasing.CancelInvoiceAsync(invoiceId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierInvoiceDto>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_invoices", EntityId: invoiceId, Action: "INVOICE_CANCEL", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindInvoiceAsync(invoiceId, ct))!));
    }

    public async Task<Result<Contracts.V1.SupplierInvoiceDto>> GetAsync(long invoiceId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindInvoiceAsync(invoiceId, ct);
        return detail is null
            ? Result.Fail<Contracts.V1.SupplierInvoiceDto>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"))
            : Result.Ok(ToDto(detail));
    }

    public async Task<IReadOnlyList<Contracts.V1.SupplierInvoiceDto>> ListAsync(string? status, long? supplierId, int? page, int? pageSize, CancellationToken ct = default)
    {
        var rows = await _purchasing.ListInvoicesAsync(status, supplierId, page ?? 1, Math.Clamp(pageSize ?? 25, 1, 200), ct);
        var result = new List<Contracts.V1.SupplierInvoiceDto>();
        foreach (var row in rows)
        {
            var detail = await _purchasing.FindInvoiceAsync(row.InvoiceId, ct);
            if (detail is not null)
            {
                result.Add(ToDto(detail));
            }
        }
        return result;
    }

    internal static Contracts.V1.SupplierInvoiceDto ToDto(InvoiceDetail d) => new(
        d.InvoiceId, d.SupplierId, d.SupplierCode, d.SupplierNameAr,
        d.ReceiptId, d.ReceiptNumber, d.InvoiceNumber, d.InvoiceDate, d.DueDate,
        d.Subtotal, d.TaxTotal, d.GrandTotal, d.PaidAmount, d.RemainingAmount, d.Status, d.PostedAt,
        [.. d.Lines.Select(l => new Contracts.V1.SupplierInvoiceLineDto(l.SkuId, l.SkuCode, l.Qty, l.UomId, l.UomCode, l.UnitCost, l.LineTotal))]);
}

/// <summary>سداد الموردين (PAGE-26): أثر خزينة واحد وتوزيع على الفواتير ≤ المتبقي (§12.3 ق3 · §13).</summary>
public sealed partial class ManageSupplierPaymentsUseCase
{
    private readonly IPurchasingRepository _purchasing;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageSupplierPaymentsUseCase> _logger;

    public ManageSupplierPaymentsUseCase(
        IPurchasingRepository purchasing, IAuditWriter audit, ICurrentUser user, ILogger<ManageSupplierPaymentsUseCase> logger)
    {
        _purchasing = purchasing;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
        { "CASH", "BANK_TRANSFER", "CHEQUE", "CARD" };

    public async Task<Result<Contracts.V1.SupplierPaymentDto>> CreateAsync(Contracts.V1.CreateSupplierPaymentRequest request, string? correlationId, CancellationToken ct = default)
    {
        var supplier = await _purchasing.FindSupplierAsync(request.SupplierId, ct);
        if (supplier is null)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (supplier.AccountStatus != PartyStatuses.Active)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("SUPPLIER_NOT_ACTIVE", "المورد غير نشط"));
        }
        if (request.Amount <= 0)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("PAYMENT_AMOUNT_INVALID", "قيمة السداد موجبة"));
        }
        if (!Methods.Contains(request.PaymentMethod))
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("PAYMENT_METHOD_INVALID", "وسيلة السداد CASH أو BANK_TRANSFER أو CHEQUE أو CARD"));
        }
        var treasury = (await _purchasing.ListTreasuryAccountsAsync(activeOnly: true, ct))
            .FirstOrDefault(t => t.TreasuryAccountId == request.TreasuryAccountId);
        if (treasury is null)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new(
                "TREASURY_ACCOUNT_NOT_FOUND",
                "حساب الخزينة غير موجود أو غير نشط — إنشاء حسابات الخزينة وإدارتها في G8 (قراءة فقط في G6)"));
        }

        var allocations = new List<PaymentAllocationDraft>();
        var allocatedTotal = 0m;
        var seenInvoices = new HashSet<long>();
        foreach (var alloc in request.Allocations)
        {
            if (alloc.Amount <= 0)
            {
                return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("ALLOCATION_AMOUNT_INVALID", "قيمة التخصيص موجبة"));
            }
            if (!seenInvoices.Add(alloc.InvoiceId))
            {
                return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("ALLOCATION_DUPLICATED", $"الفاتورة {alloc.InvoiceId} مخصصة مرتين في نفس السند"));
            }
            var invoice = await _purchasing.FindInvoiceAsync(alloc.InvoiceId, ct);
            if (invoice is null)
            {
                return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("INVOICE_NOT_FOUND", $"الفاتورة {alloc.InvoiceId} غير موجودة"));
            }
            if (invoice.SupplierId != request.SupplierId)
            {
                return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("INVOICE_SUPPLIER_MISMATCH", "تخصيص سداد لفاتورة مورد آخر"));
            }
            if (invoice.Status is not (InvoiceStates.Posted or InvoiceStates.PartiallyPaid))
            {
                return Result.Fail<Contracts.V1.SupplierPaymentDto>(new(
                    "INVOICE_NOT_PAYABLE", $"الفاتورة {invoice.InvoiceNumber} ليست في حالة قابلة للسداد ({invoice.Status})"));
            }
            // منع التخصيص بأكثر من المتبقي — يُعاد فحصه في معاملة المستودع (حارس V017)
            try
            {
                PaymentAllocationRules.ValidateAgainstInvoice(invoice.GrandTotal, invoice.PaidAmount, alloc.Amount);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Fail<Contracts.V1.SupplierPaymentDto>(new(
                    ex.Message.Contains("INVOICE_OVER_PAYMENT", StringComparison.Ordinal) ? "INVOICE_OVER_PAYMENT" : "PAYMENT_ALLOCATION_INVALID",
                    "المخصص على الفاتورة يتجاوز قيمتها — لا تخصيص بأكثر من المتبقي (§13)"));
            }
            allocations.Add(new PaymentAllocationDraft(alloc.InvoiceId, alloc.Amount));
            allocatedTotal += alloc.Amount;
        }
        // Σ التخصيصات ≤ قيمة السداد — حارس V004 + فحص هنا
        try
        {
            PaymentAllocationRules.ValidateAgainstPayment(request.Amount, allocatedTotal);
        }
        catch (InvalidOperationException)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("PAYMENT_OVER_ALLOCATION", "مجموع التخصيصات يتجاوز قيمة السداد (§13)"));
        }

        Result<long> created;
        try
        {
            created = await _purchasing.CreatePaymentAsync(new PaymentDraft(
                request.SupplierId, request.TreasuryAccountId, request.Amount, request.PaymentMethod,
                request.PaymentDate, request.Notes?.Trim(), allocations, _user.UserId), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(created.Error!);
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_payments", EntityId: created.Value, Action: "PAYMENT_CREATE", Result: "SUCCESS",
            Reason: $"{request.Amount:0.####} على {treasury.Code}", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindPaymentAsync(created.Value, ct))!));
    }

    /// <summary>الترحيل (بوابة القبول: لا تكرار دفع): أثر خزينة واحد مهما وزّع — لا يُسجل مصروفاً.</summary>
    public async Task<Result<Contracts.V1.PostPaymentResponse>> PostAsync(long paymentId, Contracts.V1.PostDocumentRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.PostPaymentResponse>(new("BRANCH_SCOPE_REQUIRED", "ترحيل السداد يتطلب نطاق فرع"));
        }
        var detail = await _purchasing.FindPaymentAsync(paymentId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PostPaymentResponse>(new("PAYMENT_NOT_FOUND", "السداد غير موجود"));
        }
        if (!PaymentStates.CanPost(detail.Status))
        {
            return Result.Fail<Contracts.V1.PostPaymentResponse>(new("PAYMENT_ALREADY_POSTED", "السداد مرحّل بالفعل — لا تكرار دفع"));
        }
        var result = await _purchasing.PostPaymentAsync(new PaymentPostPlan(
            paymentId, request.IdempotencyKey?.Trim(), branchId, _user.UserId), ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PostPaymentResponse>(result.Error!);
        }
        var posted = result.Value!;

        LogPosted(paymentId, posted.TreasuryTxnId, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "purch.supplier_payments", EntityId: paymentId, Action: "PAYMENT_POST", Result: "SUCCESS",
            Reason: $"أثر خزينة={posted.TreasuryTxnId} فواتير متأثرة={posted.AffectedInvoiceIds.Length}",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PostPaymentResponse(
            posted.PaymentId, posted.VoucherNumber, posted.TreasuryTxnId, posted.AffectedInvoiceIds));
    }

    public async Task<Result<Contracts.V1.SupplierPaymentDto>> CancelAsync(long paymentId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindPaymentAsync(paymentId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new("PAYMENT_NOT_FOUND", "السداد غير موجود"));
        }
        if (!PaymentStates.CanCancel(detail.Status))
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(new(
                "PAYMENT_NOT_CANCELLABLE", "الإلغاء من DRAFT فقط — المرحّل أثر في الخزينة وتصحيحه بسند عكسي موثق"));
        }
        var result = await _purchasing.CancelPaymentAsync(paymentId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierPaymentDto>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_payments", EntityId: paymentId, Action: "PAYMENT_CANCEL", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindPaymentAsync(paymentId, ct))!));
    }

    public async Task<IReadOnlyList<Contracts.V1.SupplierPaymentDto>> ListAsync(long? supplierId, string? status, CancellationToken ct = default)
    {
        var rows = await _purchasing.ListPaymentsAsync(supplierId, status, ct);
        var result = new List<Contracts.V1.SupplierPaymentDto>();
        foreach (var row in rows)
        {
            var detail = await _purchasing.FindPaymentAsync(row.PaymentId, ct);
            if (detail is not null)
            {
                result.Add(ToDto(detail));
            }
        }
        return result;
    }

    public async Task<Contracts.V1.TreasuryAccountListResponse> ListTreasuryAsync(CancellationToken ct = default)
    {
        var rows = await _purchasing.ListTreasuryAccountsAsync(activeOnly: false, ct);
        return new Contracts.V1.TreasuryAccountListResponse(
            [.. rows.Select(r => new Contracts.V1.TreasuryAccountDto(
                r.TreasuryAccountId, r.Code, r.NameAr, r.AccountKind, r.BranchId, r.Currency, r.IsActive))]);
    }

    internal static Contracts.V1.SupplierPaymentDto ToDto(PaymentDetail d) => new(
        d.PaymentId, d.VoucherNumber, d.SupplierId, d.SupplierCode, d.SupplierNameAr,
        d.TreasuryAccountId, d.TreasuryAccountCode, d.Amount, d.PaymentMethod, d.PaymentDate,
        d.Status, d.Notes, d.PostedAt,
        [.. d.Allocations.Select(a => new Contracts.V1.PaymentAllocationDto(a.InvoiceId, a.InvoiceNumber, a.Amount))]);

    [LoggerMessage(Level = LogLevel.Information, Message = "ترحيل سداد {PaymentId}: أثر خزينة {TxnId} واحد — correlation={CorrelationId}")]
    private partial void LogPosted(long paymentId, long txnId, string? correlationId);
}

/// <summary>مرتجعات الموردين (PAGE-27): مرتبطة بالاستلام الأصلي — لا رد فوق ما استُلم.</summary>
public sealed partial class ManageSupplierReturnsUseCase
{
    private readonly IPurchasingRepository _purchasing;
    private readonly PurchasingLineResolver _resolver;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<ManageSupplierReturnsUseCase> _logger;

    public ManageSupplierReturnsUseCase(
        IPurchasingRepository purchasing, PurchasingLineResolver resolver,
        IAuditWriter audit, ICurrentUser user, ILogger<ManageSupplierReturnsUseCase> logger)
    {
        _purchasing = purchasing;
        _resolver = resolver;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.SupplierReturnDto>> CreateAsync(Contracts.V1.CreateSupplierReturnRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!RefundMethods.All.Contains(request.RefundMethod))
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("REFUND_METHOD_INVALID", "طريقة الرد CASH أو CREDIT_NOTE أو BANK_TRANSFER"));
        }
        var supplier = await _purchasing.FindSupplierAsync(request.SupplierId, ct);
        if (supplier is null)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (request.ReceiptId is not { } receiptId)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new(
                "RETURN_ORIGIN_REQUIRED", "المرتجع يرتبط باستلام أصلي مرحّل (receiptId) — هو مصدر دخول المخزون (§12.3 ق4)"));
        }
        var receipt = await _purchasing.FindReceiptAsync(receiptId, ct);
        if (receipt is null)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RECEIPT_NOT_FOUND", "الاستلام الأصلي غير موجود"));
        }
        if (receipt.Status != ReceiptStates.Posted)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RECEIPT_NOT_POSTED", "المرتجع يرتبط باستلام مرحّل فقط"));
        }
        if (receipt.SupplierId != request.SupplierId)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RECEIPT_SUPPLIER_MISMATCH", "الاستلام الأصلي لمورد آخر"));
        }
        if (receipt.WarehouseId != request.WarehouseId)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RECEIPT_WAREHOUSE_MISMATCH", "الرد من مخزن الاستلام نفسه"));
        }
        if (request.InvoiceId is { } invId)
        {
            var invoice = await _purchasing.FindInvoiceAsync(invId, ct);
            if (invoice is null || invoice.SupplierId != request.SupplierId)
            {
                return Result.Fail<Contracts.V1.SupplierReturnDto>(new("INVOICE_SUPPLIER_MISMATCH", "الفاتورة المرجعية لمورد آخر"));
            }
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RETURN_LINES_REQUIRED", "سطر مرتجع واحد على الأقل"));
        }

        var lines = new List<ReturnLineDraft>();
        var seen = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (!seen.Add(line.SkuId))
            {
                return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RETURN_LINE_DUPLICATED", $"الصيغة {line.SkuId} مكررة — سطر واحد لكل صنف"));
            }
            var resolved = await _resolver.ResolveAsync(line.SkuId, line.Qty, line.UomId, line.UnitCost, ct);
            if (!resolved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.SupplierReturnDto>(resolved.Error!);
            }
            var r = resolved.Value!;
            // فحص مبكر ضد الأصل: لا رد فوق ما استُلم منه لهذا الصنف (الفحص الحاسم يعاد عند الترحيل)
            var receivedBase = receipt.Lines.Where(l => l.SkuId == line.SkuId).Sum(l => l.BaseQty);
            if (r.BaseQty > receivedBase + 0.000001m)
            {
                return Result.Fail<Contracts.V1.SupplierReturnDto>(new(
                    "RETURN_EXCEEDS_RECEIVED",
                    $"الرد على {r.SkuCode} ({r.BaseQty:0.######}) يتجاوز ما استُلم من الأصل لهذا الصنف ({receivedBase:0.######}) — §12.3 ق4"));
            }
            lines.Add(new ReturnLineDraft(
                line.SkuId, r.SkuCode, line.Qty, line.UomId, r.UomCode, line.UnitCost,
                r.TargetSkuId, r.ConversionFactor, r.BaseQty, r.LineTotal));
        }

        Result<long> created;
        try
        {
            created = await _purchasing.CreateReturnAsync(new ReturnDraft(
                request.SupplierId, receiptId, request.InvoiceId, request.WarehouseId, request.RefundMethod,
                lines, _user.UserId), ct);
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new(ex.Code, ex.UserMessageAr));
        }
        if (!created.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(created.Error!);
        }

        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_returns", EntityId: created.Value, Action: "RETURN_CREATE", Result: "SUCCESS",
            Reason: $"رد {request.RefundMethod} على {receipt.ReceiptNumber}", CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindReturnAsync(created.Value, ct))!));
    }

    /// <summary>الترحيل: حركة SUPPLIER_RETURN لكل سطر بحد ما استُلم من الأصل + أثر خزينة إن كان رداً نقدياً.</summary>
    public async Task<Result<Contracts.V1.PostReturnResponse>> PostAsync(long returnId, Contracts.V1.PostDocumentRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (_user.BranchId is not { } branchId)
        {
            return Result.Fail<Contracts.V1.PostReturnResponse>(new("BRANCH_SCOPE_REQUIRED", "الترحيل يتطلب نطاق فرع"));
        }
        var detail = await _purchasing.FindReturnAsync(returnId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PostReturnResponse>(new("RETURN_NOT_FOUND", "المرتجع غير موجود"));
        }
        if (!ReturnStates.CanPost(detail.Status))
        {
            return Result.Fail<Contracts.V1.PostReturnResponse>(new("RETURN_ALREADY_POSTED", "المرتجع مرحّل بالفعل — لا تكرار"));
        }

        var movements = new List<ReturnMovementPlan>();
        foreach (var line in detail.Lines)
        {
            var resolved = await _resolver.ResolveAsync(line.SkuId, line.Qty, line.UomId, line.UnitCost, ct);
            if (!resolved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostReturnResponse>(resolved.Error!);
            }
            var r = resolved.Value!;
            movements.Add(new ReturnMovementPlan(
                line.LineId, line.SkuId, r.SkuCode, r.TargetSkuId,
                r.BaseQty, line.UomId, r.ConversionFactor, r.LineTotal));
        }

        var isCashRefund = RefundMethods.IsCashRefund(detail.RefundMethod);
        PostedReturn posted;
        try
        {
            var result = await _purchasing.PostReturnAsync(new ReturnPostPlan(
                returnId, request.IdempotencyKey?.Trim(), branchId, _user.UserId,
                isCashRefund, detail.Total, movements), ct);
            if (!result.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostReturnResponse>(result.Error!);
            }
            posted = result.Value!;
        }
        catch (PurchasingPolicyException ex)
        {
            return Result.Fail<Contracts.V1.PostReturnResponse>(new(ex.Code, ex.UserMessageAr));
        }

        LogPosted(returnId, posted.MovementIds.Length, posted.TreasuryTxnId, correlationId);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: branchId,
            EntitySchema: "purch.supplier_returns", EntityId: returnId, Action: "RETURN_POST", Result: "SUCCESS",
            Reason: $"رد {detail.RefundMethod} · أثر خزينة={(posted.TreasuryTxnId is null ? "لا" : posted.TreasuryTxnId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
            CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.PostReturnResponse(
            posted.ReturnId, posted.MovementIds, posted.TreasuryTxnId, posted.ReducesPayable));
    }

    public async Task<Result<Contracts.V1.SupplierReturnDto>> CancelAsync(long returnId, string? correlationId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindReturnAsync(returnId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new("RETURN_NOT_FOUND", "المرتجع غير موجود"));
        }
        if (!ReturnStates.CanCancel(detail.Status))
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(new(
                "RETURN_NOT_CANCELLABLE", "الإلغاء من DRAFT فقط — المرحّل وقع في الدفتر والخزينة"));
        }
        var result = await _purchasing.CancelReturnAsync(returnId, ct);
        if (!result.IsSuccess)
        {
            return Result.Fail<Contracts.V1.SupplierReturnDto>(result.Error!);
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "purch.supplier_returns", EntityId: returnId, Action: "RETURN_CANCEL", Result: "SUCCESS",
            CorrelationId: correlationId), ct);
        return Result.Ok(ToDto((await _purchasing.FindReturnAsync(returnId, ct))!));
    }

    public async Task<IReadOnlyList<Contracts.V1.SupplierReturnDto>> ListAsync(string? status, long? supplierId, CancellationToken ct = default)
    {
        var rows = await _purchasing.ListReturnsAsync(status, supplierId, ct);
        var result = new List<Contracts.V1.SupplierReturnDto>();
        foreach (var row in rows)
        {
            var detail = await _purchasing.FindReturnAsync(row.ReturnId, ct);
            if (detail is not null)
            {
                result.Add(ToDto(detail));
            }
        }
        return result;
    }

    public async Task<Result<Contracts.V1.SupplierReturnDto>> GetAsync(long returnId, CancellationToken ct = default)
    {
        var detail = await _purchasing.FindReturnAsync(returnId, ct);
        return detail is null
            ? Result.Fail<Contracts.V1.SupplierReturnDto>(new("RETURN_NOT_FOUND", "المرتجع غير موجود"))
            : Result.Ok(ToDto(detail));
    }

    internal static Contracts.V1.SupplierReturnDto ToDto(ReturnDetail d) => new(
        d.ReturnId, d.ReturnNumber, d.SupplierId, d.SupplierCode, d.SupplierNameAr,
        d.ReceiptId, d.ReceiptNumber, d.InvoiceId, d.InvoiceNumber,
        d.WarehouseId, d.WarehouseCode, d.Status, d.RefundMethod, d.Total,
        d.CreatedByName, d.CreatedAt, d.PostedAt,
        [.. d.Lines.Select(l => new Contracts.V1.SupplierReturnLineDto(
            l.SkuId, l.SkuCode, l.Qty, l.UomId, l.UomCode, l.ConversionFactor, l.UnitCost, l.LineTotal))]);

    [LoggerMessage(Level = LogLevel.Information, Message = "ترحيل مرتجع {ReturnId}: {Movements} حركة وأثر خزينة={TxnId} — correlation={CorrelationId}")]
    private partial void LogPosted(long returnId, int movements, long? txnId, string? correlationId);
}
