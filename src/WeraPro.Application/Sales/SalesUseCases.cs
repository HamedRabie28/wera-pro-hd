using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Catalog;
using WeraPro.Domain.Catalog;
using WeraPro.Domain.Sales;
using WeraPro.Domain.SharedKernel;
using WeraPro.Domain.Weighing;

namespace WeraPro.Application.Sales;

// ═══════════════ إعدادات النشاط كما يقرؤها POS (لا ثوابت كود — §2.5) ═══════════

public sealed record PosSettings(
    string TaxMode,
    decimal TaxRate,
    WeighingSettings Weighing,
    WeraPro.Contracts.V1.DiscountLimitsDto DiscountLimits,
    string NegativeStockPolicy,
    string CardProvider,
    string ReturnsCashForCard);

public sealed class PosSettingsReader(ISalesRepository sales)
{
    public async Task<PosSettings> ReadAsync(CancellationToken ct = default)
    {
        var taxMode = await sales.GetSettingAsync("tax.policy_mode", ct) ?? SalesTaxMode.VatInclusive;
        var taxRate = decimal.TryParse(await sales.GetSettingAsync("tax.standard_rate", ct) ?? "0.1400",
            System.Globalization.CultureInfo.InvariantCulture, out var rate) ? rate : 0.1400m;
        var precision = int.TryParse(await sales.GetSettingAsync("weighing.precision_g", ct) ?? "1", out var p) ? p : 1;
        var step = int.TryParse(await sales.GetSettingAsync("weighing.step_g", ct) ?? "5", out var s) ? s : 5;
        var weighing = new WeighingSettings(
            PrecisionGrams: precision, StepGrams: step,
            AmountRounding: AmountRounding.BankersToCent, WeightRounding: WeightRounding.NearestStep);
        var limitsRaw = await sales.GetSettingAsync("pricing.discount_limits", ct)
            ?? """{"cashier_pct":5,"supervisor_pct":15,"over_requires":"MANAGER"}""";
        decimal cashierPct = 5m, supervisorPct = 15m;
        string overRequires = "MANAGER";
        try
        {
            using var doc = JsonDocument.Parse(limitsRaw);
            if (doc.RootElement.TryGetProperty("cashier_pct", out var c) && c.TryGetDecimal(out var cv)) cashierPct = cv;
            if (doc.RootElement.TryGetProperty("supervisor_pct", out var s2) && s2.TryGetDecimal(out var sv)) supervisorPct = sv;
            if (doc.RootElement.TryGetProperty("over_requires", out var o) && o.GetString() is { } ov) overRequires = ov;
        }
        catch (JsonException)
        {
            // إعداد تالف: تبقى التوصية المعتمدة (D-06) — لا تخمين أبعد منها
        }
        return new PosSettings(
            TaxMode: taxMode, TaxRate: taxRate, Weighing: weighing,
            DiscountLimits: new WeraPro.Contracts.V1.DiscountLimitsDto(cashierPct, supervisorPct, overRequires),
            NegativeStockPolicy: await sales.GetSettingAsync("inventory.negative_stock_policy", ct) ?? "SUPERVISOR_APPROVAL",
            CardProvider: await sales.GetSettingAsync("payments.card_provider", ct) ?? "NOT_CONFIGURED",
            ReturnsCashForCard: await sales.GetSettingAsync("returns.cash_for_card_allowed", ct) ?? "false");
    }
}

// ═══════════════ حلال سطر البيع (بوابة G4 الموحدة — نمط PurchasingLineResolver) ═

public sealed class SaleLineResolver
{
    public sealed record ResolvedLine(
        long SkuId, string SkuCode, long ProductId, string ProductNameAr, string SaleMode,
        long? PackageId, decimal Qty, long UomId, string UomCode,
        decimal ConversionFactor, decimal BaseQty, long MovementSkuId,
        decimal? WeightKg, string? WeightSource, decimal? AmountTarget, decimal? TareKg,
        decimal UnitPrice, long? PriceListId, string TaxTreatment, decimal TaxRate,
        decimal LineDiscount, decimal CostSnapshot, decimal ContentCostAvg, string? Notes,
        SkuWeightPolicy? WeightPolicy, bool AllowNegative);

    private readonly ISalesRepository _sales;
    private readonly IUnitCatalog _units;

    public SaleLineResolver(ISalesRepository sales, IUnitCatalog units)
    {
        _sales = sales;
        _units = units;
    }

    internal static SkuWeightPolicy BuildWeightPolicy(SaleItemData d, in WeighingSettings settings) => new(
        MinWeightKg: d.MinWeightKg,
        MaxWeightKg: d.MaxWeightKg,
        StepGrams: d.WeightStepG ?? settings.StepGrams,
        PrecisionGrams: d.WeightPrecisionG ?? settings.PrecisionGrams,
        AllowManualWeight: d.AllowManualWeight ?? true,
        AllowAmountTarget: d.AllowAmountTarget ?? true,
        DefaultTareKg: d.DefaultTareKg);

