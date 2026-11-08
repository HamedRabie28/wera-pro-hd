// Views/InventoryHubPage.xaml.cs — WERA PRO HD Desktop (G6)
// لوحة المخزون: تحميل الأرصدة/النواقص/الدفعات/التقييم من الخادم + كرت الحركة
// بنقرة مزدوجة (PAGE-19 نافذة) + تسوية الهالك/التلف بمسار اعتماد D-03.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class InventoryHubPage : Page
{
    private readonly InventoryDashboardViewModel _vm;

    public InventoryHubPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<InventoryDashboardViewModel>();
        DataContext = _vm;
        DamageButton.Visibility = _vm.CanAdjust ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InventoryDashboardViewModel.IsLoading))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
            if (e.PropertyName == nameof(InventoryDashboardViewModel.TotalCount))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshPager);
            }
        };
        foreach (var w in _vm.Warehouses) WarehouseFilter.Items.Add(w);
        RefreshPager();
    }

    private void RefreshPager()
    {
        PageText.Text = $"صفحة {_vm.Page}";
        TotalText.Text = $"{_vm.TotalCount:N0} رصيداً";
        ValuationTotalText.Text = _vm.TotalValuation.ToString("N2") + " ج.م";
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = SearchBox.Text;
        await _vm.SearchAsync(default);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Search_Click(sender, e);
        }
    }

    private async void WarehouseFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.WarehouseFilter = WarehouseFilter.SelectedItem as WeraPro.Contracts.V1.WarehouseDto;
        await _vm.LoadBalancesAsync(default);
        await _vm.LoadAdvisoriesAsync(default);
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _vm.BelowReorderOnly = BelowReorderBox.IsChecked == true;
        await _vm.LoadBalancesAsync(default);
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e) => await _vm.PreviousPageAsync(default);

    private async void NextPage_Click(object sender, RoutedEventArgs e) => await _vm.NextPageAsync(default);

    private void BalancesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // كرت الحركة (PAGE-19): تسلسل موثق بمصدره ومعامل تحويله ورصيده الجاري
        if (BalancesList.SelectedItem is BalanceRow row)
        {
            var card = ActivatorUtilities.CreateInstance<MovementCardWindow>(App.Host.Services);
            // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
            card.LoadFor(row.SkuId, _vm.WarehouseFilter?.WarehouseId, row.SkuCode);
            card.Activate();
        }
    }

    private async void Damage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdjustmentDialog(XamlRoot) { Title = "تسوية هالك / تلف صلاحية (باعتماد مشرف)" };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Posted)
        {
            await _vm.LoadBalancesAsync(default);
            await _vm.LoadAdvisoriesAsync(default);
            if (dialog.PostedInfo is { } info)
            {
                var ok = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "رُحّلت التسوية",
                    Content = info, CloseButtonText = "حسناً",
                };
                _ = await ok.ShowAsync();
            }
        }
    }
}
