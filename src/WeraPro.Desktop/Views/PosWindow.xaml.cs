// Views/PosWindow.xaml.cs — WERA PRO HD Desktop (G3)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class PosWindow : Window
{
    private readonly PosViewModel _vm;
    private readonly ConnectivityService _connectivity;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _heartbeatTimer;

    public PosWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — POS (G3 Shell — بيانات السلة DEV حتى G7)";
        MinWidth = 1280;
        MinHeight = 720;

        _vm = App.Host.Services.GetRequiredService<PosViewModel>();
        _connectivity = App.Host.Services.GetRequiredService<ConnectivityService>();

        SessionText.Text = $"{_vm.CashierDisplayAr} · {_vm.StationDisplayAr} · {_vm.ScopeDisplayAr}";
        _ = LoadFoundationAsync();

        _connectivity.StateChanged += () =>
            _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshConnection);

        _heartbeatTimer = DispatcherQueue.CreateTimer();
        _heartbeatTimer.Interval = TimeSpan.FromSeconds(30);
        _heartbeatTimer.Tick += async (_, _) => await _connectivity.ProbeAsync(default);
        _heartbeatTimer.Start();
        _ = _connectivity.ProbeAsync(default);
        RefreshConnection();
    }

    private async Task LoadFoundationAsync()
    {
        await _vm.LoadFoundationAsync(default);
        _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            WeighingText.Text = _vm.WeighingInfoAr;
        });
    }

    private void RefreshConnection()
    {
        ConnText.Text = _connectivity.DisplayAr;
        ConnDot.Fill = _connectivity.State == ConnectionState.Online
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraActionPrimaryInkBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"];
    }
}