    internal async Task<Result<ResolvedLine>> ResolveAsync(
        Contracts.V1.PosLineRequest line, long warehouseId, PosSettings settings, CancellationToken ct = default)
    {
        if (line.Qty <= 0 && line.WeightKg is null && line.AmountTarget is null)
        {
            return Result.Fail<ResolvedLine>(new("LINE_QTY_INVALID", "كمية السطر يجب أن تكون موجبة"));
        }
        var data = await _sales.GetSaleItemDataAsync(line.SkuId, warehouseId, null, ct);
        if (data is null)
        {
            return Result.Fail<ResolvedLine>(new("SKU_NOT_FOUND", "صيغة البيع غير موجودة"));
        }
        var graph = (await _units.LoadGraphAsync(ct)).Value!;
        var sku = new SkuSaleInfo(
            data.SkuId, data.ProductId, data.SkuCode, ParseSaleMode(data.SaleMode),
            data.BaseUomId, data.SaleUomId, data.MinWeightKg, data.MaxWeightKg);

        if (sku.SaleMode == SaleMode.Serialized)
        {
            return Result.Fail<ResolvedLine>(new(
                "SERIALIZED_POS_NOT_SUPPORTED",
                "بيع الأصناف المسلسلة يتطلب تفعيل تتبع السيريالات — خارج نطاق G7 (رفض صريح لا تخمين)"));
        }

        // ── الوزن ومصدره (§5.1 — المصدر مسجل دائماً ولا يُعرض المشتق كقراءة ميزان) ──
        SkuWeightPolicy? weightPolicy = sku.SaleMode == SaleMode.Weight
            ? BuildWeightPolicy(data, settings.Weighing)
            : null;
        decimal? weightKg = null, amountTarget = null, tareKg = line.TareKg;
        string? weightSource = line.WeightSource;

        if (sku.SaleMode == SaleMode.Weight)
        {
            if (string.IsNullOrWhiteSpace(weightSource) || !WeightSources.IsValid(weightSource))
            {
                return Result.Fail<ResolvedLine>(new("WEIGHT_SOURCE_REQUIRED", "السطر الوزني يتطلب مصدر وزن معتمداً (§5.1)"));
            }
            var policy = weightPolicy!;
            switch (weightSource)
            {
                case WeightSources.AmountDerived:
                {
                    if (line.AmountTarget is not { } target)
                    {
                        return Result.Fail<ResolvedLine>(new("AMOUNT_TARGET_REQUIRED", "المبلغ المستهدف مطلوب لمصدر AMOUNT_DERIVED"));
                    }
                    var pricePerKg = UnitPriceInKg(data, graph);
                    var calc = WeightSaleRules.AcceptAmountTarget(pricePerKg, target, policy, settings.Weighing);
                    if (!calc.IsSuccess)
                    {
                        return Result.Fail<ResolvedLine>(calc.Error!);
                    }
                    weightKg = calc.Value!.DisplayWeightGrams / 1000m;
                    amountTarget = target;
                    break;
                }
                case WeightSources.ScaleReading or WeightSources.QuickPick
                    or WeightSources.ManualWeight or WeightSources.WeightLabel or WeightSources.PackPreweighed:
                {
                    if (line.WeightKg is not { } w)
                    {
                        return Result.Fail<ResolvedLine>(new("WEIGHT_REQUIRED", "الوزن مطلوب لهذا المصدر"));
                    }
                    var check = weightSource == WeightSources.ManualWeight
                        ? WeightSaleRules.AcceptManualWeight(w, policy, policy.AllowManualWeight)
                        : WeightSaleRules.AcceptWeightLabel(w, policy);
                    if (!check.IsSuccess)
                    {
                        return Result.Fail<ResolvedLine>(check.Error!);
                    }
                    weightKg = check.Value!;
                    break;
                }
                default:
                    return Result.Fail<ResolvedLine>(new("WEIGHT_SOURCE_UNKNOWN", "مصدر وزن غير معروف"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(weightSource))
        {
            return Result.Fail<ResolvedLine>(new("WEIGHT_SOURCE_NOT_ALLOWED", "مصدر الوزن يُقبل للصيغ الوزنية فقط"));
        }

        // ── الكمية ولقطة التحويل (بوابة G4: أثر واحد على المحتوى عند PACK) ──
        long sourceUom = line.UomId ?? data.SaleUomId;
        if (!graph.HasUnit(sourceUom))
        {
            return Result.Fail<ResolvedLine>(new("UNIT_UNKNOWN", "وحدة القياس غير معرفة في المخطط"));
        }
        var uomCode = graph.Unit(sourceUom).Code;

        PackageInfo? package = null;
        SkuSaleInfo? contentSku = null;
        if (sku.SaleMode == SaleMode.Pack)
        {
            if (data.PackageId is null || data.UnitsPerPackage is null || data.ContentSkuId is null)
            {
                return Result.Fail<ResolvedLine>(new("PACKAGE_DEFINITION_REQUIRED", "صيغة PACK بلا تعريف عبوة ساري"));
            }
            package = new PackageInfo(data.SkuId, data.ContentSkuId.Value, data.UnitsPerPackage.Value, OpenPackageAllowed: true);
            contentSku = new SkuSaleInfo(
                data.ContentSkuId.Value, 0, data.ContentSkuCode ?? "", SaleMode.Count,
                data.ContentBaseUomId ?? data.BaseUomId, data.ContentBaseUomId ?? data.BaseUomId, null, null);
        }

        // الوزني: الكمية = الوزن بوحدة البيع (الكيلوجرام) — الوزن بوحدات كتلة فقط
        decimal qty = sku.SaleMode == SaleMode.Weight
            ? weightKg!.Value * Factor(graph, "KG", uomCode)
            : line.Qty;

        var snap = StockQuantityCalculator.Resolve(sku, qty, sourceUom, graph, package, contentSku);
        if (!snap.IsSuccess)
        {
            return Result.Fail<ResolvedLine>(snap.Error!);
        }
        var s = snap.Value!;

        // ── السعر: من قائمة الأسعار السارية، محوَّلاً لوحدة البيع — لا يُثق بسعر العميل ──
        decimal unitPrice = data.UnitPrice * Factor(graph, data.PriceUomCode, uomCode);
        if (line.PriceOverride is { } overridePrice)
        {
            if (overridePrice <= 0)
            {
                return Result.Fail<ResolvedLine>(new("PRICE_OVERRIDE_INVALID", "سعر التعديل يجب أن يكون موجباً"));
            }
            unitPrice = overridePrice; // الصلاحية والسبب يفحصهما حال الاستخدام (sales.price.override)
        }
        if (unitPrice <= 0)
        {
            return Result.Fail<ResolvedLine>(new("PRICE_NOT_CONFIGURED", "لا سعر ساري لهذا الصنف — لا بيع بلا سعر موثق"));
        }

        // ── لقطة التكلفة WAC للوحدة الأساسية للمحتوى/الصنف (§15.3 — تكلفة اليوم مجمدة في التاريخ) ──
        decimal costPerBase = data.ContentCostAvg;
        decimal costSnapshot = s.BaseQty >= 0 ? decimal.Round(s.BaseQty * costPerBase, 4, MidpointRounding.AwayFromZero) : 0m;

        return Result.Ok(new ResolvedLine(
            data.SkuId, data.SkuCode, data.ProductId, data.ProductNameAr, data.SaleMode,
            data.PackageId, qty, sourceUom, uomCode,
            s.ConversionFactor, s.BaseQty, s.SkuId,
            weightKg, weightSource, amountTarget, tareKg,
            unitPrice, data.PriceListId, data.TaxTreatment, data.TaxRate,
            line.LineDiscount ?? 0m, costSnapshot, costPerBase, line.Notes,
            weightPolicy, data.AllowNegative ?? false));
    }

    /// <summary>سعر الكيلوجرام من سعر وحدة السعر (للمبلغ المستهدف — §9.3 ق3).</summary>
    internal static decimal UnitPriceInKg(SaleItemData data, UnitConversionGraph graph) =>
        data.UnitPrice * Factor(graph, data.PriceUomCode, "KG");

    private static decimal Factor(UnitConversionGraph graph, string fromCode, string toCode)
    {
        if (fromCode == toCode)
        {
            return 1m;
        }
        var from = graph.Units.FirstOrDefault(u => u.Code == fromCode);
        var to = graph.Units.FirstOrDefault(u => u.Code == toCode);
        if (from is null || to is null)
        {
            return 1m;
        }
        var factor = graph.Factor(from.UomId, to.UomId);
        return factor.IsSuccess ? factor.Value! : 1m;
    }

    private static SaleMode ParseSaleMode(string mode) => mode switch
    {
        SaleModes.Count => SaleMode.Count,
        SaleModes.Weight => SaleMode.Weight,
        SaleModes.Pack => SaleMode.Pack,
        SaleModes.Serialized => SaleMode.Serialized,
        _ => throw new InvalidOperationException($"SALE_MODE_UNKNOWN: {mode}")
    };
}

// ═══════════════ سياق المحطة (§9.1A — شريط الجلسة) ═════════════════════════════

public sealed class GetPosContextUseCase
{
    private readonly ISalesRepository _sales;
    private readonly PosSettingsReader _settings;
    private readonly ICurrentUser _user;

    public GetPosContextUseCase(ISalesRepository sales, PosSettingsReader settings, ICurrentUser user)
    {
        _sales = sales;
        _settings = settings;
        _user = user;
    }

    public async Task<Result<Contracts.V1.PosContextResponse>> ExecuteAsync(CancellationToken ct = default)
    {
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.PosContextResponse>(new("STATION_REQUIRED", "POS يتطلب جلسة محطة — سجل الدخول على محطة"));
        }
        var station = await _sales.GetStationAsync(stationId, ct);
        if (station is null)
        {
            return Result.Fail<Contracts.V1.PosContextResponse>(new("STATION_NOT_FOUND", "المحطة غير موجودة"));
        }
        var settings = await _settings.ReadAsync(ct);
        var shift = await _sales.GetCurrentShiftAsync(stationId, ct);
        var drawer = await _sales.GetDrawerAccountAsync(station.BranchId, ct);
        var printer = await _sales.GetStationReceiptPrinterAsync(stationId, ct);
        var routing = ReceiptRouting.Resolve(printer is null ? null
            : new StationReceiptPrinter(
                printer.PrinterProfileId, printer.MediaClass, printer.PrintableWidthMm,
                printer.VerifiedMediaWidthMm, printer.IsCalibrated, printer.IsAutoRoutingEnabled));

        string cardNotice = settings.CardProvider switch
        {
            "NOT_CONFIGURED" => "البطاقة معطلة حتى اعتماد مزود وSandbox موثق (B-02/D-11) — النقد والمختلط نقد+نقد يعملان",
            "SANDBOX_SIMULATOR" => "محاكاة SANDBOX محلية موسومة (للاختبار فقط) — لا مزود حقيقي بعد (B-02/D-11)",
            _ => $"مزود معتمد: {settings.CardProvider}"
        };

        var printerStatus = new Contracts.V1.ReceiptPrinterStatusDto(
            Assigned: printer is not null,
            MediaClass: printer?.MediaClass,
            Verified: printer?.VerifiedMediaWidthMm is not null,
            AutoRoutingEnabled: printer?.IsAutoRoutingEnabled ?? false,
            HoldReasonCode: routing.IsSuccess ? null : routing.Error!.Code,
            StatusTextAr: routing.IsSuccess
                ? $"إيصال العميل يوجه تلقائياً إلى طابعة المحطة ({printer!.MediaClass} معاير) — لا Print Dialog ولا A4 (§29.4)"
                : routing.Error!.Code == "HOLD_PRINTER_NOT_ASSIGNED"
                    ? "لا طابعة معتمدة لدور إيصال العميل — الإيصال يبقى محفوظاً بانتظار تعيين مسؤول (HOLD)"
                    : $"توجيه الإيصال موقوف: {routing.Error.MessageAr}");

        return Result.Ok(new Contracts.V1.PosContextResponse(
            StationId: station.StationId,
            StationCode: station.StationCode,
            StationNameAr: station.StationNameAr,
            BranchId: station.BranchId,
            BranchNameAr: station.BranchNameAr,
            CashierDisplayAr: _user.Username,
            ShiftId: shift?.ShiftId,
            ShiftNumber: shift?.ShiftNumber,
            ShiftOpenedAtUtc: shift?.OpenedAtUtc,
            ShiftOpeningFloat: shift?.OpeningFloat,
            DrawerTreasuryAccountId: drawer?.TreasuryAccountId,
            DrawerTreasuryAccountCode: drawer?.Code,
            TaxMode: settings.TaxMode,
            TaxStandardRate: settings.TaxRate,
            DiscountLimits: settings.DiscountLimits,
            Weighing: new Contracts.V1.WeighingSettingsDto(
                settings.Weighing.PrecisionGrams, settings.Weighing.StepGrams,
                "ROUND_HALF_EVEN_0.01", "NEAREST_STEP"),
            Card: new Contracts.V1.CardProviderStatusDto(
                ProviderName: settings.CardProvider,
                Enabled: settings.CardProvider is "SANDBOX_SIMULATOR" or not "NOT_CONFIGURED",
                NoticeAr: cardNotice),
            ReceiptPrinter: printerStatus));
    }
}

// ═══════════════ نطاق POS: محطة → فرع → مخزن البيع (يفك مرة في كل حالة استخدام) ═

public sealed record PosScope(PosStationData Station, long WarehouseId);

/// <summary>يفك محطة الجلسة إلى فرعها ومخزن بيعها — POS بلا محطة لا يعمل (§9.1A).</summary>
public sealed class PosScopeResolver(ISalesRepository sales, ICurrentUser user)
{
    public async Task<Result<PosScope>> ResolveAsync(CancellationToken ct = default)
    {
        if (user.StationId is not { } stationId)
        {
            return Result.Fail<PosScope>(new("STATION_REQUIRED", "POS يتطلب جلسة محطة — سجل الدخول على محطة"));
        }
        var station = await sales.GetStationAsync(stationId, ct);
        if (station is null)
        {
            return Result.Fail<PosScope>(new("STATION_NOT_FOUND", "المحطة غير موجودة"));
        }
        var warehouseId = await sales.GetPosWarehouseIdAsync(station.BranchId, ct);
        return warehouseId is null
            ? Result.Fail<PosScope>(new("POS_WAREHOUSE_NOT_FOUND", "لا مخزن بيع نشط لفرع المحطة — عرّف مخزناً للفرع"))
            : Result.Ok(new PosScope(station, warehouseId.Value));
    }
}

// ═══════════════ الالتقاط: باركود/SKU/PLU + باركود الوزن المتغير (§9.2 · §5.3) ═

public sealed class LookupPosItemUseCase
{
    private readonly ISalesRepository _sales;
    private readonly IUnitCatalog _units;
    private readonly PosScopeResolver _scope;

    public LookupPosItemUseCase(ISalesRepository sales, IUnitCatalog units, PosScopeResolver scope)
    {
        _sales = sales;
        _units = units;
        _scope = scope;
    }

    public async Task<Result<Contracts.V1.PosLookupResponse>> ExecuteAsync(
        string? rawCode, CancellationToken ct = default)
    {
        var scope = await _scope.ResolveAsync(ct);
        if (!scope.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PosLookupResponse>(scope.Error!);
        }
        var warehouseId = scope.Value!.WarehouseId;
        var code = (rawCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return Result.Fail<Contracts.V1.PosLookupResponse>(new("LOOKUP_CODE_EMPTY", "أدخل باركوداً أو كوداً"));
        }

        // 1) Exact Match بالباركود المنمّط (§3.1)
        var normalized = BarcodeNormalizer.NormalizeForLookup(code);
        long? skuId = await _sales.FindSkuIdByBarcodeAsync(normalized, ct)
            ?? await _sales.FindSkuIdBySkuCodeAsync(code, ct);

        Contracts.V1.PosLookupResponse NotFound(string ar) =>
            new(false, null, false, null, null, ar);

        // 2) باركود وزن متغير (ملصق الميزان — §5.3): لا تخمين، تحليل الملف المعتمد فقط
        if (skuId is null)
        {
            var profiles = await _sales.GetActiveVwProfilesAsync(ct);
            if (profiles.Count > 0)
            {
                var parsed = VariableWeightBarcodeParser.Parse(
                    code,
                    profiles.Select(p => new VariableWeightProfile(
                        p.ProfileId, p.Name, p.Prefix, p.TotalLength, p.PluStartPos, p.PluLength,
                        p.ValueType, p.ValueStartPos, p.ValueLength, p.ValueDecimals,
                        p.ValueUnitId, p.CheckDigitRule)).ToList());
                if (parsed.IsSuccess)
                {
                    var v = parsed.Value!;
                    var weightSku = await _sales.FindSkuIdByPluAsync(v.PluCode, ct);
                    if (weightSku is null)
                    {
                        return Result.Ok(NotFound($"باركود وزن صالح البنية لكن PLU {v.PluCode} غير مرتبط بصنف نشط — رفض موثق (§5.3)"));
                    }
                    var data = await _sales.GetSaleItemDataAsync(weightSku.Value, warehouseId, null, ct);
                    if (data is null)
                    {
                        return Result.Ok(NotFound("الصنف الوزني غير مهيأ للبيع"));
                    }
                    if (v.ValueType != "WEIGHT")
                    {
                        return Result.Ok(NotFound("ملصق يشفر السعر لا الوزن — رفض موثق: لا يُخمن الوزن من السعر (§5.3)"));
                    }
                    if (v.ValueUnitId is null)
                    {
                        return Result.Ok(NotFound("ملف الباركود بلا وحدة قيمة معرفة — لا تحويل بلا وحدة (لا تخمين §5.3)"));
                    }
                    var graph = (await _units.LoadGraphAsync(ct)).Value!;
                    var kgUnit = graph.Units.FirstOrDefault(u => u.Code == "KG");
                    var srcUnit = graph.Units.FirstOrDefault(u => u.UomId == v.ValueUnitId.Value);
                    if (kgUnit is null || srcUnit is null || srcUnit.Dimension != UnitDimension.Mass)
                    {
                        return Result.Ok(NotFound("وحدة القيمة في ملف الباركود ليست وحدة كتلة — رفض موثق"));
                    }
                    var toKg = graph.Factor(srcUnit.UomId, kgUnit.UomId);
                    if (!toKg.IsSuccess)
                    {
                        return Result.Ok(NotFound("لا تحويل من وحدة القيمة إلى الكيلوجرام — رفض موثق"));
                    }
                    return Result.Ok(new Contracts.V1.PosLookupResponse(
                        Found: true,
                        Item: await ToDtoAsync(data, warehouseId, ct),
                        IsWeightLabel: true,
                        LabelWeightKg: decimal.Round(v.Value * toKg.Value!, 3, MidpointRounding.ToEven),
                        LabelProfileName: v.ProfileName,
                        ErrorAr: null));
                }
            }
        }

        // 3) PLU مباشر
        skuId ??= await _sales.FindSkuIdByPluAsync(code, ct);
        if (skuId is null)
        {
            return Result.Ok(NotFound($"لا صنف بباركود/كود/PLU «{code}» — استخدم البحث النصي F2"));
        }
        var item = await _sales.GetSaleItemDataAsync(skuId.Value, warehouseId, null, ct);
        return item is null
            ? Result.Ok(NotFound("الصنف غير مهيأ للبيع (لا سعر ساري)"))
            : Result.Ok(new Contracts.V1.PosLookupResponse(
                Found: true, Item: await ToDtoAsync(item, warehouseId, ct),
                IsWeightLabel: false, LabelWeightKg: null, LabelProfileName: null, ErrorAr: null));
    }

    private async Task<Contracts.V1.PosItemDto> ToDtoAsync(SaleItemData d, long warehouseId, CancellationToken ct)
    {
        var settings = new PosSettingsReader(_sales);
        var s = await settings.ReadAsync(ct);
        Contracts.V1.WeightPolicyDto? weight = d.SaleMode == SaleModes.Weight
            ? new Contracts.V1.WeightPolicyDto(
                d.MinWeightKg, d.MaxWeightKg,
                d.WeightStepG ?? s.Weighing.StepGrams, d.WeightPrecisionG ?? s.Weighing.PrecisionGrams,
                d.AllowManualWeight ?? true, d.AllowAmountTarget ?? true, d.DefaultTareKg,
                QuickPickGrams(d.WeightStepG ?? s.Weighing.StepGrams))
            : null;
        return new Contracts.V1.PosItemDto(
            d.SkuId, d.SkuCode, d.ProductNameAr, d.SaleMode,
            d.UnitPrice, d.PriceUomCode, d.PriceUomId,
            d.TaxTreatment, d.TaxRate,
            d.PackageId, d.UnitsPerPackage, d.ContentSkuCode,
            d.AvailableQty, d.BaseUomCode, d.ImageUrl, weight);
    }

    private static readonly int[] QuickPickCandidates = [100, 250, 350, 500, 1000];

    private static int[] QuickPickGrams(int stepGrams) =>
        QuickPickCandidates
            .Where(g => WeightSaleRules.QuickPickAllowed(g, new SkuWeightPolicy(null, null, stepGrams, 1, true, true, 0m)))
            .ToArray();
}

public sealed class SearchPosItemsUseCase
{
    private readonly ISalesRepository _sales;
    private readonly PosScopeResolver _scope;

    public SearchPosItemsUseCase(ISalesRepository sales, PosScopeResolver scope)
    {
        _sales = sales;
        _scope = scope;
    }

    public async Task<Result<Contracts.V1.PosSearchResponse>> ExecuteAsync(
        string? query, int page, int pageSize, CancellationToken ct = default)
    {
        var scope = await _scope.ResolveAsync(ct);
        if (!scope.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PosSearchResponse>(scope.Error!);
        }
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var (items, total) = await _sales.SearchSaleItemsPagedAsync(query ?? string.Empty, page, pageSize, scope.Value!.WarehouseId, ct);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        var dtos = items.Select(ToDto).ToList();
        return Result.Ok(new Contracts.V1.PosSearchResponse(page, pageSize, total, Math.Max(1, totalPages), dtos));
    }

    private static Contracts.V1.PosItemDto ToDto(SaleItemData d) => new(
        d.SkuId, d.SkuCode, d.ProductNameAr, d.SaleMode, d.UnitPrice, d.PriceUomCode, d.PriceUomId,
        d.TaxTreatment, d.TaxRate, d.PackageId, d.UnitsPerPackage, d.ContentSkuCode,
        d.AvailableQty, d.BaseUomCode, d.ImageUrl,
        d.SaleMode == SaleModes.Weight
            ? new Contracts.V1.WeightPolicyDto(d.MinWeightKg, d.MaxWeightKg, d.WeightStepG ?? 5,
                d.WeightPrecisionG ?? 1, d.AllowManualWeight ?? true, d.AllowAmountTarget ?? true,
                d.DefaultTareKg, QuickPickDefaults)
            : null);

    private static readonly int[] QuickPickDefaults = [100, 250, 500, 1000];
}

// ═══════════════ معاينة خادمية (§9.0 ق10: المجاميع من خدمة الفاتورة فقط) ════════

public sealed class PreviewSaleUseCase
{
    private readonly SaleLineResolver _resolver;
    private readonly PosScopeResolver _scope;
    private readonly PosSettingsReader _settings;

    public PreviewSaleUseCase(SaleLineResolver resolver, PosScopeResolver scope, PosSettingsReader settings)
    {
        _resolver = resolver;
        _scope = scope;
        _settings = settings;
    }

    public async Task<Result<Contracts.V1.PosPreviewResponse>> ExecuteAsync(
        Contracts.V1.PosPreviewRequest request, CancellationToken ct = default)
    {
        var scope = await _scope.ResolveAsync(ct);
        if (!scope.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PosPreviewResponse>(scope.Error!);
        }
        var warehouseId = scope.Value!.WarehouseId;
        var settings = await _settings.ReadAsync(ct);
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.PosPreviewResponse>(new("CART_EMPTY", "السلة فارغة"));
        }
        var computed = new List<(SaleLineResolver.ResolvedLine Line, ComputedSaleLine Calc)>();
        foreach (var line in request.Lines)
        {
            var resolved = await _resolver.ResolveAsync(line, warehouseId, settings, ct);
            if (!resolved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PosPreviewResponse>(resolved.Error!);
            }
            var r = resolved.Value!;
            var calc = SalesMath.ComputeLine(new SaleLineComputationInput(
                r.Qty, r.UnitPrice, r.TaxRate, r.TaxTreatment, r.LineDiscount, settings.TaxMode));
            computed.Add((r, calc));
        }

        var shares = SalesMath.DistributeInvoiceDiscount(
            computed.Select(c => c.Calc.DueShare).ToList(), request.InvoiceDiscount ?? 0m);

        var lines = new List<Contracts.V1.PosPreviewLine>();
        var finals = new List<ComputedSaleLine>();
        for (var i = 0; i < computed.Count; i++)
        {
            var (r, calc) = computed[i];
            var final = SalesMath.ApplyInvoiceDiscountShare(calc, shares[i], r.TaxRate, r.TaxTreatment, settings.TaxMode);
            finals.Add(final);
            lines.Add(new Contracts.V1.PosPreviewLine(
                i + 1, r.SkuId, r.SkuCode, r.ProductNameAr, r.SaleMode, r.Qty, r.UomCode,
                r.WeightKg, r.WeightSource, r.UnitPrice, r.LineDiscount, shares[i],
                final.TaxAmount, final.NetAmount, final.DueShare));
        }

        var totals = SalesMath.ComputeTotals(finals, request.InvoiceDiscount ?? 0m, paidTotal: 0m);
        return Result.Ok(new Contracts.V1.PosPreviewResponse(lines, ToDto(totals)));
    }

    internal static Contracts.V1.SaleTotalsDto ToDto(in SaleTotals t) => new(
        t.GrossTotal, t.LineDiscountTotal, t.InvoiceDiscount, t.TaxTotal, t.NetTotal,
        t.DueAmount, t.PaidTotal, t.ChangeDue);
}

// ═══════════════ إتمام البيع (§9.7 — الخطوات الاثنتا عشرة) ═════════════════════

public sealed partial class PostSaleUseCase
{
    private readonly ISalesRepository _sales;
    private readonly SaleLineResolver _resolver;
    private readonly PosSettingsReader _settings;
    private readonly ICardPaymentGateway _card;
    private readonly IUserRepository _users;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<PostSaleUseCase> _logger;

