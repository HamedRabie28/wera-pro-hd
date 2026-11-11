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
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshIdentity);
        };

        // نبضة جاهزية كل 30 ثانية — كل النداءات async (لا تجميد UI)
        _heartbeatTimer = DispatcherQueue.CreateTimer();
        _heartbeatTimer.Interval = TimeSpan.FromSeconds(30);
        _heartbeatTimer.Tick += async (_, _) => await _vm.HeartbeatAsync(default);
        _heartbeatTimer.Start();
        _ = _vm.HeartbeatAsync(default);

        // الرئيسية التنفيذية (G9): طلب فتح POS من زر الإجراء السريع
        HomeContent.OpenPosRequested += OpenPosWindow;
    }

    private void RefreshIdentity()
    {
        UserNameText.Text = _vm.DisplayNameAr;
        UserRoleText.Text = _vm.RolesDisplayAr + " · " + _vm.ScopeDisplayAr;
        ConnText.Text = _vm.ConnectionDisplayAr;
        ConnDot.Fill = _vm.IsOnline
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraActionPrimaryInkBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"];
        UserInitials.Text = _vm.DisplayNameAr.Length > 0 ? _vm.DisplayNameAr[..Math.Min(2, _vm.DisplayNameAr.Length)] : "؟";

        // إعدادات الوزن (D-07) من الخادم — تغذية حقيقية لأساس POS عبر PosViewModel
        _ = LoadFoundationAsync();
    }

    private async Task LoadFoundationAsync()
    {
        var pos = App.Host.Services.GetRequiredService<PosViewModel>();
        await pos.LoadFoundationAsync(default);
        // لا يوجد عنصر عرض هنا بعد الرئيسية الجديدة (HOME-01) — الأساس يُعرض في POS نفسها
    }

    private void NavTop_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // G4: «الكتالوج»؛ G5: «مكتبة الصور»؛ G6: «المخزون/المشتريات/الأطراف/المخازن» —
        // صفحة واحدة ظاهرة في كل لحظة («المخزون» مركز لسباقات G6 فتفتح اللوحة والجرد والتحويلات معاً)
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            // G7: «نقطة البيع» تفتح نافذة POS المستقلة (§9.0) — لا تُبدّل محتوى الشِل
            if (tag == "pos")
            {
                OpenPosWindow();
                return;
            }
            var g6 = tag is "inventory" or "purchasing" or "parties" or "warehouses";
            var g8 = tag is "finance";
            var admin = tag is "users" or "settings" or "maintenance";
            var sales = tag is "sales" or "returns";
            HomeContent.Visibility = tag is "catalog" or "media" || g6 || g8 || admin || sales ? Visibility.Collapsed : Visibility.Visible;
            CatalogContent.Visibility = tag == "catalog" ? Visibility.Visible : Visibility.Collapsed;
            MediaContent.Visibility = tag == "media" ? Visibility.Visible : Visibility.Collapsed;
            InventoryContent.Visibility = tag == "inventory" ? Visibility.Visible : Visibility.Collapsed;
            StocktakeContent.Visibility = tag == "inventory" ? Visibility.Visible : Visibility.Collapsed;
            TransfersContent.Visibility = tag == "inventory" ? Visibility.Visible : Visibility.Collapsed;
            WarehousesContent.Visibility = tag == "warehouses" ? Visibility.Visible : Visibility.Collapsed;
            PurchasingContent.Visibility = tag == "purchasing" ? Visibility.Visible : Visibility.Collapsed;
            PartiesContent.Visibility = tag == "parties" ? Visibility.Visible : Visibility.Collapsed;
            FinanceContent.Visibility = tag == "finance" ? Visibility.Visible : Visibility.Collapsed;
            // G7 سجلات البيع: الفواتير والمعلقات والمطابقة + المرتجعات (PAGE-03/05/06/08)
            SalesContent.Visibility = tag == "sales" ? Visibility.Visible : Visibility.Collapsed;
            ReturnsContent.Visibility = tag == "returns" ? Visibility.Visible : Visibility.Collapsed;
            // G-ADMIN (PAGE-47/51/52): المستخدمون والإعدادات والنسخ والصيانة
            UsersContent.Visibility = tag == "users" ? Visibility.Visible : Visibility.Collapsed;
            SettingsContent.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
            MaintenanceContent.Visibility = tag == "maintenance" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OpenPos_Click(object sender, RoutedEventArgs e) => OpenPosWindow();

    private void OpenPosWindow()
    {
        // POS نافذة مستقلة بلا أي قائمة إدارية (§9.0)
        var pos = ActivatorUtilities.CreateInstance<PosWindow>(App.Host.Services);
        // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
        pos.Activate();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _heartbeatTimer.Stop();
        await _vm.LogoutAsync(default);
    }

    private void OnLoggedOut()
    {
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var login = ActivatorUtilities.CreateInstance<LoginWindow>(App.Host.Services);
            login.Activate();
            Close();
        });
    }
}
