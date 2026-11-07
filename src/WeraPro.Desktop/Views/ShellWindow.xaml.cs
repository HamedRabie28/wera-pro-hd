// Views/ShellWindow.xaml.cs — WERA PRO HD Desktop (G3)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _heartbeatTimer;

    public ShellWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — Shell الإداري";
        App.ActiveWindow = this;
        App.MainWindow = this; // حارس النافذة الواحدة (2026-11-07)

        _vm = App.Host.Services.GetRequiredService<ShellViewModel>();
        _vm.LoggedOut += OnLoggedOut;
        RefreshIdentity();
        _vm.PropertyChanged += (_, e) =>
        {
            _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshIdentity);
        };

        // نبضة جاهزية كل 30 ثانية — كل النداءات async (لا تجميد UI)
        _heartbeatTimer = DispatcherQueue.CreateTimer();
        _heartbeatTimer.Interval = TimeSpan.FromSeconds(30);
        _heartbeatTimer.Tick += async (_, _) => await _vm.HeartbeatAsync(default);
        _heartbeatTimer.Start();
        _ = _vm.HeartbeatAsync(default);
    }

    private void RefreshIdentity()
    {
        UserNameText.Text = _vm.DisplayNameAr;
        UserRoleText.Text = _vm.RolesDisplayAr + " · " + _vm.ScopeDisplayAr;
        ScopeText.Text = "النطاق: " + _vm.ScopeDisplayAr;
        ConnText.Text = _vm.ConnectionDisplayAr;
        ConnDot.Fill = _vm.IsOnline
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraActionPrimaryInkBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"];
        UserInitials.Text = _vm.DisplayNameAr.Length > 0 ? _vm.DisplayNameAr[..Math.Min(2, _vm.DisplayNameAr.Length)] : "؟";

        // إعدادات الوزن (D-07) من الخادم — إثبات التغذية الحقيقية للأساس
        _ = LoadFoundationAsync();
    }

    private async Task LoadFoundationAsync()
    {
        var pos = App.Host.Services.GetRequiredService<PosViewModel>();
        await pos.LoadFoundationAsync(default);
        _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            WeighingText.Text = pos.WeighingInfoAr;
        });
    }

    private void NavTop_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // G4: «الكتالوج»؛ G5: «مكتبة الصور»؛ G6: «المخزون/المشتريات/الأطراف/المخازن» —
        // صفحة واحدة ظاهرة في كل لحظة («المخزون» مركز لسباقات G6 فتفتح اللوحة والجرد والتحويلات معاً)
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            var g6 = tag is "inventory" or "purchasing" or "parties" or "warehouses";
            var g8 = tag is "finance";
            HomeContent.Visibility = tag is "catalog" or "media" || g6 || g8 ? Visibility.Collapsed : Visibility.Visible;
            CatalogContent.Visibility = tag == "catalog" ? Visibility.Visible : Visibility.Collapsed;
            MediaContent.Visibility = tag == "media" ? Visibility.Visible : Visibility.Collapsed;
            InventoryContent.Visibility = tag == "inventory" ? Visibility.Visible : Visibility.Collapsed;
            StocktakeContent.Visibility = tag == "inventory" ? Visibility.Visible : Visibility.Collapsed;
            TransfersContent.Visibility = tag == "inventory" ? Visibility.Visible : Visibility.Collapsed;
            WarehousesContent.Visibility = tag == "warehouses" ? Visibility.Visible : Visibility.Collapsed;
            PurchasingContent.Visibility = tag == "purchasing" ? Visibility.Visible : Visibility.Collapsed;
            PartiesContent.Visibility = tag == "parties" ? Visibility.Visible : Visibility.Collapsed;
            FinanceContent.Visibility = tag == "finance" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OpenPos_Click(object sender, RoutedEventArgs e)
    {
        // POS نافذة مستقلة بلا أي قائمة إدارية (§9.0)
        var pos = ActivatorUtilities.CreateInstance<PosWindow>(App.Host.Services);
        pos.FlowDirection = FlowDirection.RightToLeft;
        pos.Activate();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _heartbeatTimer.Stop();
        await _vm.LogoutAsync(default);
    }

    private void OnLoggedOut()
    {
        _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var login = ActivatorUtilities.CreateInstance<LoginWindow>(App.Host.Services);
            login.Activate();
            Close();
        });
    }
}
