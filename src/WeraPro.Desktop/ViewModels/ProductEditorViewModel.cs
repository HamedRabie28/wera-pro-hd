// ViewModels/ProductEditorViewModel.cs — WERA PRO HD Desktop (G4)
// محرر منتج متعدد الأقسام (PAGE-10): هوية · صيغ البيع · وحدات ووزن (D-07 من
// الخادم) · باركود/PLU · أسعار وضرائب · عبوات — ثم مراجعة إلزامية قبل الحفظ
// (لا حفظ جزئي: الطلب ذرّي على الخادم، والحفظ معطّل حتى اعتماد المراجعة §4.4).
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>مسودة صيغة بيع داخل المحرر — صيغة صريحة لا تُستنتج (§4.2).</summary>
public sealed partial class SkuDraft : ObservableObject
{
    public int Ordinal { get; set; }

    [ObservableProperty]
    private string _skuCode = string.Empty;

    [ObservableProperty]
    private string _nameAr = string.Empty;

    /// <summary>0=COUNT · 1=WEIGHT · 2=PACK · 3=SERIALIZED.</summary>
    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private int _baseUnitIndex;

    [ObservableProperty]
    private int _saleUnitIndex;

    // إعدادات الوزن — للصيغة الوزنية حصراً (§4.4 هـ)
    [ObservableProperty]
    private string _weightPrecision = "1";

    [ObservableProperty]
    private string _weightStep = "5";

    [ObservableProperty]
    private string _minWeightKg = string.Empty;

    [ObservableProperty]
    private string _maxWeightKg = string.Empty;

    // الباركود وPLU
    [ObservableProperty]
    private string _barcodeValue = string.Empty;

    /// <summary>0=EAN13 · 1=CODE128 · 2=INTERNAL.</summary>
    [ObservableProperty]
    private int _barcodeTypeIndex;

    [ObservableProperty]
    private string _pluCode = string.Empty;

    // السعر (مستقل لكل صيغة — §4.3 ق4)
    [ObservableProperty]
    private string _unitPrice = string.Empty;

    /// <summary>0=TAXABLE · 1=ZERO_RATED · 2=EXEMPT.</summary>
    [ObservableProperty]
    private int _taxIndex;

    public bool IsWeight => ModeIndex == 1;
    public bool IsPack => ModeIndex == 2;
    public bool IsSerialized => ModeIndex == 3;

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWeight));
        OnPropertyChanged(nameof(IsPack));
        OnPropertyChanged(nameof(IsSerialized));
    }
}

/// <summary>مسودة تعريف عبوة — ربط صيغة العلبة بصيغة المحتوى بعدد وحدات صريح.</summary>
public sealed partial class PackageDraft : ObservableObject
{
    /// <summary>ترتيب صيغة العبوة داخل قائمة الصيغ (PACK).</summary>
    [ObservableProperty]
    private int _packageSkuOrdinal;

    /// <summary>ترتيب صيغة المحتوى (المفردة).</summary>
    [ObservableProperty]
    private int _contentSkuOrdinal;

    [ObservableProperty]
    private string _unitsPerPackage = "12";
}

/// <summary>
/// نموذج محرر المنتج: كل قسم يعمل على مسودات في الذاكرة، ولا يمسّ الخادم إلا
/// عند «اعتماد وحفظ» بعد المراجعة — طلب واحد ذرّي يبني كل الأقسام أو لا شيء.
/// </summary>
public sealed partial class ProductEditorViewModel : ObservableObject
{
    private readonly WeraApiClient _api;

    public static readonly string[] SaleModeNames = ["عددي (قطعة/كيس)", "وزني متغير", "عبوة بعامل معروف", "سيريال لكل وحدة"];
    public static readonly string[] SaleModeCodes = ["COUNT", "WEIGHT", "PACK", "SERIALIZED"];
    public static readonly string[] BarcodeTypeNames = ["EAN-13", "CODE128", "داخلي"];
    public static readonly string[] BarcodeTypeCodes = ["EAN13", "CODE128", "INTERNAL"];
    public static readonly string[] TaxNames = ["خاضع للضريبة (شامل 14%)", "معفاة بسعر صفر", "معفاة"];
    public static readonly string[] TaxCodes = ["TAXABLE", "ZERO_RATED", "EXEMPT"];

    // ── قسم 1: الهوية ──
    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _nameAr = string.Empty;

    [ObservableProperty]
    private string _nameEn = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private int _categoryIndex = -1;

    [ObservableProperty]
    private bool _trackLots;

    [ObservableProperty]
    private bool _trackSerials;

    // ── مرجع التسميات من الخادم ──
    public ObservableCollection<string> CategoryLabels { get; } = [];
    public ObservableCollection<string> UnitLabels { get; } = [];

    private List<CategoryDto> _categories = [];
    private List<UnitDto> _units = [];
    private long _defaultPriceListId;

