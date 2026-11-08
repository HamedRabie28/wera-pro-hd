// Views/LoginWindow.xaml.cs — WERA PRO HD Desktop (G3)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _probeTimer;

    public LoginWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — تسجيل الدخول";

        // حجم بوابة ثابت احترافي (لوحة هوية + صورة حقيقية) — بالبكسل الفعلي
        if (AppWindow is not null)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 780));
        }

        // حارس النافذة الواحدة (2026-11-07): تسجيل نفسها كنافذة رئيسية/نشطة
        App.MainWindow = this;
        App.ActiveWindow = this;

        _vm = App.Host.Services.GetRequiredService<LoginViewModel>();
        _vm.LoginSucceeded += OnLoginSucceeded;

        _vm.PropertyChanged += (_, e) =>
        {
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (e.PropertyName == nameof(LoginViewModel.ServerStatusAr))
                {
                    ServerStatusText.Text = _vm.ServerStatusAr;
                }
                if (e.PropertyName == nameof(LoginViewModel.StatusAr))
                {
                    StatusText.Text = _vm.StatusAr;
                }
                if (e.PropertyName == nameof(LoginViewModel.ServerOnline))
                {
                    // نقطة الحالة تعكس الحالة الفعلية: أخضر جاهز / كهرماني غير متاح
                    ServerDot.Fill = _vm.ServerOnline
                        ? (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["WeraStatusSuccessBrush"]
                        : (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["WeraStatusWarningBrush"];
                }
                LoginBtn.IsEnabled = _vm.CanLogin;
            });
        };

        BranchField.ItemsSource = _vm.Branches;
        StationField.ItemsSource = _vm.Stations;
        LoginBtn.IsEnabled = false;

        // نبضة اتصال دورية خفيفة كل 30 ثانية + فحص أول فوري (كل نداءات I/O async)
        _probeTimer = DispatcherQueue.CreateTimer();
        _probeTimer.Interval = TimeSpan.FromSeconds(30);
        _probeTimer.Tick += async (_, _) => await _vm.CheckServerAsync(default);
        _probeTimer.Start();
        _ = _vm.CheckServerAsync(default);
    }

    private async void Password_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && _vm.CanLogin)
        {
            await RunLoginAsync();
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await RunLoginAsync();

    private async Task RunLoginAsync()
    {
        _vm.Password = PasswordField.Password;
        LoginBtn.IsEnabled = false;
        StatusText.Text = "جارٍ التحقق…";
        try
        {
            await _vm.LoginAsync(default);
        }
        finally
        {
            LoginBtn.IsEnabled = _vm.CanLogin;
        }
    }

    private void BranchField_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedBranch = BranchField.SelectedItem as Contracts.V1.BranchDto;
        StationField.IsEnabled = _vm.SelectedBranch is not null;
    }

    private void OnLoginSucceeded()
    {
        _probeTimer.Stop();
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            var shell = ActivatorUtilities.CreateInstance<ShellWindow>(App.Host.Services);
            App.MainWindow = shell; // تسليم عصا النافذة الرئيسية لحارس التكرار
            shell.Activate();
            Close();
        });
    }
}