    public PostSaleUseCase(
        ISalesRepository sales, SaleLineResolver resolver, PosSettingsReader settings,
        ICardPaymentGateway card, IUserRepository users, IAuditWriter audit,
        ICurrentUser user, ILogger<PostSaleUseCase> logger)
    {
        _sales = sales;
        _resolver = resolver;
        _settings = settings;
        _card = card;
        _users = users;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.PostedSaleResponse>> ExecuteAsync(
        Contracts.V1.PostSaleRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(new("STATION_REQUIRED", "POS يتطلب جلسة محطة"));
        }
        var station = await _sales.GetStationAsync(stationId, ct);
        if (station is null)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(new("STATION_NOT_FOUND", "المحطة غير موجودة"));
        }
        var warehouseId = await _sales.GetPosWarehouseIdAsync(station.BranchId, ct);
        if (warehouseId is null)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(new("POS_WAREHOUSE_NOT_FOUND", "لا مخزن بيع للمحطة — عرّف مخزناً نشطاً للفرع"));
        }
        var settings = await _settings.ReadAsync(ct);

        // ── Idempotency: إعادة الإرسال بعد timeout تعيد الأصل ولا تبيع مرتين (§9.5/§15.4) ──
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            if (await _sales.FindInvoiceIdByIdempotencyKeyAsync(request.IdempotencyKey, ct) is { } existingId)
            {
                return await ReplayAsync(existingId, ct);
            }
        }

        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CART_EMPTY", "لا ترحيل لسلة فارغة"));
        }

        // ── 2) إعادة التسعير على الخادم: لا سعر ولا خصم من العميل يمر كما هو ──
        var resolved = new List<SaleLineResolver.ResolvedLine>();
        foreach (var line in request.Lines)
        {
            var r = await _resolver.ResolveAsync(line, warehouseId.Value, settings, ct);
            if (!r.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(r.Error!);
            }
            resolved.Add(r.Value!);
        }

        var perms = await _users.GetPermissionCodesAsync(_user.UserId, station.BranchId, ct);
        var has = (string code) => perms.Contains(code);

        // ── تعديل السعر: صلاحية + سبب (§9.4) ──
        if (request.Lines.Any(l => l.PriceOverride is not null))
        {
            if (!has(Domain.Security.PermissionCodes.SalesPriceOverride))
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("PRICE_OVERRIDE_DENIED",
                    "تعديل السعر يتطلب صلاحية sales.price.override (يسجل السابق والجديد في التدقيق)"));
            }
            if (request.Lines.Any(l => l.PriceOverride is not null && string.IsNullOrWhiteSpace(l.PriceOverrideReason)))
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("PRICE_OVERRIDE_REASON_REQUIRED", "تعديل السعر يتطلب سبباً موثقاً"));
            }
        }

        // ── الخصومات: حدود الدور (D-06) — كاشير 5% · مشرف 15% · فوقها مدير/مالك ──
        var discountCheck = CheckDiscountPermissions(request, resolved, settings, perms);
        if (discountCheck is not null)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(discountCheck);
        }

        // ── الحساب النهائي مع توزيع خصم الفاتورة حتمياً (§16 ق5) ──
        var computed = resolved
            .Select(r => (r, SalesMath.ComputeLine(new SaleLineComputationInput(
                r.Qty, r.UnitPrice, r.TaxRate, r.TaxTreatment, r.LineDiscount, settings.TaxMode))))
            .ToList();
        var shares = SalesMath.DistributeInvoiceDiscount(
            computed.Select(c => c.Item2.DueShare).ToList(), request.InvoiceDiscount ?? 0m);
        var finals = new List<ComputedSaleLine>();
        for (var i = 0; i < computed.Count; i++)
        {
            finals.Add(SalesMath.ApplyInvoiceDiscountShare(
                computed[i].Item2, shares[i], resolved[i].TaxRate, resolved[i].TaxTreatment, settings.TaxMode));
        }
        var preTotals = SalesMath.ComputeTotals(finals, request.InvoiceDiscount ?? 0m, 0m);
        var due = preTotals.DueAmount;

        // ── الدفع (§9.6): نقد/بطاقة/مختلط/آجل ──
        var parts = request.Payments
            .Select(p => new PaymentPart(p.Method, p.Amount))
            .ToList();
        var plan = PaymentMath.PlanPayments(due, parts);
        if (!plan.IsSuccess)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(plan.Error!);
        }
        var paymentPlan = plan.Value!;

        // ── البطاقة: لا ادعاء مزود — NOT_CONFIGURED يرفض، والمحاكاة موسومة (B-02/D-11) ──
        CardChargeResult? cardCharge = null;
        var cardAmount = parts.Where(p => p.Method == PaymentMethods.Card).Sum(p => p.Amount);
        if (cardAmount > 0)
        {
            if (settings.CardProvider == "NOT_CONFIGURED")
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CARD_PROVIDER_NOT_CONFIGURED",
                    "البطاقة معطلة حتى اعتماد مزود وSandbox موثق (B-02/D-11) — النقد والمختلط نقد+نقد متاحان"));
            }
            cardCharge = await _card.ChargeAsync(cardAmount, request.IdempotencyKey ?? $"card-{Guid.NewGuid():N}", ct);
            if (!cardCharge.Approved)
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CARD_DECLINED",
                    cardCharge.DeclineReasonAr ?? "رفض جهاز الدفع العملية"));
            }
        }

        // ── الآجل: عميل + حد ائتمان + صلاحية (D-05) ──
        CustomerCreditData? customer = null;
        if (paymentPlan.HasCredit)
        {
            if (request.CustomerId is not { } custId)
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CREDIT_CUSTOMER_REQUIRED", "البيع الآجل يتطلب عميلاً محدداً (§9.4)"));
            }
            if (!has(Domain.Security.PermissionCodes.SalesCreditSell))
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CREDIT_PERMISSION_DENIED", "البيع الآجل يتطلب صلاحية sales.credit.sell"));
            }
            customer = await _sales.GetCustomerCreditAsync(custId, ct);
            if (customer is null || customer.AccountStatus != "ACTIVE")
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CREDIT_CUSTOMER_INVALID", "العميل غير موجود أو غير نشط"));
            }
            if (!customer.CreditEnabled)
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(new("CREDIT_NOT_ENABLED_FOR_CUSTOMER",
                    "الآجل معطل لهذا العميل — تفعيله لكل عميل قرار موثق (D-05)"));
            }
            var creditAmount = parts.Where(p => p.Method == PaymentMethods.Credit).Sum(p => p.Amount);
            var outstanding = await _sales.GetCustomerOutstandingCreditAsync(custId, ct);
            var limit = CreditRules.CheckLimit(outstanding, customer.CreditLimit, creditAmount);
            if (!limit.IsSuccess)
            {
                return Result.Fail<Contracts.V1.PostedSaleResponse>(limit.Error!);
            }
        }
        else if (request.CustomerId is { } id)
        {
            customer = await _sales.GetCustomerCreditAsync(id, ct);
        }

        // ── المخزون وسياسة السالب (D-03): القيد النهائي مشغل V003 — الفحص المسبق لرسالة أوضح ──
        long? negativeApprovalId = null;
        var needed = resolved.GroupBy(r => r.MovementSkuId)
            .Select(g => (SkuId: g.Key, BaseQty: g.Sum(x => x.BaseQty)))
            .ToList();
        foreach (var need in needed)
        {
            var item = await _sales.GetSaleItemDataAsync(need.SkuId, warehouseId.Value, null, ct);
            if (item is null)
            {
                continue;
            }
            if (need.BaseQty > item.AvailableQty + 0.0000001m && item.AllowNegative != true)
            {
                if (settings.NegativeStockPolicy == "BLOCKED")
                {
                    return Result.Fail<Contracts.V1.PostedSaleResponse>(new("NEGATIVE_STOCK_BLOCKED",
                        $"الرصيد غير كافٍ للصنف {item.SkuCode} وسياسة السالب تمنع البيع"));
                }
                if (request.ApprovalId is not { } approvalId)
                {
                    return Result.Fail<Contracts.V1.PostedSaleResponse>(new("NEGATIVE_STOCK_NEEDS_APPROVAL",
                        $"البيع يجعل رصيد {item.SkuCode} سالباً — يتطلب اعتماد مشرف (D-03) عبر /approvals"));
                }
                negativeApprovalId = approvalId;
            }
        }

        // ── الوردية (§9.9): الفاتورة ترتبط بالوردية المفتوحة إن وجدت ──
        var shift = await _sales.GetCurrentShiftAsync(stationId, ct);

        // ── بناء الخطة والترحيل الذري ──
        var drawer = await _sales.GetDrawerAccountAsync(station.BranchId, ct);
        var providerAccount = await _sales.GetProviderAccountAsync(ct);

        var linePlans = new List<SaleLinePostPlan>();
        for (var i = 0; i < resolved.Count; i++)
        {
            var r = resolved[i];
            linePlans.Add(new SaleLinePostPlan(
                LineNo: i + 1,
                SkuId: r.SkuId, SkuCode: r.SkuCode, ProductNameAr: r.ProductNameAr, SaleMode: r.SaleMode,
                PackageId: r.PackageId, Qty: r.Qty, UomId: r.UomId, UomCode: r.UomCode,
                ConversionFactor: r.ConversionFactor, BaseQty: r.BaseQty,
                WeightKg: r.WeightKg, WeightSource: r.WeightSource, AmountTarget: r.AmountTarget, TareKg: r.TareKg,
                UnitPrice: r.UnitPrice, PriceListId: r.PriceListId, TaxTreatment: r.TaxTreatment, TaxRate: r.TaxRate,
                LineDiscount: r.LineDiscount, InvoiceDiscountShare: shares[i],
                TaxAmount: finals[i].TaxAmount, NetAmount: finals[i].NetAmount,
                CostSnapshot: r.CostSnapshot, CostMethod: "WAC",
                MovementSkuId: r.MovementSkuId, Notes: r.Notes));
        }

        var paymentPlans = new List<SalePaymentPostPlan>();
        foreach (var part in parts)
        {
            paymentPlans.Add(part.Method switch
            {
                PaymentMethods.Cash => new SalePaymentPostPlan(
                    PaymentMethods.Cash, part.Amount, "CAPTURED", null, null, drawer?.TreasuryAccountId),
                PaymentMethods.Card => new SalePaymentPostPlan(
                    PaymentMethods.Card, part.Amount, "CAPTURED",
                    _card.ProviderName, cardCharge?.Reference, providerAccount?.TreasuryAccountId),
                PaymentMethods.Credit => new SalePaymentPostPlan(
                    PaymentMethods.Credit, part.Amount, "CAPTURED", null, null, null),
                _ => new SalePaymentPostPlan(part.Method, part.Amount, "CAPTURED", null, null, null)
            });
        }

        var totals = SalesMath.ComputeTotals(finals, request.InvoiceDiscount ?? 0m, paymentPlan.TotalApplied);
        var postPlan = new SalePostPlan(
            BranchId: station.BranchId, StationId: stationId, ShiftId: shift?.ShiftId,
            CashierUserId: _user.UserId, CustomerId: request.CustomerId,
            Currency: "EGP", TaxMode: settings.TaxMode,
            GrossTotal: totals.GrossTotal, LineDiscountTotal: totals.LineDiscountTotal,
            InvoiceDiscount: totals.InvoiceDiscount, TaxTotal: totals.TaxTotal,
            NetTotal: totals.NetTotal, DueAmount: totals.DueAmount, PaidTotal: paymentPlan.TotalApplied,
            Lines: linePlans, Payments: paymentPlans,
            CashTendered: request.CashTendered,
            IdempotencyKey: request.IdempotencyKey,
            NegativeApprovalId: negativeApprovalId);

        try
        {
            var result = await _sales.PostSaleAsync(postPlan, ct);
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: stationId, BranchId: station.BranchId,
                EntitySchema: "sales.invoices", EntityId: result.InvoiceId, Action: "SALE_POST", Result: "SUCCESS",
                Reason: $"فاتورة {result.InvoiceNumber} · مستحق {totals.DueAmount:0.00} · حركات {result.MovementIds.Length}" +
                        (result.IdempotentReplay ? " · إعادة Idempotency" : string.Empty),
                CorrelationId: correlationId), ct);
            return Result.Ok(ToResponse(result, in totals, request));
        }
        catch (SalesPolicyException ex)
        {
            // تعويض شحن البطاقة إذا فشل ترحيل الفاتورة — لا يبقى شحن بلا فاتورة (§9.6)
            if (cardCharge is not null)
            {
                await _card.VoidAsync(cardCharge.Reference, ct);
            }
            LogFailed(ex.Code, correlationId);
            return Result.Fail<Contracts.V1.PostedSaleResponse>(new(ex.Code, ex.UserMessageAr));
        }
    }

    private static DomainError? CheckDiscountPermissions(
        Contracts.V1.PostSaleRequest request,
        List<SaleLineResolver.ResolvedLine> resolved,
        PosSettings settings,
        IReadOnlyList<string> perms)
    {
        decimal tierLimit = 0m;
        if (perms.Contains(Domain.Security.PermissionCodes.SalesDiscountOver15)) tierLimit = decimal.MaxValue;
        else if (perms.Contains(Domain.Security.PermissionCodes.SalesDiscountUpto15)) tierLimit = settings.DiscountLimits.SupervisorPct;
        else if (perms.Contains(Domain.Security.PermissionCodes.SalesDiscountUpto5)) tierLimit = settings.DiscountLimits.CashierPct;

        for (var i = 0; i < resolved.Count; i++)
        {
            var line = request.Lines[i];
            var r = resolved[i];
            if ((line.LineDiscount ?? 0m) > 0m)
            {
                var gross = decimal.Round(r.Qty * r.UnitPrice, 2);
                var pct = gross > 0 ? (line.LineDiscount!.Value / gross) * 100m : 0m;
                if (pct > tierLimit)
                {
                    return new("DISCOUNT_PERMISSION_DENIED",
                        $"خصم السطر {pct:0.#}% يتجاوز حد دورك ({(tierLimit == decimal.MaxValue ? "غير محدود" : $"{tierLimit:0.#}%")} — D-06)");
                }
            }
        }
        if ((request.InvoiceDiscount ?? 0m) > 0m)
        {
            var grossTotal = resolved.Sum(r => decimal.Round(r.Qty * r.UnitPrice, 2));
            var pct = grossTotal > 0 ? (request.InvoiceDiscount!.Value / grossTotal) * 100m : 0m;
            if (pct > tierLimit)
            {
                return new("DISCOUNT_PERMISSION_DENIED",
                    $"خصم الفاتورة {pct:0.#}% يتجاوز حد دورك ({(tierLimit == decimal.MaxValue ? "غير محدود" : $"{tierLimit:0.#}%")} — D-06)");
            }
        }
        return null;
    }

    private async Task<Result<Contracts.V1.PostedSaleResponse>> ReplayAsync(long invoiceId, CancellationToken ct)
    {
        var detail = await _sales.GetSaleAsync(invoiceId, ct);
        if (detail is null)
        {
            return Result.Fail<Contracts.V1.PostedSaleResponse>(new("INVOICE_NOT_FOUND", "فاتورة Idempotency غير موجودة"));
        }
        var totals = new SaleTotals(
            detail.GrossTotal, detail.LineDiscountTotal, detail.InvoiceDiscount, detail.TaxTotal,
            detail.NetTotal, detail.DueAmount, detail.PaidTotal, 0m);
        var jobs = await _sales.ListPrintJobsAsync(invoiceId, ct);
        var job = jobs.FirstOrDefault(j => j.CopyType == "CUSTOMER");
        return Result.Ok(new Contracts.V1.PostedSaleResponse(
            detail.InvoiceId, detail.InvoiceNumber, detail.Status,
            PreviewSaleUseCase.ToDto(in totals),
            [], [], [], null,
            new Contracts.V1.PrintOutcomeDto(
                JobCreated: job is not null, JobId: job?.JobId, JobStatus: job?.Status,
                MediaClass: job?.MediaClass, HoldReasonCode: null,
                NoticeAr: "إعادة إرسال بمفتاح Idempotency — أعيدت الفاتورة الأصلية دون أي ترحيل جديد (§15.4)"),
            IdempotentReplay: true));
    }

    private static Contracts.V1.PostedSaleResponse ToResponse(in PostedSaleResult result, in SaleTotals totals, Contracts.V1.PostSaleRequest request)
    {
        // الفكة من المسلَّم نقدياً (تميَّز عن «مستحق على العميل» — §9.1F)
        var change = request.CashTendered is { } tendered && tendered > totals.DueAmount
            ? MoneyRounding.ToCent(tendered - totals.DueAmount)
            : totals.ChangeDue;
        return new Contracts.V1.PostedSaleResponse(
            result.InvoiceId, result.InvoiceNumber, "POSTED",
            PreviewSaleUseCase.ToDto(in totals) with { ChangeDue = change },
            result.MovementIds, result.TreasuryTxnIds, result.PaymentIds, result.DrawerEventId,
            new Contracts.V1.PrintOutcomeDto(
                result.Receipt.JobCreated, result.Receipt.JobId, result.Receipt.JobStatus,
                result.Receipt.MediaClass, result.Receipt.HoldReasonCode, result.Receipt.NoticeAr),
            result.IdempotentReplay);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SALE_POST failed {Code} — correlation={CorrelationId}")]
    private partial void LogFailed(string code, string? correlationId);
}

// ═══════════════ إلغاء المسودة (§9.2 — قبل الترحيل بسبب؛ المرحلة تُعكس بمرتجع) ═

public sealed class VoidSaleUseCase
{
    private readonly ISalesRepository _sales;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public VoidSaleUseCase(ISalesRepository sales, IAuditWriter audit, ICurrentUser user)
    {
        _sales = sales;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<long>> ExecuteAsync(long invoiceId, string reasonAr, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reasonAr) || reasonAr.Trim().Length < 3)
        {
            return Result.Fail<long>(new("VOID_REASON_REQUIRED", "إلغاء الفاتورة يتطلب سبباً موثقاً (≥3 محارف)"));
        }
        try
        {
            var id = await _sales.VoidDraftInvoiceAsync(invoiceId, _user.UserId, reasonAr.Trim(), ct);
            if (!id.IsSuccess)
            {
                return Result.Fail<long>(id.Error!);
            }
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
                EntitySchema: "sales.invoices", EntityId: invoiceId, Action: "SALE_VOID_DRAFT", Result: "SUCCESS",
                Reason: reasonAr.Trim(), CorrelationId: correlationId), ct);
            return Result.Ok(id.Value!);
        }
        catch (SalesPolicyException ex)
        {
            return Result.Fail<long>(new(ex.Code, ex.UserMessageAr));
        }
    }
}

// ═══════════════ السلال المعلقة (§9.5 — لا تنشئ بيعاً ولا دفعاً ولا حركة) ═══════

public sealed class ManageHeldCartsUseCase
{
    private readonly ISalesRepository _sales;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageHeldCartsUseCase(ISalesRepository sales, IAuditWriter audit, ICurrentUser user)
    {
        _sales = sales;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<long>> HoldAsync(string? cartJson, string? noteAr, long? customerId, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cartJson))
        {
            return Result.Fail<long>(new("CART_SNAPSHOT_REQUIRED", "لقطة السلة مطلوبة للتعليق"));
        }
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<long>(new("STATION_REQUIRED", "التعليق يتطلب جلسة محطة"));
        }
        var cartId = await _sales.HoldCartAsync(
            new HoldCartPlan(stationId, _user.UserId, customerId, noteAr, cartJson), ct);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: stationId, BranchId: _user.BranchId,
            EntitySchema: "sales.held_carts", EntityId: cartId, Action: "CART_HOLD", Result: "SUCCESS",
            Reason: noteAr, CorrelationId: correlationId), ct);
        return Result.Ok(cartId);
    }

    public async Task<Result<Contracts.V1.RestoreCartResponse>> RestoreAsync(long cartId, string? correlationId, CancellationToken ct = default)
    {
        var restored = await _sales.RestoreCartAsync(cartId, _user.UserId, ct);
        if (restored is null)
        {
            return Result.Fail<Contracts.V1.RestoreCartResponse>(new("CART_NOT_FOUND_OR_RESTORED", "السلة غير موجودة أو مستعادة سابقاً"));
        }
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: _user.BranchId,
            EntitySchema: "sales.held_carts", EntityId: cartId, Action: "CART_RESTORE", Result: "SUCCESS",
            Reason: "الاستعادة قراءة — لا بيع ولا دفع ولا حركة مخزون (§9.5)", CorrelationId: correlationId), ct);
        return Result.Ok(new Contracts.V1.RestoreCartResponse(restored.CartId, restored.CartJson, restored.CustomerId));
    }

    public async Task<Result<Contracts.V1.HeldCartDto[]>> ListAsync(bool openOnly, CancellationToken ct = default)
    {
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.HeldCartDto[]>(new("STATION_REQUIRED", "القائمة تتطلب جلسة محطة"));
        }
        var carts = await _sales.ListHeldCartsAsync(stationId, openOnly, ct);
        return Result.Ok(carts.Select(c => new Contracts.V1.HeldCartDto(
            c.CartId, c.HeldAtUtc, c.HeldByDisplayAr, c.NoteAr, c.Restored)).ToArray());
    }
}