    public ObservableCollection<SkuDraft> Skus { get; } = [];

    public ObservableCollection<PackageDraft> Packages { get; } = [];

    // ── قسم المراجعة والحفظ ──
    [ObservableProperty]
    private bool _reviewApproved;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string? _successAr;

    public ObservableCollection<string> ReviewLines { get; } = [];

    /// <summary>الحفظ معطّل حتى اعتماد المراجعة (إلزامي §4.4) وأثناء الإرسال.</summary>
    public bool CanSave => ReviewApproved && !IsSaving && Skus.Count > 0;

    public event Action<ProductDto>? ProductCreated;

    public ProductEditorViewModel(WeraApiClient api)
    {
        _api = api;
        Skus.Add(new SkuDraft { Ordinal = 0 });
    }

    /// <summary>تحميل المرجع (فئات/وحدات/قوائم) من الخادم — عند فتح المحرر.</summary>
    public async Task LoadReferenceAsync(CancellationToken ct)
    {
        ErrorAr = null;
        var units = await _api.GetUnitsAsync(ct);
        if (!units.Ok || units.Value is null)
        {
            ErrorAr = units.ErrorAr ?? "تعذر تحميل الوحدات";
            return;
        }
        _units = [.. units.Value.Units];
        UnitLabels.Clear();
        foreach (var u in _units)
        {
            UnitLabels.Add($"{u.Code} — {u.NameAr} ({u.Dimension})");
        }

        var reference = await _api.GetCatalogReferenceAsync(ct);
        if (!reference.Ok || reference.Value is null)
        {
            ErrorAr = reference.ErrorAr ?? "تعذر تحميل الفئات";
            return;
        }
        _categories = [.. reference.Value.Categories];
        CategoryLabels.Clear();
        foreach (var c in _categories)
        {
            CategoryLabels.Add(c.NameAr);
        }
        _defaultPriceListId = reference.Value.PriceLists.FirstOrDefault(p => p.IsDefault)?.PriceListId
            ?? reference.Value.PriceLists.FirstOrDefault()?.PriceListId ?? 0;
    }

    [RelayCommand]
    private void AddSku() => Skus.Add(new SkuDraft { Ordinal = Skus.Count });

    [RelayCommand]
    private void RemoveSku(SkuDraft draft)
    {
        if (Skus.Count > 1)
        {
            Skus.Remove(draft);
            RebuildOrdinals();
        }
    }

    [RelayCommand]
    private void AddPackage() => Packages.Add(new PackageDraft());

    [RelayCommand]
    private void RemovePackage(PackageDraft draft) => Packages.Remove(draft);

    private void RebuildOrdinals()
    {
        for (var i = 0; i < Skus.Count; i++)
        {
            Skus[i].Ordinal = i;
        }
    }

