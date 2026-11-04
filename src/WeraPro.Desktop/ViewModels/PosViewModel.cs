// ViewModels/PosViewModel.cs — WERA PRO HD Desktop (G7)
// نموذج عرض نقطة البيع الكامل (§9): سلة حقيقية من الخادم، معاينة تسعير خادمية
// (§9.0 ق10 — لا حسابات مالية في ViewModel)، تعليق/استعادة، مرتجع، وردية، ودرج.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>سطر سلة العرض — نسخة عرض فقط؛ التسعير يعاد من الخادم عند كل معاينة.</summary>
public sealed partial class CartLineViewModel : ObservableObject
{
    public required long SkuId { get; init; }
    public required string SkuCode { get; init; }
    public required string ProductNameAr { get; init; }
    public required string SaleMode { get; init; }
    public required string UomCode { get; init; }
    public required decimal UnitPrice { get; init; }
    public required string? ImageUrl { get; init; }
    public required bool IsWeight { get; init; }
    public required WeightPolicyDto? WeightPolicy { get; init; }

    [ObservableProperty]
    private decimal _qty = 1m;                    // للعددي: الكمية · للوزني: تعاد من الخادم

    [ObservableProperty]
    private decimal? _weightKg;

    [ObservableProperty]
    private string? _weightSource;

    [ObservableProperty]
    private decimal? _amountTarget;

    [ObservableProperty]
    private decimal? _lineDiscount;

    /// <summary>إجمالي السطر من آخر معاينة خادمية (لا يُحسب محلياً أبداً).</summary>
    [ObservableProperty]
    private decimal _serverLineTotal;

    public string DisplayQty => IsWeight
        ? $"{WeightKg:0.###} كجم{(WeightSource is null ? "" : $" ({WeightSourceAr})")}"
        : $"{Qty:0.######} {UomCode}";

    public string WeightSourceAr => WeightSource switch
    {
        "SCALE_READING" => "ميزان",
        "MANUAL_WEIGHT" => "يدوي",
        "AMOUNT_DERIVED" => "من مبلغ",
        "WEIGHT_LABEL" => "ملصق",
        "QUICK_PICK" => "سريع",
        "PACK_PREWEIGHED" => "عبوة معدة",
        _ => WeightSource ?? string.Empty,
    };

    public PosLineRequest ToRequest() => new(
        SkuId, Qty, UomId: null, WeightKg, WeightSource, AmountTarget, TareKg: null,
        LineDiscount, PriceOverride: null, PriceOverrideReason: null, Notes: null);

    partial void OnWeightKgChanged(decimal? value) => OnPropertyChanged(nameof(DisplayQty));
    partial void OnWeightSourceChanged(string? value) => OnPropertyChanged(nameof(DisplayQty));
    partial void OnQtyChanged(decimal value) => OnPropertyChanged(nameof(DisplayQty));
}

/// <summary>
/// نموذج POS الكامل (G7 · §9): كل أمر يمر على الخادم عبر Use Cases — الواجهة ليست
/// مصدر الحقيقة للأسعار أو الأرصدة أو الصلاحيات (§9.0 ق10)، والمعاينة تُعاد بعد
/// كل تغيير بسلة (Debounce خفيف عبر إلغاء المعاينة السابقة).
/// </summary>
public sealed partial class PosViewModel : ObservableObject
{
    private readonly SessionService _session;
    private readonly WeraApiClient _api;
    private CancellationTokenSource _previewCts = new();

    public PosViewModel(SessionService session, WeraApiClient api)
    {
        _session = session;
        _api = api;
    }

    public string CashierDisplayAr => _session.User?.DisplayNameAr ?? "—";
    public string StationDisplayAr => _session.StationNameAr ?? "محطة غير محددة";
    public string ScopeDisplayAr => _session.BranchNameAr ?? "كل الفروع";

    [ObservableProperty]
    private string _statusAr = "جاهز";

    [ObservableProperty]
    private string _contextInfoAr = "سياق المحطة يُجلب من الخادم…";

    [ObservableProperty]
    private string _shiftInfoAr = "لا وردية";

    [ObservableProperty]
    private string _printerInfoAr = "الإيصال في قائمة الطباعة — طابعة المحطة غير معتمدة (G10)";

