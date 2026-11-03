// ViewModels/MediaLibraryViewModel.cs — WERA PRO HD Desktop (G5)
// مكتبة الوسائط (PAGE-16): شبكة أصول بمصغرات حقيقية من الخادم (thumb-catalog 320
// لا الأصل أبداً) + بحث ببادئة البصمة + فلترة النوع/حالة الربط + ترقيم خادمي +
// حالات صدق (تحميل/خطأ/فارغ) + عمليات الربط/فك الربط/الرئيسية/الحذف المحمي
// بالمراجعة/إعادة المعالجة/تقرير المكررات — كل البيانات من الخادم (§6).
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>
/// بطاقة أصل في شبكة المكتبة: بيانات وصفية من الخادم + مصغرة تُحمَّل بتكاسل
/// (thumb-catalog — تصغير فقط لا تكبير §6.1). المصغرة BitmapImage لأنها تُربط
/// مباشرة بـImage في القالب — بايتات فقط لا مسارات محلية.
/// </summary>
public sealed partial class MediaCardRow : ObservableObject
{
    public required MediaDto Source { get; init; }

    public long MediaId => Source.MediaId;
    public string ShaShort => Source.Sha256.Length > 12 ? Source.Sha256[..12] + "…" : Source.Sha256;
    public string DimensionsAr => $"{Source.WidthPx}×{Source.HeightPx}";
    public string SizeAr => (Source.SizeBytes / 1024.0).ToString("N0") + " KB";
    public string MimeAr => Source.MimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? "PNG" : "JPEG";
    public string UploadedAr => Source.UploadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string LinksAr => Source.LinkCount == 0 ? "غير مربوطة" : $"{Source.LinkCount:N0} ربط";
    public bool HasPreview => Source.HasPreview;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _thumb;

    [ObservableProperty]
    private bool _thumbFailed;
}

/// <summary>صف ربط في تفاصيل الأصل المحدد (منتج أو صيغة بيع — أولوية واضحة §6.3).</summary>
public sealed class MediaLinkRow
{
    public required MediaLinkDto Source { get; init; }
    public long LinkId => Source.LinkId;
    public string TargetTypeAr => Source.TargetType == "SKU" ? "صيغة بيع" : "منتج";
    public string Target => Source.TargetCode + " — " + Source.TargetNameAr;
    public string PrimaryAr => Source.IsPrimary ? "رئيسية" : "—";
}