// ═══════════════ المرتجعات والاستبدال (§9.8 · D-04 — رد بوسيلة الدفع الأصلية) ═══

public sealed partial class PostRefundUseCase
{
    private readonly ISalesRepository _sales;
    private readonly PosSettingsReader _settings;
    private readonly IUserRepository _users;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;
    private readonly ILogger<PostRefundUseCase> _logger;

    public PostRefundUseCase(
        ISalesRepository sales, PosSettingsReader settings, IUserRepository users,
        IAuditWriter audit, ICurrentUser user, ILogger<PostRefundUseCase> logger)
    {
        _sales = sales;
        _settings = settings;
        _users = users;
        _audit = audit;
        _user = user;
        _logger = logger;
    }

    public async Task<Result<Contracts.V1.PostedRefundResponse>> ExecuteAsync(
        Contracts.V1.PostRefundRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ReasonAr) || request.ReasonAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_REASON_REQUIRED", "المرتجع يتطلب سبباً (≥3 محارف)"));
        }
        if (request.Lines.Count == 0)
        {
            return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_LINES_EMPTY", "لا مرتجع بلا سطور"));
        }

        // ── Idempotency للمرتجع (V018): إعادة الإرسال تعيد الأصل ──
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            if (await _sales.FindRefundIdByIdempotencyKeyAsync(request.IdempotencyKey, ct) is { } existingRefund)
            {
                var existing = await _sales.GetRefundAsync(existingRefund, ct);
                if (existing is null)
                {
                    return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_REPLAY_NOT_FOUND", "مرتجع Idempotency غير موجود"));
                }
                return Result.Ok(new Contracts.V1.PostedRefundResponse(
                    existing.RefundId, existing.RefundNumber, existing.Total, existing.RefundMethod,
                    existing.Restock, [], [], existing.InvoiceStatusAfter, IdempotentReplay: true));
            }
        }

        var invoice = await _sales.GetSaleAsync(request.OriginalInvoiceId, ct);
        if (invoice is null)
        {
            return Result.Fail<Contracts.V1.PostedRefundResponse>(new("INVOICE_NOT_FOUND", "الفاتورة الأصلية غير موجودة"));
        }
        if (invoice.Status is not ("POSTED" or "PARTIAL_RETURN"))
        {
            return Result.Fail<Contracts.V1.PostedRefundResponse>(new("INVOICE_NOT_REFUNDABLE",
                $"الفاتورة في حالة {invoice.Status} — المرتجع من POSTED/PARTIAL_RETURN فقط"));
        }

        // ── الحدود: لا رد فوق ما بيع ولم يرد (قيد V005 tf_refund_line_guard يعيد الفحص) ──
        var linePlans = new List<RefundLinePostPlan>();
        foreach (var line in request.Lines)
        {
            var original = invoice.Lines.FirstOrDefault(l => l.LineId == line.OriginalLineId);
            if (original is null)
            {
                return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_LINE_NOT_IN_INVOICE", "سطر مرتجع لا ينتمي للفاتورة الأصلية"));
            }
            if (line.Qty <= 0)
            {
                return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_QTY_INVALID", "كمية المرتجع يجب أن تكون موجبة"));
            }
            var remaining = original.Qty - original.RefundedQty;
            if (line.Qty > remaining + 0.0000001m)
            {
                return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_EXCEEDS_SOLD",
                    $"المرتجع {line.Qty:0.######} يتجاوز المتبقي القابل للرد {remaining:0.######} من السطر {original.LineNo}"));
            }
            // رد بقيمة السطر الأصلية بعد كل الخصومات (شامل الضريبة) — لا تسعير جديد للمرتجع
            var dueLine = original.NetAmount + original.TaxAmount;
            var unitRefund = MoneyRounding.ToCent(dueLine / original.Qty);
            linePlans.Add(new RefundLinePostPlan(
                original.LineId, line.Qty, unitRefund,
                MoneyRounding.ToCent(unitRefund * line.Qty)));
        }
        var total = MoneyRounding.ToCent(linePlans.Sum(l => l.LineTotal));

        // ── وسيلة الرد = وسيلة الدفع الأصلية (D-04) — النقد للنقد والبطاقة للبطاقة ──
        var settings = await _settings.ReadAsync(ct);
        var originalMethods = invoice.Payments
            .Where(p => p.Status is "CAPTURED" or "SETTLED")
            .Select(p => p.Method)
            .Distinct()
            .ToList();
        var requested = request.RefundMethod;
        var allowedByOriginal = requested switch
        {
            RefundMethods.Cash => originalMethods.Contains(PaymentMethods.Cash),
            RefundMethods.Card => originalMethods.Contains(PaymentMethods.Card),
            RefundMethods.CreditReversal => originalMethods.Contains(PaymentMethods.Credit),
            _ => false
        };
        if (!allowedByOriginal)
        {
            // استثناء موثق واحد: رد نقدي لقيمة بطاقة بقرار وصلاحية (D-04)
            if (requested == RefundMethods.Cash && originalMethods.Contains(PaymentMethods.Card))
            {
                var perms = await _users.GetPermissionCodesAsync(_user.UserId, _user.BranchId, ct);
                if (settings.ReturnsCashForCard != "true" ||
                    !perms.Contains(Domain.Security.PermissionCodes.SalesRefundCashForCard))
                {
                    return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_METHOD_MISMATCH",
                        "رد قيمة البطاقة نقداً قرار استثنائي يتطلب سياسة معتمدة + صلاحية sales.refund.cash_for_card (D-04) — الرد للبطاقة"));
                }
            }
            else
            {
                return Result.Fail<Contracts.V1.PostedRefundResponse>(new("REFUND_METHOD_MISMATCH",
                    $"وسيلة الرد {requested} لا تطابق وسيلة الدفع الأصلية ({string.Join("/", originalMethods)}) — D-04"));
            }
        }

        var plan = new RefundPostPlan(
            OriginalInvoiceId: invoice.InvoiceId, BranchId: invoice.BranchId,
            CreatedByUserId: _user.UserId, ApprovalId: request.ApprovalId,
            RefundMethod: requested, Restock: request.Restock,
            ReasonAr: request.ReasonAr.Trim(), Total: total,
            Lines: linePlans, IdempotencyKey: request.IdempotencyKey);

        try
        {
            var result = await _sales.PostRefundAsync(plan, ct);
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: _user.StationId, BranchId: plan.BranchId,
                EntitySchema: "sales.refunds", EntityId: result.RefundId, Action: "REFUND_POST", Result: "SUCCESS",
                Reason: $"مرتجع {result.RefundNumber} على فاتورة {invoice.InvoiceNumber} · {total:0.00} · {requested}" +
                        (result.IdempotentReplay ? " · إعادة Idempotency" : string.Empty),
                CorrelationId: correlationId), ct);
            return Result.Ok(ToResponse(result, total, requested, request.Restock));
        }
        catch (SalesPolicyException ex)
        {
            LogFailed(ex.Code, correlationId);
            return Result.Fail<Contracts.V1.PostedRefundResponse>(new(ex.Code, ex.UserMessageAr));
        }
    }

    private static Contracts.V1.PostedRefundResponse ToResponse(
        in PostedRefundResult r, decimal total, string method, bool restock) => new(
        r.RefundId, r.RefundNumber, total, method, restock,
        r.MovementIds, r.TreasuryTxnIds, r.InvoiceStatusAfter, r.IdempotentReplay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "REFUND_POST failed {Code} — correlation={CorrelationId}")]
    private partial void LogFailed(string code, string? correlationId);
}

