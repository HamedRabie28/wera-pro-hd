// Views/CatalogPage.xaml.cs — WERA PRO HD Desktop (G4)
// سلوك جدول الكتالوج: بحث وفلترة وترقيم خادمي وحالات صدق — كل النداءات async.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Input;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class CatalogPage : UserControl
{
    private readonly CatalogViewModel _vm;
    private bool _loading;

    public CatalogPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<CatalogViewModel>();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CatalogViewModel.IsLoading) or nameof(CatalogViewModel.ErrorAr)
                or nameof(CatalogViewModel.SummaryAr) or nameof(CatalogViewModel.Page)
                or nameof(CatalogViewModel.TotalPages) or nameof(CatalogViewModel.CanCreate))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
            }
        };
        RowsControl.ItemsSource = _vm.Rows;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        await _vm.LoadAsync(default);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
    }

    /// <summary>حالات صدق: تحميل · خطأ مع إعادة محاولة · فارغ — بلا ادعاء.</summary>
    private void RefreshState()
    {
        _loading = _vm.IsLoading;

        LoadingRing.IsActive = _vm.IsLoading;
        LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        var hasError = !_vm.IsLoading && _vm.ErrorAr is not null;
        ErrorPanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = _vm.ErrorAr ?? string.Empty;

        var isEmpty = !_vm.IsLoading && _vm.ErrorAr is null && _vm.Rows.Count == 0;
        EmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

        SummaryText.Text = _vm.SummaryAr;
        PageText.Text = $"الصفحة {_vm.Page} من {_vm.TotalPages}";
        PrevBtn.IsEnabled = _vm.Page > 1 && !_vm.IsLoading;
        NextBtn.IsEnabled = _vm.Page < _vm.TotalPages && !_vm.IsLoading;
        NewProductBtn.Visibility = _vm.CanCreate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _ = SearchAsync();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        _vm.SearchText = SearchBox.Text;
        await _vm.SearchAsync(default);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SaleModeFilter is null || StatusFilter is null)
        {
            return; // أثناء InitializeComponent قبل ربط الفلاتر
        }
        _vm.SaleModeFilterIndex = SaleModeFilter.SelectedIndex;   // 0 = «الكل» (null)
        _vm.StatusFilterIndex = StatusFilter.SelectedIndex;
        _ = LoadAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void PrevPage_Click(object sender, RoutedEventArgs e) => _ = RunCommandAsync(_vm.PreviousPageCommand);

    private void NextPage_Click(object sender, RoutedEventArgs e) => _ = RunCommandAsync(_vm.NextPageCommand);

    private async Task RunCommandAsync(IAsyncRelayCommand command)
    {
        if (command.CanExecute(null))
        {
            await command.ExecuteAsync(null);
        }
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
    }

    private void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var editor = ActivatorUtilities.CreateInstance<ProductEditorWindow>(App.Host.Services);
        // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
        editor.Activate();
    }
}