    [ObservableProperty]
    private string _cardInfoAr = "البطاقة معطلة حتى اعتماد مزود (B-02/D-11)";

    [ObservableProperty]
    private PosContextResponse? _context;

    [ObservableProperty]
    private SaleTotalsDto? _totals;                // من الخادم فقط

    [ObservableProperty]
    private PosItemDto? _selectedDetails;

    [ObservableProperty]
    private long? _customerId;

    [ObservableProperty]
    private string? _customerNameAr;

    [ObservableProperty]
    private string? _lastResultAr;

    /// <summary>سطر السلة قيد التحرير (إطار Teal حسب POS_VISUAL_SPEC §2).</summary>
    [ObservableProperty]
    private CartLineViewModel? _activeLine;

    public ObservableCollection<CartLineViewModel> Cart { get; } = [];

    // ── التهيئة (سياق المحطة + الوردية + الطابعة/البطاقة الصادقة) ───────────

    public async Task LoadFoundationAsync(CancellationToken ct)
    {
        var settings = await _api.GetSettingsAsync("WEIGHT", ct);
        var weighingInfo = settings.Ok && settings.Value is not null
            ? string.Join(" · ", settings.Value.Select(s => $"{s.SettingKey}={s.ValueText}"))
            : "تعذر جلب إعدادات الوزن";

        var context = await _api.GetPosContextAsync(ct);
        if (context.Ok && context.Value is not null)
        {
            Context = context.Value;
            ShiftInfoAr = context.Value.ShiftId is null
                ? "لا وردية مفتوحة — افتح وردية (F11 نافذة الوردية)"
                : $"وردية #{context.Value.ShiftNumber} · افتتاحي {context.Value.ShiftOpeningFloat:0.00}";
            PrinterInfoAr = context.Value.ReceiptPrinter.StatusTextAr;
            CardInfoAr = context.Value.Card.NoticeAr ?? CardInfoAr;
            ContextInfoAr =
                $"{context.Value.BranchNameAr} · {context.Value.StationNameAr} · ضريبة {context.Value.TaxStandardRate:P1} شاملة · " +
                $"حد خصمك {(context.Value.DiscountLimits.CashierPct == 0 ? "—" : $"{context.Value.DiscountLimits.CashierPct:0.#}%")} · {weighingInfo}";
        }
        else
        {
            ContextInfoAr = "تعذر جلب سياق المحطة — " + (context.ErrorAr ?? "سبب غير معروف");
        }
    }

    // ── الالتقاط (باركود Exact Match / بحث) ──────────────────────────────────

    /// <summary>التقاط بالباركود/الكود — وزني يفتح نافذة الوزن، ملصق الوزن يضيف مباشرة (§5.3).</summary>
    public async Task<bool> CaptureAsync(string code, Func<CartLineViewModel, Task<bool>> openWeightWindow, CancellationToken ct)
    {
        var lookup = await _api.PosLookupAsync(code, ct);
        if (!lookup.Ok || lookup.Value is null)
        {
            StatusAr = lookup.ErrorAr ?? "تعذر الالتقاط";
            return false;
        }
        var result = lookup.Value;
        if (!result.Found || result.Item is null)
        {
            StatusAr = result.ErrorAr ?? "لا صنف بهذا الكود — استخدم البحث F2";
            return false;
        }
        var item = result.Item;

        // ملصق وزن محلول: يضاف مباشرة بوزنه المشفر ومصدره WEIGHT_LABEL (§9.3 ق5)
        if (result.IsWeightLabel && result.LabelWeightKg is { } labelKg)
        {
            AddLine(item, weightKg: labelKg, weightSource: "WEIGHT_LABEL");
            StatusAr = $"أُضيف {item.ProductNameAr} — {labelKg:0.###} كجم (ملصق {result.LabelProfileName})";
            return true;
        }

        if (item.SaleMode == "WEIGHT")
        {
            // صنف وزني بلا وزن في الباركود → نافذة الوزن بالطرق الست (§5.2)
            var line = NewLine(item);
            var accepted = await openWeightWindow(line);
            if (!accepted)
            {
                StatusAr = "أُلغيت إضافة الصنف الوزني";
                return false;
            }
            Cart.Add(line);
            ActiveLine = line;
            await PreviewAsync(ct);
            return true;
        }

        // عددي/عبوة: زيادة الكمية إن كان السطر موجوداً (مسح متتابع §9.12)
        var existing = Cart.FirstOrDefault(l => l.SkuId == item.SkuId && l.WeightSource is null);
        if (existing is not null)
        {
            existing.Qty += 1m;
            ActiveLine = existing;
        }
        else
        {
            var line = NewLine(item);
            Cart.Add(line);
            ActiveLine = line;
        }
        SelectedDetails = item;
        StatusAr = $"أُضيف {item.ProductNameAr} — متاح {item.AvailableQty:0.###} {item.AvailableUnitCode}";
        await PreviewAsync(ct);
        return true;
    }

    private CartLineViewModel NewLine(PosItemDto item) => new()
    {
        SkuId = item.SkuId,
        SkuCode = item.SkuCode,
        ProductNameAr = item.ProductNameAr,
        SaleMode = item.SaleMode,
        UomCode = item.PriceUnitCode,
        UnitPrice = item.UnitPrice,
        ImageUrl = item.ImageUrl,
        IsWeight = item.SaleMode == "WEIGHT",
        WeightPolicy = item.Weight,
    };

    private void AddLine(PosItemDto item, decimal weightKg, string weightSource)
    {
        var line = NewLine(item);
        line.WeightKg = weightKg;
        line.WeightSource = weightSource;
        Cart.Add(line);
        ActiveLine = line;
    }

    // ── تحرير السلة (§9.2) ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RemoveActiveAsync()
    {
        if (ActiveLine is null)
        {
            StatusAr = "لا سطر محدد";
            return;
        }
        Cart.Remove(ActiveLine);
        ActiveLine = Cart.LastOrDefault();
        await PreviewAsync();
    }

    [RelayCommand]
    private async Task IncreaseActiveAsync()
    {
        if (ActiveLine is { IsWeight: false })
        {
            ActiveLine.Qty += 1m;
            await PreviewAsync();
        }
    }

    [RelayCommand]
    private async Task DecreaseActiveAsync()
    {
        if (ActiveLine is { IsWeight: false } line)
        {
            line.Qty = line.Qty > 1m ? line.Qty - 1m : 1m;
            await PreviewAsync();
        }
    }

    /// <summary>خصم سطر بقيمة — يخضع لحد الدور خادمياً (D-06)؛ الرفض يظهر برسالة الخادم.</summary>
    [RelayCommand]
    private async Task DiscountActiveAsync(decimal amount)
    {
        if (ActiveLine is null)
        {
            return;
        }
        ActiveLine.LineDiscount = ActiveLine.LineDiscount is null ? amount : ActiveLine.LineDiscount + amount;
        await PreviewAsync();
    }

    public async Task SetActiveWeightAsync(decimal weightKg, string source, decimal? amountTarget, CancellationToken ct = default)
    {
        if (ActiveLine is null)
        {
            return;
        }
        ActiveLine.WeightKg = weightKg;
        ActiveLine.WeightSource = source;
        ActiveLine.AmountTarget = amountTarget;
        await PreviewAsync(ct);
    }

    // ── المعاينة الخادمية (المجاميع من الخادم فقط §9.1F) ─────────────────────

    public async Task PreviewAsync(CancellationToken ct = default)
    {
        _previewCts.Cancel();
        _previewCts.Dispose();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (Cart.Count == 0)
        {
            Totals = null;
            return;
        }
        var request = new PosPreviewRequest(
            [.. Cart.Select(l => l.ToRequest())],
            CustomerId,
            InvoiceDiscount: null,
            InvoiceDiscountReason: null);
        var preview = await _api.PosPreviewAsync(request, _previewCts.Token);
        if (preview.Ok && preview.Value is not null)
        {
            Totals = preview.Value.Totals;
            for (var i = 0; i < Math.Min(Cart.Count, preview.Value.Lines.Count); i++)
            {
                Cart[i].ServerLineTotal = preview.Value.Lines[i].LineTotal;
            }
            StatusAr = "معاينة محدثة من الخادم";
        }
        else
        {
            StatusAr = preview.ErrorAr ?? "تعذرت المعاينة";
        }
    }

    // ── البيع (يبني الطلب من السلة؛ الدفع في نافذة الدفع §9.6) ───────────────

    public PostSaleRequest BuildSaleRequest(IReadOnlyList<PosPaymentRequest> payments, decimal? cashTendered)
        => new(
            [.. Cart.Select(l => l.ToRequest())],
            payments,
            CustomerId,
            InvoiceDiscount: null,
            InvoiceDiscountReason: null,
            cashTendered,
            IdempotencyKey: $"pos-{Guid.NewGuid():N}",
            ApprovalId: null);

    public async Task<(bool Ok, PostedSaleResponse? Sale, string? ErrorAr)> PostSaleAsync(
        IReadOnlyList<PosPaymentRequest> payments, decimal? cashTendered, CancellationToken ct = default)
    {
        var request = BuildSaleRequest(payments, cashTendered);
        var result = await _api.PostSaleAsync(request, ct);
        if (result.Ok && result.Value is not null)
        {
            LastResultAr =
                $"فورة {result.Value.InvoiceNumber} · مستحق {result.Value.Totals.DueAmount:0.00} · " +
                $"إيصال: {result.Value.Receipt.NoticeAr}";
            StatusAr = LastResultAr;
            return (true, result.Value, null);
        }
        return (false, null, result.ErrorAr ?? "تعذر ترحيل البيع");
    }

    /// <summary>تفريغ السلة بعد ترحيل ناجح فقط (§9.7 ق12).</summary>
    public void ResetCartAfterSuccessfulPost()
    {
        Cart.Clear();
        ActiveLine = null;
        Totals = null;
        SelectedDetails = null;
    }

    // ── لقطة السلة للتعليق/الاستعادة (§9.5) ──────────────────────────────────

    /// <summary>وردية مفتوحة للمحطة (من سياق آخر جلب أو استعلام مباشر).</summary>
    [ObservableProperty]
    private bool _hasOpenShift;

    /// <summary>نص ملخص الوردية للعرض (فتح/مجموعات) — الإغلاق التام في G8.</summary>
    public async Task<string> LoadCurrentShiftTextAsync(CancellationToken ct = default)
    {
        var current = await _api.GetCurrentShiftAsync(ct);
        if (current.Ok && current.Value is { } shift)
        {
            HasOpenShift = true;
            ShiftInfoAr = $"وردية #{shift.ShiftNumber} · افتتاحي {shift.OpeningFloat:0.00}";
            var summary = await _api.GetShiftSummaryAsync(shift.ShiftId, ct);
            if (summary.Ok && summary.Value is { } sum)
            {
                return
                    $"وردية #{shift.ShiftNumber} مفتوحة منذ {shift.OpenedAtUtc.LocalDateTime:HH:mm} · فواتير {sum.InvoiceCount} · " +
                    $"صافي {sum.NetSales:0.00} · نقدي {sum.CashCollected:0.00} · بطاقة {sum.CardCollected:0.00} · " +
                    $"آجل {sum.CreditIssued:0.00} · مرتجعات {sum.ReturnsTotal:0.00}\n" +
                    $"المتوقع في الدرج (افتتاحي + نقدي − مرتجعات نقدية): {sum.ExpectedCashDrawer:0.00} ج.م";
            }
            return ShiftInfoAr;
        }
        HasOpenShift = false;
        return "لا وردية مفتوحة للمحطة — افتح وردية قبل ملخصها (§9.9)";
    }

    public string BuildCartSnapshot()
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            customerId = CustomerId,
            customerName = CustomerNameAr,
            lines = Cart.Select(l => new
            {
                skuId = l.SkuId, skuCode = l.SkuCode, name = l.ProductNameAr,
                qty = l.Qty, weightKg = l.WeightKg, weightSource = l.WeightSource,
                amountTarget = l.AmountTarget, lineDiscount = l.LineDiscount,
            }).ToArray(),
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    public async Task HoldAsync(string noteAr, CancellationToken ct = default)
    {
        if (Cart.Count == 0)
        {
            StatusAr = "لا سلة لتعليقها";
            return;
        }
        var result = await _api.HoldCartAsync(BuildCartSnapshot(), noteAr, CustomerId, ct);
        StatusAr = result.Ok ? "عُلقت السلة — استعدها من قائمة المعلقات (F10)" : result.ErrorAr ?? "تعذر التعليق";
        if (result.Ok)
        {
            ResetCartAfterSuccessfulPost();
        }
    }
}