// ═══════════════ الورديات (§9.9 — فتح وملخص؛ الإغلاق التام G8) والدرج ═══════════

public sealed class ManageShiftsUseCase
{
    private readonly ISalesRepository _sales;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageShiftsUseCase(ISalesRepository sales, IAuditWriter audit, ICurrentUser user)
    {
        _sales = sales;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<Contracts.V1.ShiftDto>> OpenAsync(decimal openingFloat, string? correlationId, CancellationToken ct = default)
    {
        if (openingFloat < 0)
        {
            return Result.Fail<Contracts.V1.ShiftDto>(new("OPENING_FLOAT_INVALID", "الرصيد الافتتاحي لا يكون سالباً"));
        }
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.ShiftDto>(new("STATION_REQUIRED", "فتح الوردية يتطلب جلسة محطة"));
        }
        try
        {
            var shift = await _sales.OpenShiftAsync(stationId, _user.UserId, openingFloat, ct);
            if (!shift.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ShiftDto>(shift.Error!);
            }
            await _audit.WriteAsync(new AuditEntry(
                UserId: _user.UserId, RoleCode: null, StationId: stationId, BranchId: _user.BranchId,
                EntitySchema: "sales.shifts", EntityId: shift.Value!.ShiftId, Action: "SHIFT_OPEN", Result: "SUCCESS",
                Reason: $"افتتاحي {openingFloat:0.00}", CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(shift.Value!));
        }
        catch (SalesPolicyException ex)
        {
            return Result.Fail<Contracts.V1.ShiftDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.ShiftDto>> GetCurrentAsync(CancellationToken ct = default)
    {
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.ShiftDto>(new("STATION_REQUIRED", "الوردية تتطلب جلسة محطة"));
        }
        var shift = await _sales.GetCurrentShiftAsync(stationId, ct);
        return shift is null
            ? Result.Fail<Contracts.V1.ShiftDto>(new("SHIFT_NOT_OPEN", "لا وردية مفتوحة للمحطة — افتح وردية (§9.9)"))
            : Result.Ok(ToDto(shift));
    }

    public async Task<Result<Contracts.V1.ShiftSummaryDto>> GetSummaryAsync(long shiftId, CancellationToken ct = default)
    {
        var summary = await _sales.GetShiftSummaryAsync(shiftId, ct);
        return summary is null
            ? Result.Fail<Contracts.V1.ShiftSummaryDto>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"))
            : Result.Ok(new Contracts.V1.ShiftSummaryDto(
                ToDto(summary.Shift), summary.InvoiceCount, summary.NetSales, summary.CashCollected,
                summary.CardCollected, summary.CreditIssued, summary.CashRefunded, summary.ReturnsTotal,
                summary.ExpectedCashDrawer));
    }

    private static Contracts.V1.ShiftDto ToDto(in ShiftInfo s) => new(
        s.ShiftId, s.ShiftNumber, s.StationId, s.OpenedByDisplayAr, s.OpenedAtUtc, s.OpeningFloat, s.Status);
}

public sealed class OpenDrawerUseCase
{
    private readonly ISalesRepository _sales;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public OpenDrawerUseCase(ISalesRepository sales, IAuditWriter audit, ICurrentUser user)
    {
        _sales = sales;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<Contracts.V1.DrawerEventResponse>> ExecuteAsync(string reasonAr, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reasonAr))
        {
            return Result.Fail<Contracts.V1.DrawerEventResponse>(new("DRAWER_REASON_REQUIRED", "فتح الدرج بغير حركة بيع يتطلب سبباً (§9.9)"));
        }
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.DrawerEventResponse>(new("STATION_REQUIRED", "الدرج يتطلب جلسة محطة"));
        }
        var shift = await _sales.GetCurrentShiftAsync(stationId, ct);
        var evt = await _sales.RecordDrawerEventAsync(stationId, shift?.ShiftId, _user.UserId, "MANUAL_OPEN", reasonAr.Trim(), ct);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: stationId, BranchId: _user.BranchId,
            EntitySchema: "sales.drawer_events", EntityId: evt.EventId, Action: "DRAWER_MANUAL_OPEN", Result: evt.ResultStatus,
            Reason: reasonAr.Trim(), CorrelationId: correlationId), ct);
        // B-01 صادق: لا جهاز درج مختبراً بعد — الوقت المرسل موثق والحالة UNKNOWN لا استجابة مفترضة
        return Result.Ok(new Contracts.V1.DrawerEventResponse(
            evt.EventId, evt.EventType, evt.RequestedAtUtc, evt.RespondedAtUtc, evt.ResultStatus));
    }
}