    /// <summary>بناء أسطر المراجعة من المسودات — تُعرض كاملة قبل السماح بالحفظ.</summary>
    [RelayCommand]
    public void BuildReview()
    {
        ReviewLines.Clear();
        ReviewLines.Add($"• الهوية: {Code} — {NameAr}" + (string.IsNullOrWhiteSpace(NameEn) ? "" : $" ({NameEn})"));
        ReviewLines.Add($"• الفئة: {(CategoryIndex >= 0 && CategoryIndex < _categories.Count ? _categories[CategoryIndex].NameAr : "غير محددة!")}");
        ReviewLines.Add($"• تتبع الدفعات: {(TrackLots ? "نعم" : "لا")} · تتبع السيريال: {(TrackSerials ? "نعم" : "لا")}");

        foreach (var sku in Skus)
        {
            var mode = SaleModeNames[sku.ModeIndex];
            var baseUnit = UnitLabel(sku.BaseUnitIndex);
            var saleUnit = UnitLabel(sku.SaleUnitIndex);
            ReviewLines.Add($"• صيغة «{sku.SkuCode}» {(string.IsNullOrWhiteSpace(sku.NameAr) ? "" : $"({sku.NameAr})")}: {mode} · أساس {baseUnit} · بيع {saleUnit}");
            if (sku.ModeIndex == 1)
            {
                ReviewLines.Add($"    وزن: دقة {sku.WeightPrecision}g · خطوة {sku.WeightStep}g (D-07 من الخادم عند الحفظ الفعلي)");
            }
            if (!string.IsNullOrWhiteSpace(sku.BarcodeValue))
            {
                ReviewLines.Add($"    باركود: {sku.BarcodeValue} ({BarcodeTypeNames[sku.BarcodeTypeIndex]})");
            }
            if (!string.IsNullOrWhiteSpace(sku.PluCode))
            {
                ReviewLines.Add($"    PLU: {sku.PluCode}");
            }
            ReviewLines.Add($"    سعر مستقل: {sku.UnitPrice} ج.م · {TaxNames[sku.TaxIndex]}");
        }

        foreach (var pkg in Packages)
        {
            var packageSku = Skus.FirstOrDefault(s => s.Ordinal == pkg.PackageSkuOrdinal);
            var contentSku = Skus.FirstOrDefault(s => s.Ordinal == pkg.ContentSkuOrdinal);
            ReviewLines.Add($"• عبوة: «{packageSku?.SkuCode ?? "؟"}» تحتوي {pkg.UnitsPerPackage} × «{contentSku?.SkuCode ?? "؟"}» — أثر مخزون واحد على المحتوى");
        }

        ReviewLines.Add("— الحفظ ذرّي: كل الأقسام أو لا شيء (لا حفظ جزئي)، ويُوثَّق في سجل التدقيق بصلاحيتك.");
        ReviewApproved = false;
        OnPropertyChanged(nameof(CanSave));
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct)
    {
        if (!CanSave)
        {
            return;
        }

        ErrorAr = null;
        IsSaving = true;
        OnPropertyChanged(nameof(CanSave));
        try
        {
            var request = BuildRequest();
            var result = await _api.CreateProductAsync(request, ct);
            if (!result.Ok || result.Value is null)
            {
                ErrorAr = result.ErrorAr ?? "تعذر حفظ المنتج";
                return;
            }
            SuccessAr = $"حُفظ المنتج {result.Value.Code} بصيغه كلها في معاملة واحدة (معرّف {result.Value.ProductId})";
            ProductCreated?.Invoke(result.Value);
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    partial void OnReviewApprovedChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    private string UnitLabel(int index) =>
        index >= 0 && index < _units.Count ? _units[index].Code : "؟";

    /// <summary>يبني طلب الإنشاء الكامل من المسودات — الخادم يعيد التحقق في كل شيء.</summary>
    private CreateProductRequest BuildRequest()
    {
        var skus = new List<SkuUpsertRequest>();
        var barcodes = new List<BarcodeUpsertRequest>();
        var plus = new List<PluUpsertRequest>();
        var prices = new List<PriceUpsertRequest>();
        var packages = new List<PackageUpsertRequest>();

        foreach (var sku in Skus)
        {
            var mode = SaleModeCodes[sku.ModeIndex];
            var weight = sku.ModeIndex == 1
                ? new SkuWeightSettings(
                    WeightPrecisionG: int.TryParse(sku.WeightPrecision, out var precision) ? precision : null,
                    WeightStepG: int.TryParse(sku.WeightStep, out var step) ? step : null,
                    AllowManualWeight: true,
                    AllowAmountTarget: true,
                    MinWeightKg: decimal.TryParse(sku.MinWeightKg, out var min) ? min : null,
                    MaxWeightKg: decimal.TryParse(sku.MaxWeightKg, out var max) ? max : null,
                    DefaultTareKg: 0m)
                : null;

            skus.Add(new SkuUpsertRequest(
                sku.SkuCode.Trim(),
                mode,
                _units[sku.BaseUnitIndex].UomId,
                _units[sku.SaleUnitIndex].UomId,
                string.IsNullOrWhiteSpace(sku.NameAr) ? null : sku.NameAr.Trim(),
                weight,
                ReorderLevel: 0m,
                AllowNegative: null));

            if (!string.IsNullOrWhiteSpace(sku.BarcodeValue))
            {
                barcodes.Add(new BarcodeUpsertRequest(
                    sku.BarcodeValue.Trim(), BarcodeTypeCodes[sku.BarcodeTypeIndex], IsPrintedLabel: false, SkuOrdinal: sku.Ordinal));
            }
            if (!string.IsNullOrWhiteSpace(sku.PluCode))
            {
                plus.Add(new PluUpsertRequest(sku.PluCode.Trim(), sku.Ordinal));
            }
            if (decimal.TryParse(sku.UnitPrice, out var price))
            {
                prices.Add(new PriceUpsertRequest(
                    sku.Ordinal, _defaultPriceListId, _units[sku.BaseUnitIndex].UomId, price, TaxCodes[sku.TaxIndex]));
            }
        }

        foreach (var pkg in Packages)
        {
            if (decimal.TryParse(pkg.UnitsPerPackage, out var units))
            {
                packages.Add(new PackageUpsertRequest(pkg.PackageSkuOrdinal, pkg.ContentSkuOrdinal, units, OpenPackageAllowed: true));
            }
        }

        return new CreateProductRequest(
            Code.Trim(),
            NameAr.Trim(),
            string.IsNullOrWhiteSpace(NameEn) ? null : NameEn.Trim(),
            _categories[CategoryIndex].CategoryId,
            BrandId: null,
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            TrackLots,
            TrackSerials,
            skus,
            barcodes,
            plus,
            prices,
            packages);
    }
}
