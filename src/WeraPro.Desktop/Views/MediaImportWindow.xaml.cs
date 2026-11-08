// Views/MediaImportWindow.xaml.cs — WERA PRO HD Desktop (G5)
// سلوك مركز الاستيراد الدفعي: الترحيل على دفعات 25 ملفاً (طلبات multipart
// صغيرة — لا حد لعدد الملفات)، واستقصاء كل 2s أثناء RUNNING فقط (مؤقت
// نافذة لا Timer داخل ViewModel)، وتقرير لكل ملف بأسبابه المسماة.
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class MediaImportWindow : Window
{
    private const int TransferBatchSize = 25;

    private readonly MediaImportViewModel _vm;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pollTimer;

    public MediaImportWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — مركز استيراد الصور الدفعي";
        App.ActiveWindow = this;

        _vm = ActivatorUtilities.CreateInstance<MediaImportViewModel>(App.Host.Services);
        FilesControl.ItemsSource = _vm.Files;
        _vm.ImportFinished += () => _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () => await RefreshReportAsync());

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaImportViewModel.IsBusy)
                or nameof(MediaImportViewModel.ErrorAr)
                or nameof(MediaImportViewModel.SuccessAr)
                or nameof(MediaImportViewModel.JobStateAr)
                or nameof(MediaImportViewModel.CountsAr)
                or nameof(MediaImportViewModel.ErrorsHistogramAr)
                or nameof(MediaImportViewModel.StagedCount)
                or nameof(MediaImportViewModel.ScopeNoteAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
            }
        };

        // استقصاء كل 2s أثناء RUNNING فقط — يبدأ ويتوقف مع حالة المهمة
        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(2);
        _pollTimer.Tick += async (_, _) =>
        {
            await _vm.PollAsync(default);
            await RefreshReportAsync();
        };

        RefreshState();
    }

    private void RefreshState()
    {
        var busy = _vm.IsBusy;
        CreateJobBtn.IsEnabled = !busy && _vm.CanImport;
        AddFilesBtn.IsEnabled = !busy && _vm.HasJob && !_vm.IsRunning;
        StartBtn.IsEnabled = !busy && _vm.CanStart;
        CancelBtn.IsEnabled = !busy && _vm.CanCancel;

        JobStateText.Text = _vm.JobStateAr
            + (_vm.JobId is { } id ? " #" + id : "");
        StagedText.Text = _vm.StagedCount.ToString("N0") + " ملف مرحّل";
        ScopeText.Text = _vm.ScopeNoteAr;
        CountsText.Text = _vm.CountsAr;
        ErrorsText.Text = _vm.ErrorsHistogramAr;
        StatusText.Text = _vm.ErrorAr ?? _vm.SuccessAr ?? string.Empty;
        StatusText.Foreground = _vm.ErrorAr is not null
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraTextSecondaryBrush"];
        FilesSummaryText.Text = _vm.SummaryAr;
        FilesPageText.Text = _vm.Files.Count == 0 ? "—" : $"صفحة {_vm.Page} / {_vm.TotalPages}";

        // المؤقت يعمل فقط والمهمة جارية — لا استقصاء خامل
        if (_vm.IsRunning && !_pollTimer.IsRunning)
        {
            _pollTimer.Start();
        }
        else if (!_vm.IsRunning && _pollTimer.IsRunning)
        {
            _pollTimer.Stop();
        }
    }

    // ── الأحداث ─────────────────────────────────────────────────────────

    private async void CreateJob_Click(object sender, RoutedEventArgs e)
    {
        _vm.LabelText = LabelBox.Text;
        await _vm.CreateJobAsync(default);
        RefreshState();
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        // WinAppSDK picker (Microsoft.Windows.Storage.Pickers) — الوحيد الذي يعمل
        // في تطبيق غير مُغلَّف؛ النتيجة PickFileResult.Path فقط (لا StorageFile).
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)))
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        // ترحيل على دفعات 25 ملفاً — طلبات صغيرة متتابعة لا طلب عملاق واحداً
        foreach (var batch in files.Chunk(TransferBatchSize))
        {
            var payload = new List<(string FileName, Stream Content)>();
            foreach (var file in batch)
            {
                try
                {
                    var owned = new MemoryStream();
                    using (var source = System.IO.File.OpenRead(file.Path))
                    {
                        await source.CopyToAsync(owned, 64 * 1024);
                    }
                    owned.Position = 0;
                    payload.Add((System.IO.Path.GetFileName(file.Path), owned));
                }
                catch (Exception)
                {
                    // ملف لا يُقرأ محلياً — يتخطى ولا يوقف الدفعة (يظهر في التقرير بما رُحّل)
                }
            }
            if (payload.Count > 0)
            {
                await _vm.AddFilesAsync(payload, default);
            }
            RefreshState();
        }
        await _vm.LoadFilesAsync(default);
        RefreshState();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await _vm.StartAsync(default);
        RefreshState();
        await RefreshReportAsync();
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await _vm.CancelAsync(default);
        RefreshState();
        await RefreshReportAsync();
    }

    private void StateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        _vm.ApplyStateFilter(StateFilter.SelectedIndex);
        _ = _vm.LoadFilesAsync(default);
    }

    private async void ReloadReport_Click(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        await _vm.PreviousPageAsync(default);
        RefreshState();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        await _vm.NextPageAsync(default);
        RefreshState();
    }

    private async Task RefreshReportAsync()
    {
        await _vm.LoadFilesAsync(default);
        await _vm.LoadReportAsync(default);
        RefreshState();
    }
}