// ═══════════════ الطباعة (§29.4/§29.9 — مهمة مستقلة، فشلها لا يعيد البيع) ══════

public sealed class ManageReceiptPrintingUseCase
{
    private readonly ISalesRepository _sales;
    private readonly IAuditWriter _audit;
    private readonly ICurrentUser _user;

    public ManageReceiptPrintingUseCase(ISalesRepository sales, IAuditWriter audit, ICurrentUser user)
    {
        _sales = sales;
        _audit = audit;
        _user = user;
    }

    public async Task<Result<Contracts.V1.PrintJobDto>> PrintAsync(
        long invoiceId, string copyType, string? correlationId, CancellationToken ct = default)
    {
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.PrintJobDto>(new("STATION_REQUIRED", "الطباعة تتطلب جلسة محطة"));
        }
        var invoice = await _sales.GetSaleAsync(invoiceId, ct);
        if (invoice is null)
        {
            return Result.Fail<Contracts.V1.PrintJobDto>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"));
        }
        long? reprintOf = null;
        if (copyType == "REPRINT")
        {
            var jobs = await _sales.ListPrintJobsAsync(invoiceId, ct);
            reprintOf = jobs.FirstOrDefault(j => j.CopyType == "CUSTOMER")?.JobId;
        }
        var outcome = await _sales.CreateReceiptJobAsync(invoiceId, stationId, _user.UserId, copyType, reprintOf, ct);
        await _audit.WriteAsync(new AuditEntry(
            UserId: _user.UserId, RoleCode: null, StationId: stationId, BranchId: _user.BranchId,
            EntitySchema: "print.print_jobs", EntityId: outcome.JobId ?? 0, Action: "RECEIPT_PRINT", Result: "SUCCESS",
            Reason: outcome.NoticeAr, CorrelationId: correlationId), ct);
        if (!outcome.JobCreated)
        {
            return Result.Fail<Contracts.V1.PrintJobDto>(new(outcome.HoldReasonCode ?? "PRINT_HELD", outcome.NoticeAr));
        }
        var job = await _sales.GetPrintJobAsync(outcome.JobId!.Value, ct);
        return job is null
            ? Result.Fail<Contracts.V1.PrintJobDto>(new("PRINT_JOB_NOT_FOUND", "مهمة الطباعة غير موجودة"))
            : Result.Ok(ToDto(job));
    }

    public async Task<Result<Contracts.V1.PrintJobDto[]>> ListAsync(long invoiceId, CancellationToken ct = default)
    {
        var jobs = await _sales.ListPrintJobsAsync(invoiceId, ct);
        return Result.Ok(jobs.Select(j => ToDto(j)).ToArray());
    }

    private static Contracts.V1.PrintJobDto ToDto(in PrintJobInfo j) => new(
        j.JobId, j.DocumentId, j.DocumentType, j.MediaClass, j.CopyType, j.Status,
        j.AttemptCount, j.FailureReasonAr, j.RelatedReprintOf, j.CreatedAtUtc);
}

