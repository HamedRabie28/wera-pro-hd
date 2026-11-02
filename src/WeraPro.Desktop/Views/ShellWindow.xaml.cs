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
        // G3: «التشغيل» فقط مفعّل — البقية معطلة في XAML بأسبابها (قاعدة الصدق)
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
