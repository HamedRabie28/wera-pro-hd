// Views/MediaLibraryPage.xaml.cs — WERA PRO HD Desktop (G5)
// سلوك مكتبة الوسائط (PAGE-16): كل العمليات عبر ViewModel بنداءات async —
// لا تجميد UI؛ مصغرة كل بطاقة تُحمَّل عند ظهورها (تكاسل) لا دفعة واحدة،
// والحذف يمر بمراجعة الروابط المعروضة (الرفض الخادمي 409 هو الحارس).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class MediaLibraryPage : UserControl
{
    private readonly MediaLibraryViewModel _vm;
    private readonly WeraApiClient _api;
    private long _selectedMediaId;

    public MediaLibraryPage()
    {
        InitializeComponent();

        _vm = App.Host.Services.GetRequiredService<MediaLibraryViewModel>();
        _api = App.Host.Services.GetRequiredService<WeraApiClient>();
        _vm.PreviewRequested += OnPreviewRequested;
        _vm.LibraryChanged += () => _ = ReloadCurrentPageAsync();

        CardsControl.ItemsSource = _vm.Cards;
        LinksControl.ItemsSource = _vm.Links;
        _vm.Cards.CollectionChanged += (_, _) => LoadVisibleThumbs();

        UploadBtn.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;
        ImportBtn.Visibility = _vm.CanImport ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaLibraryViewModel.IsLoading)
                or nameof(MediaLibraryViewModel.ErrorAr)
                or nameof(MediaLibraryViewModel.SuccessAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
            }
        };
        _ = _vm.LoadAsync(default);
    }

    private void RefreshState()
    {
        LoadingRing.IsActive = _vm.IsLoading;
        var hasError = _vm.ErrorAr is not null;
        ErrorPanel.Visibility = hasError && !_vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = _vm.ErrorAr ?? string.Empty;
        EmptyText.Visibility = !_vm.IsLoading && !hasError && _vm.Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _vm.SuccessAr ?? _vm.DetailsErrorAr ?? string.Empty;
        StatusText.Foreground = _vm.ErrorAr is not null
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraTextSecondaryBrush"];
        SummaryText.Text = _vm.SummaryAr;
        PageText.Text = _vm.TotalCount == 0 ? "—" : $"صفحة {_vm.Page} / {_vm.TotalPages}";
        PrevBtn.IsEnabled = _vm.Page > 1;
        NextBtn.IsEnabled = _vm.Page < _vm.TotalPages;
    }

    private async Task ReloadCurrentPageAsync()
    {
        await _vm.LoadAsync(default);
    }

    // ── الشبكة: اختيار بطاقة + تحميل مصغرة بتكاسل ────────────────────────

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long mediaId })
        {
            _selectedMediaId = mediaId;
            var card = _vm.Cards.FirstOrDefault(c => c.MediaId == mediaId);
            if (card is null)
            {
                return;
            }
            DetailsHint.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Visible;
            SelectedSha.Text = "بصمة SHA-256: " + card.Source.Sha256;
            SelectedMeta.Text = $"{card.DimensionsAr} · {card.SizeAr} · {card.MimeAr} · {card.LinksAr}";
            UploadedText.Text = "رُفعت: " + card.UploadedAr + (card.HasPreview ? " · معاينة متاحة" : " · لا معاينة");
            _ = _vm.LoadDetailsAsync(mediaId, default);
        }
    }

    /// <summary>تحميل مصغرة البطاقات الظاهرة — يُستدعى من حمل القائمة (تكاسل: أول صفحة فقط).</summary>
    private void LoadVisibleThumbs()
    {
        foreach (var card in _vm.Cards.Take(MediaLibraryViewModel.PageSize))
        {
            _ = _vm.LoadThumbAsync(card, default);
        }
    }

    // ── الأحداث: بحث/فلاتر/ترقيم ─────────────────────────────────────────

    private void ShaBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _vm.ShaSearchText = ShaBox.Text;
            _ = _vm.SearchAsync(default);
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _vm.ShaSearchText = ShaBox.Text;
        _ = _vm.SearchAsync(default);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        _vm.MimeFilterIndex = MimeFilter.SelectedIndex;
        _vm.LinkFilterIndex = LinkFilter.SelectedIndex;
        if (IsLoaded)
        {
            _ = _vm.SearchAsync(default);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = _vm.RefreshAsync(default);

    private void PrevPage_Click(object sender, RoutedEventArgs e) => _ = _vm.PreviousPageAsync(default);

    private void NextPage_Click(object sender, RoutedEventArgs e) => _ = _vm.NextPageAsync(default);

    // ── الرفع والاستيراد ─────────────────────────────────────────────────

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        // الصفحة UserControl (لا تملك مقبض نافذة) — نأخذ مقبض النافذة النشطة
        if (App.ActiveWindow is not { } owner)
        {
            return;
        }
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner)))
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        await _vm.UploadAsync(file.Path, default);
        RefreshState();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        // مركز الاستيراد الدفعي (PAGE-17) — نافذة مستقلة
        var window = ActivatorUtilities.CreateInstance<MediaImportWindow>(App.Host.Services);
        // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
        window.Activate();
    }

    // ── المكررات (تقرير فقط §6.2) ───────────────────────────────────────

    private async void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        var report = await _api.GetDuplicatesReportAsync(ct: default);
        if (!report.Ok || report.Value is null)
        {
            await ShowMessageAsync("تعذر جلب تقرير المكررات", report.ErrorAr ?? "سبب غير معروف");
            return;
        }

        var exact = report.Value.Exact;
        var visual = report.Value.VisualSuspects;
        var lines = new List<string>
        {
            $"لقاءات «مطابق تماماً» (من مهام الاستيراد): {exact.TotalCount:N0}",
        };
        foreach (var item in exact.Items.Take(8))
        {
            lines.Add($"  · {item.ClientName} (مهمة #{item.JobId}) → أصل قائم #{item.ExistingMediaId}");
        }
        if (exact.Items.Count > 8)
        {
            lines.Add($"  … و{exact.Items.Count - 8} آخر");
        }
        lines.Add(string.Empty);
        lines.Add($"أزواج «مشتبه بصرياً» (هامنغ ≤ 8): {visual.TotalCount:N0}");
        foreach (var pair in visual.Items.Take(8))
        {
            lines.Add($"  · أصل #{pair.FirstMediaId} ↔ أصل #{pair.SecondMediaId} (مسافة {pair.AhashDistance})");
        }
        if (visual.Items.Count > 8)
        {
            lines.Add($"  … و{visual.Items.Count - 8} زوجاً آخر");
        }
        lines.Add(string.Empty);
        lines.Add("التقرير للمراجعة فقط — لا دمج ولا حذف تلقائي (§6.2).");

        await ShowMessageAsync("تقرير المكررات", string.Join('\n', lines));
    }

    // ── عمليات التفاصيل ─────────────────────────────────────────────────

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaId == 0)
        {
            return;
        }
        var dialog = new MediaLinkDialog(_api, _selectedMediaId)
        {
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _vm.LinkAsync(_selectedMediaId, dialog.ProductId, dialog.SkuId, dialog.MakePrimary, default);
            RefreshState();
        }
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long linkId })
        {
            await _vm.UnlinkAsync(_selectedMediaId, linkId, default);
            RefreshState();
        }
    }

    private async void SetPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long linkId })
        {
            await _vm.SetPrimaryAsync(_selectedMediaId, linkId, default);
            RefreshState();
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaId != 0)
        {
            _vm.RequestPreview(_selectedMediaId);
        }
    }

    private async void Reprocess_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaId == 0)
        {
            return;
        }
        await _vm.ReprocessAsync(_selectedMediaId, default);
        RefreshState();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMediaId == 0)
        {
            return;
        }
        var confirm = new ContentDialog
        {
            Title = "حذف أصل نهائياً",
            Content = "سيُحذف الأصل وكل نسخه من المخزن. ما دام مربوطاً يرفضه الخادم — راجع الروابط أولاً. هل تريد المتابعة؟",
            PrimaryButtonText = "حذف نهائي",
            CloseButtonText = "إلغاء",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        await _vm.DeleteAsync(_selectedMediaId, default);
        RefreshState();
    }

    private void OnPreviewRequested(long mediaId)
    {
        var preview = ActivatorUtilities.CreateInstance<MediaPreviewWindow>(App.Host.Services, mediaId);
        // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
        preview.Activate();
    }

    private async Task ShowMessageAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = "إغلاق",
            XamlRoot = XamlRoot,
        };
        _ = await dialog.ShowAsync();
    }
}