// ═══════════════ نافذة الوزن خادمياً (§9.3 — الحساب من خدمة المجال دائماً) ══════

public sealed class WeightPreviewUseCase
{
    private readonly ISalesRepository _sales;
    private readonly PosSettingsReader _settings;
    private readonly IUnitCatalog _units;
    private readonly PosScopeResolver _scope;

    public WeightPreviewUseCase(ISalesRepository sales, PosSettingsReader settings, IUnitCatalog units, PosScopeResolver scope)
    {
        _sales = sales;
        _settings = settings;
        _units = units;
        _scope = scope;
    }

    public async Task<Result<Contracts.V1.WeightPreviewResponse>> ExecuteAsync(
        Contracts.V1.WeightPreviewRequest request, CancellationToken ct = default)
    {
        var scope = await _scope.ResolveAsync(ct);
        if (!scope.IsSuccess)
        {
            return Result.Fail<Contracts.V1.WeightPreviewResponse>(scope.Error!);
        }
        var warehouseId = scope.Value!.WarehouseId;
        var data = await _sales.GetSaleItemDataAsync(request.SkuId, warehouseId, null, ct);
        if (data is null)
        {
            return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("SKU_NOT_FOUND", "صيغة البيع غير موجودة"));
        }
        if (data.SaleMode != SaleModes.Weight)
        {
            return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("NOT_A_WEIGHT_ITEM", "الصنف ليس وزنياً"));
        }
        var settings = await _settings.ReadAsync(ct);
        var policy = SaleLineResolver.BuildWeightPolicy(data, settings.Weighing);
        var graph = (await _units.LoadGraphAsync(ct)).Value!;
        decimal pricePerKg = SaleLineResolver.UnitPriceInKg(data, graph);

        switch (request.Mode)
        {
            case WeightSources.ScaleReading:
            {
                if (request.ScaleReadingKg is not { } reading)
                {
                    return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("SCALE_READING_REQUIRED", "قراءة الميزان مطلوبة"));
                }
                var check = WeightSaleRules.AcceptScaleReading(
                    new ScaleReading(
                        reading, request.ScaleStable ?? false, request.ScaleConnected ?? false,
                        request.ScaleReadAtUtc ?? DateTimeOffset.MinValue, request.TareKg),
                    policy, DateTimeOffset.UtcNow);
                if (!check.IsSuccess)
                {
                    return Fail(check.Error!, pricePerKg);
                }
                return Ok(check.Value!, WeightSources.ScaleReading, pricePerKg, null, null, settings);
            }
            case WeightSources.ManualWeight:
            {
                if (request.WeightKg is not { } manual)
                {
                    return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("WEIGHT_REQUIRED", "الوزن اليدوي مطلوب"));
                }
                var check = WeightSaleRules.AcceptManualWeight(manual, policy, policy.AllowManualWeight);
                return check.IsSuccess
                    ? Ok(check.Value!, WeightSources.ManualWeight, pricePerKg, null, null, settings)
                    : Fail(check.Error!, pricePerKg);
            }
            case WeightSources.QuickPick:
            {
                if (request.WeightKg is not { } quick)
                {
                    return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("WEIGHT_REQUIRED", "الوزن السريع مطلوب"));
                }
                var check = WeightSaleRules.AcceptWeightLabel(quick, policy);
                return check.IsSuccess
                    ? Ok(check.Value!, WeightSources.QuickPick, pricePerKg, null, null, settings)
                    : Fail(check.Error!, pricePerKg);
            }
            case WeightSources.AmountDerived:
            {
                if (request.AmountTarget is not { } target)
                {
                    return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("AMOUNT_TARGET_REQUIRED", "المبلغ المستهدف مطلوب"));
                }
                var calc = WeightSaleRules.AcceptAmountTarget(pricePerKg, target, policy, settings.Weighing);
                if (!calc.IsSuccess)
                {
                    return Fail(calc.Error!, pricePerKg);
                }
                var c = calc.Value!;
                return Ok(c.DisplayWeightGrams / 1000m, WeightSources.AmountDerived, pricePerKg,
                    c.ExactWeightGrams, c.DisplayWeightGrams, settings);
            }
            default:
                return Result.Fail<Contracts.V1.WeightPreviewResponse>(new("WEIGHT_MODE_UNKNOWN", "طريقة وزن غير معروفة"));
        }
    }

    private static Result<Contracts.V1.WeightPreviewResponse> Ok(
        decimal weightKg, string source, decimal pricePerKg, decimal? exactG, decimal? displayG, PosSettings settings)
    {
        decimal amount = WeightCalculator.AmountFromWeight(pricePerKg, weightKg * 1000m, settings.Weighing);
        return Result.Ok(new Contracts.V1.WeightPreviewResponse(
            true, weightKg, source, exactG, displayG, pricePerKg, amount, null, null));
    }

    private static Result<Contracts.V1.WeightPreviewResponse> Fail(DomainError error, decimal pricePerKg) =>
        Result.Ok(new Contracts.V1.WeightPreviewResponse(
            false, 0m, string.Empty, null, null, pricePerKg, 0m, error.MessageAr, error.Code));
}