/// <summary>
/// نموذج عرض مكتبة الوسائط: قراءة الجميع (المصغرات تكوين عمل)، والعمليات
/// الكتابية (رفع/ربط/حذف) لمن يملك catalog.media.manage في جلسته — الحماية
/// الحاسمة خادمية (§18)؛ الإخفاء هنا عرض فقط.
/// </summary>
public sealed partial class MediaLibraryViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public const int PageSize = 24;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _shaSearchText = string.Empty;

    [ObservableProperty]
    private int _mimeFilterIndex;

    [ObservableProperty]
    private int _linkFilterIndex;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private long _totalCount;

    [ObservableProperty]
    private MediaCardRow? _selected;

    [ObservableProperty]
    private bool _isLoadingDetails;

    [ObservableProperty]
    private string? _detailsErrorAr;

    /// <summary>رسالة نجاح عابرة (رفع/ربط/حذف) — تُعرض ثم تُمسح عند العمل التالي.</summary>
    [ObservableProperty]
    private string? _successAr;

    public ObservableCollection<MediaCardRow> Cards { get; } = [];
    public ObservableCollection<MediaLinkRow> Links { get; } = [];

    public static readonly string[] MimeFilters = ["النوع: الكل", "JPEG", "PNG"];
    private static readonly string?[] MimeCodes = [null, "image/jpeg", "image/png"];

    public static readonly string[] LinkFilters = ["الربط: الكل", "مربوطة", "غير مربوطة"];
    private static readonly string[] LinkCodes = ["ANY", "LINKED", "UNLINKED"];

    public string SummaryAr => TotalCount == 0
        ? "لا نتائج"
        : $"الصفحة {Page} من {TotalPages} · {TotalCount:N0} أصل (حجم الصفحة {PageSize} — ترقيم من الخادم)";

    public bool CanManage => _session.User?.Permissions.Contains("catalog.media.manage") == true;
    public bool CanImport => _session.User?.Permissions.Contains("catalog.import.run") == true;

    public bool HasSelection => Selected is not null;

    public event Action<long>? PreviewRequested;   // يفتح نافذة المعاينة (preview 1024)
    public event Action? LibraryChanged;           // يليه إعادة تحميل الصفحة الحالية

    public MediaLibraryViewModel(WeraApiClient api, SessionService session)
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

    /// <summary>تحميل صفحة المكتبة بالفلاتر الحالية — الترقيم خادمي دائماً.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        SuccessAr = null;
        OnPropertyChanged(nameof(SummaryAr));
        try
        {
            var sha = ShaSearchText.Trim();
            var result = await _api.SearchMediaAsync(
                sha.Length == 0 ? null : sha,
                MimeCodes[MimeFilterIndex],
                LinkCodes[LinkFilterIndex],
                Page, PageSize, ct);

            if (!result.Ok || result.Value is null)
            {
                Cards.Clear();
                ErrorAr = result.ErrorAr ?? "تعذر تحميل المكتبة";
            }
            else
            {
                var response = result.Value;
                TotalCount = response.TotalCount;
                TotalPages = Math.Max(1, response.TotalPages);
                Page = response.Page;
                Cards.Clear();
                foreach (var item in response.Items)
                {
                    Cards.Add(new MediaCardRow { Source = item });
                }
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(SummaryAr));
            OnPropertyChanged(nameof(CanManage));
            OnPropertyChanged(nameof(CanImport));
        }
    }

    /// <summary>تحميل مصغرة بطاقة — تكاسل: أول من يظهر فقط (لا 24 طلباً دفعة واحدة).</summary>
    public async Task LoadThumbAsync(MediaCardRow card, CancellationToken ct)
    {
        if (card.Thumb is not null || card.ThumbFailed)
        {
            return;
        }
        var fetch = await _api.GetMediaVariantAsync(card.MediaId, "thumb-catalog", ct);
        if (fetch is { Ok: true, Bytes: { Length: > 0 } bytes })
        {
            card.Thumb = await DecodeImageAsync(bytes);
        }
        card.ThumbFailed = card.Thumb is null;
    }

    /// <summary>تحميل تفاصيل الأصل المحدد + روابطه (مرجع مراجعة الحذف §6.2).</summary>
    public async Task LoadDetailsAsync(long mediaId, CancellationToken ct)
    {
        IsLoadingDetails = true;
        DetailsErrorAr = null;
        Links.Clear();
        try
        {
            var result = await _api.GetMediaDetailsAsync(mediaId, ct);
            if (!result.Ok || result.Value is null)
            {
                DetailsErrorAr = result.ErrorAr ?? "تعذر تحميل تفاصيل الأصل";
            }
            else
            {
                foreach (var link in result.Value.Links)
                {
                    Links.Add(new MediaLinkRow { Source = link });
                }
            }
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    /// <summary>رفع ملف واحد — التطابق التام يعيد الإشارة للموجود (لا تكرار §6.1).</summary>
    public async Task UploadAsync(string filePath, CancellationToken ct)
    {
        var result = await _api.UploadMediaAsync(filePath, ct);
        if (result.Ok && result.Value is { } dto)
        {
            SuccessAr = dto.Created
                ? $"رُفعت الصورة (#{dto.Media!.MediaId}) — أبعاد {dto.Media.WidthPx}×{dto.Media.HeightPx}"
                : $"الصورة مطابقة تماماً لأصل قائم (#{dto.ExistingMediaId}) — يُربط الموجود ولا يُنشأ ثانٍ";
            LibraryChanged?.Invoke();
        }
        else
        {
            ErrorAr = result.ErrorAr ?? "تعذر الرفع";
        }
    }

    /// <summary>ربط الأصل المحدد بهدف واحد (منتج أو صيغة بيع — لا الاثنين §6.3).</summary>
    public async Task LinkAsync(long mediaId, long? productId, long? skuId, bool makePrimary, CancellationToken ct)
    {
        var result = await _api.LinkMediaAsync(mediaId, productId, skuId, makePrimary, ct);
        if (result.Ok)
        {
            SuccessAr = "رُبطت الصورة بالهدف";
            await LoadDetailsAsync(mediaId, ct);
            LibraryChanged?.Invoke();
        }
        else
        {
            ErrorAr = result.ErrorAr ?? "تعذر الربط";
        }
    }

    /// <summary>فك ربط — لا يمس الأصل إطلاقاً (إزالة الربط لا تحذف §4.4 حـ).</summary>
    public async Task UnlinkAsync(long mediaId, long linkId, CancellationToken ct)
    {
        var result = await _api.UnlinkMediaAsync(linkId, ct);
        if (result.Ok)
        {
            SuccessAr = $"فُك الربط — بقي {result.Value} ربط للأصل";
            await LoadDetailsAsync(mediaId, ct);
            LibraryChanged?.Invoke();
        }
        else
        {
            ErrorAr = result.ErrorAr ?? "تعذر فك الربط";
        }
    }

    /// <summary>تعيين صورة رئيسية للهدف (تخلي باقي روابط الهدف تلقائياً).</summary>
    public async Task SetPrimaryAsync(long mediaId, long linkId, CancellationToken ct)
    {
        var result = await _api.SetPrimaryLinkAsync(linkId, ct);
        if (result.Ok)
        {
            SuccessAr = "عُينت الصورة الرئيسية للهدف";
            await LoadDetailsAsync(mediaId, ct);
        }
        else
        {
            ErrorAr = result.ErrorAr ?? "تعذر تعيين الرئيسية";
        }
    }

    /// <summary>
    /// حذف أصل نهائي — الخادم يرفض (409) ما دام مربوطاً: هذه هي المراجعة
    /// الإلزامية؛ الروابط المعروضة في لوحة التفاصيل هي ما يجب مراجعته أولاً.
    /// </summary>
    public async Task DeleteAsync(long mediaId, CancellationToken ct)
    {
        var result = await _api.DeleteMediaAsync(mediaId, ct);
        if (result.Ok)
        {
            SuccessAr = "حُذف الأصل وكنست ملفاته من المخزن";
            Selected = null;
            OnPropertyChanged(nameof(HasSelection));
            LibraryChanged?.Invoke();
        }
        else
        {
            ErrorAr = result.ErrorAr + " — راجع الروابط في لوحة التفاصيل ثم افكها أولاً";
            await LoadDetailsAsync(mediaId, ct);
        }
    }

    /// <summary>إعادة معالجة النسخ المصغرة (تصغير فقط — الأصل لا يُستبدل §6.1).</summary>
    public async Task ReprocessAsync(long mediaId, CancellationToken ct)
    {
        var result = await _api.ReprocessThumbnailsAsync(mediaId, ct);
        if (result.Ok && result.Value is { } dto)
        {
            SuccessAr = $"أُعيدت معالجة النسخ (#{dto.MediaId})";
            LibraryChanged?.Invoke();
        }
        else
        {
            ErrorAr = result.ErrorAr ?? "تعذرت إعادة المعالجة";
        }
    }

    public void RequestPreview(long mediaId) => PreviewRequested?.Invoke(mediaId);

    /// <summary>فك ترميز بايتات صورة إلى BitmapImage على خيط UI — WinUI يتطلب ذلك.</summary>
    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> DecodeImageAsync(byte[] bytes)
    {
        try
        {
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null; // بايتات لا تُفك — المصغرة تظهر كلوحة «غير متاحة» بلا انهيار
        }
    }
}

/// <summary>سطر في تقرير المكررات (عرض فقط — لا دمج/حذف تلقائي §6.2).</summary>
public sealed class DuplicateEncounterRow
{
    public required MediaDuplicateEncounterDto Source { get; init; }
    public string FileAr => $"#{Source.FileId} · {Source.ClientName}";
    public string JobAr => $"مهمة #{Source.JobId} ({Source.JobLabel})";
    public string ExistingAr => Source.ExistingMediaId is { } id ? $"أصل قائم #{id}" : "—";
    public string ShaShort => Source.Sha256.Length > 12 ? Source.Sha256[..12] + "…" : Source.Sha256;
}

/// <summary>زوج «مشتبه بصرياً» (مسافة هامنغ ≤ 8 من 64 بت) — تقرير فقط.</summary>
public sealed class VisualSuspectRow
{
    public required MediaVisualSuspectPairDto Source { get; init; }
    public string PairAr => $"#{Source.FirstMediaId} ↔ #{Source.SecondMediaId}";
    public string DistanceAr => "مسافة هامنغ " + Source.AhashDistance;
}

/// <summary>محوّل ثابت لقوالب x:Bind (bool → Visibility) — دالة صريحة لا مورد محوّل.</summary>
public static class MediaConverters
{
    public static Microsoft.UI.Xaml.Visibility BoolToVis(bool value) =>
        value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
}
