// ViewModels/CatalogViewModel.cs — WERA PRO HD Desktop (G4)
// جدول الكتالوج المتقدم (PAGE-09): بحث نصي محدود + فلترة صيغة البيع/الحالة +
// ترقيم خادمي دائماً + حالات صدق (تحميل/خطأ/فارغ) — لا بيانات DEV.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>صف جدول الكتالوج: صيغة بيع واحدة لكل صف (سعرها وباركودها مستقلان).</summary>
public sealed class CatalogRow
{
    public long ProductId { get; init; }
    public long SkuId { get; init; }
    public required string ProductName { get; init; }
    public required string SkuCode { get; init; }
    public required string Barcode { get; init; }
    public required string Category { get; init; }
    public required string SaleMode { get; init; }
    public required string Unit { get; init; }
    public required string Price { get; init; }
    public required string Status { get; init; }
}

/// <summary>
/// نموذج عرض الكتالوج (G4 — §4/§7.2.1): كل البيانات من الخادم عبر الترقيم
/// (لا صفحة كاملة أبداً — المقياس ملايين السجلات)، والدولة (تحميل/خطأ/فارغ)
/// معلنة بصدق، والصلاحية الكتابية تُقرأ من جلسة المستخدم (إخفاء الزر ليس
/// حماية — الحماية خادمية §18).
/// </summary>
public sealed partial class CatalogViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public const int PageSize = 25;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>فلاتر صيغة البيع — «الكل» أو صيغة واحدة (خادمية).</summary>
    [ObservableProperty]
    private int _saleModeFilterIndex;

    [ObservableProperty]
    private int _statusFilterIndex;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private long _totalCount;

    public ObservableCollection<CatalogRow> Rows { get; } = [];

    public static readonly string[] SaleModeFilters = ["الكل", "عددي", "وزني", "عبوة", "سيريال"];
    private static readonly string?[] SaleModeCodes = [null, "COUNT", "WEIGHT", "PACK", "SERIALIZED"];

    public static readonly string[] StatusFilters = ["الكل", "نشط", "معطل", "موقوف"];
    private static readonly string?[] StatusCodes = [null, "ACTIVE", "INACTIVE", "DISCONTINUED"];

    public string SummaryAr => TotalCount == 0
        ? "لا نتائج"
        : $"الصفحة {Page} من {TotalPages} · {TotalCount:N0} منتج (حجم الصفحة {PageSize} — ترقيم من الخادم)";

    /// <summary>صلاحية الإنشاء من الجلسة — العرض فقط؛ الفحص الحاسم على الخادم.</summary>
    public bool CanCreate => _session.User?.Permissions.Contains("catalog.product.create") == true;

    public CatalogViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Page = 1;
        await LoadAsync(ct);
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct) => await LoadAsync(ct);

    [RelayCommand]
    public async Task NextPageAsync(CancellationToken ct)
    {
        if (Page < TotalPages)
        {
            Page++;
            await LoadAsync(ct);
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync(CancellationToken ct)
    {
        if (Page > 1)
        {
            Page--;
            await LoadAsync(ct);
        }
    }

    /// <summary>تحميل الصفحة الحالية من الخادم بالفلاتر — كل النداءات async (لا تجميد UI).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        OnPropertyChanged(nameof(SummaryAr));
        try
        {
            var result = await _api.SearchProductsAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                SaleModeCodes[SaleModeFilterIndex],
                StatusCodes[StatusFilterIndex],
                Page, PageSize, ct);

            if (!result.Ok || result.Value is null)
            {
                Rows.Clear();
                ErrorAr = result.ErrorAr ?? "تعذر تحميل الكتالوج";
            }
            else
            {
                var response = result.Value;
                TotalCount = response.TotalCount;
                TotalPages = Math.Max(1, response.TotalPages);
                Page = response.Page;
                Rows.Clear();
                foreach (var product in response.Items)
                {
                    foreach (var sku in product.Skus)
                    {
                        Rows.Add(new CatalogRow
                        {
                            ProductId = product.ProductId,
                            SkuId = sku.SkuId,
                            ProductName = product.NameAr + (sku.NameAr is null ? "" : " — " + sku.NameAr),
                            SkuCode = sku.SkuCode,
                            Barcode = sku.Barcodes.Count > 0 ? sku.Barcodes[0].BarcodeValue : "—",
                            Category = product.CategoryNameAr,
                            SaleMode = sku.SaleMode,
                            Unit = sku.SaleUomCode,
                            Price = sku.Prices.Count > 0
                                ? sku.Prices[0].UnitPrice.ToString("N2") + " ج.م"
                                : "—",
                            Status = product.Status,
                        });
                    }
                }
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SummaryAr));
            OnPropertyChanged(nameof(CanCreate));
        }
    }
}