// ═══════════════ عرض الفواتير (لمسار المرتجع والإيصال وسجل الوردية) ═════════════

public sealed class GetSaleUseCase
{
    private readonly ISalesRepository _sales;
    private readonly ICurrentUser _user;

    public GetSaleUseCase(ISalesRepository sales, ICurrentUser user)
    {
        _sales = sales;
        _user = user;
    }

    public async Task<Result<Contracts.V1.PosSaleDetailResponse>> ExecuteAsync(long invoiceId, CancellationToken ct = default)
    {
        var sale = await _sales.GetSaleAsync(invoiceId, ct);
        return sale is null
            ? Result.Fail<Contracts.V1.PosSaleDetailResponse>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"))
            : Result.Ok(ToDto(sale));
    }

    public async Task<Result<Contracts.V1.PosSaleListResponse>> ListRecentAsync(int limit, long? shiftId, CancellationToken ct = default)
    {
        if (_user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.PosSaleListResponse>(new("STATION_REQUIRED", "سجل الفواتير يتطلب جلسة محطة"));
        }
        var list = await _sales.ListRecentSalesAsync(stationId, shiftId, Math.Clamp(limit, 1, 50), ct);
        return Result.Ok(new Contracts.V1.PosSaleListResponse(list.Select(s => ToDto(s)).ToList()));
    }

    private static Contracts.V1.PosSaleSummaryDto ToDto(in SaleSummaryData s) => new(
        s.InvoiceId, s.InvoiceNumber, s.PostedAtUtc, s.Status, s.NetTotal, s.TaxTotal, s.DueAmount,
        s.CustomerNameAr, s.LineCount);

    private static Contracts.V1.PosSaleDetailResponse ToDto(in SaleDetailData s) => new(
        s.InvoiceId, s.InvoiceNumber, s.Status, s.PostedAtUtc, s.CustomerId, s.CustomerNameAr, s.ShiftId,
        s.CashierDisplayAr,
        new Contracts.V1.SaleTotalsDto(
            s.GrossTotal, s.LineDiscountTotal, s.InvoiceDiscount, s.TaxTotal, s.NetTotal,
            s.DueAmount, s.PaidTotal, 0m),
        s.ReturnedTotal,
        s.Lines.Select(l => new Contracts.V1.PosPreviewLine(
            l.LineNo, l.SkuId, l.SkuCode, l.ProductNameAr, l.SaleMode, l.Qty, l.UomCode,
            l.WeightKg, l.WeightSource, l.UnitPrice, l.LineDiscount, l.InvoiceDiscountShare,
            l.TaxAmount, l.NetAmount, l.NetAmount + l.TaxAmount)).ToList(),
        s.Payments.Select(p => new Contracts.V1.PosPaymentDto(
            p.Method, p.Amount, p.Status, p.ProviderName, p.ProviderReference)).ToList(),
        CanReturn: s.Status is "POSTED" or "PARTIAL_RETURN");
}
