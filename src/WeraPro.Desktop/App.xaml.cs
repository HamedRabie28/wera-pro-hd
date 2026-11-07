// App.xaml.cs — WERA PRO HD Desktop (G3 · إقلاع محصّن 2026-11-07)
// الإقلاع: Generic Host + DI + Serilog + IHttpClientFactory، ثم نافذة الدخول.
// لا اتصال مباشر بقاعدة البيانات (ADR-002) — كل البيانات عبر WeraApiClient.
//
// تحصين 2026-11-07 (أمر المالك: حزمة تثبيت احترافية بلا تشغيل صامت في الخلفية):
//   1) نافذة واحدة فقط (Single Instance): المحاولة الثانية تُفعِّل نافذة النسخة القائمة وتخرج.
//   2) appsettings.json يُقرأ من مجلد التطبيق الفعلي (AppContext.BaseDirectory) —
//      لا من CWD؛ فالإطلاق من اختصار قائمة ابدأ يجعل CWD = System32 ويفشل الإقلاع صامتًا.
//   3) معالجات استثناءات شاملة (XAML + AppDomain + TaskScheduler) مع سجل فشل دائم على القرص.
//   4) نافذة خطأ فادح مرئية دائمًا: لا يبقى التطبيق عملية خاملة بلا نافذة أبدًا.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System.Threading;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;
using WeraPro.Desktop.Views;

namespace WeraPro.Desktop;

public sealed partial class App : Application
{
    public static IHost Host { get; private set; } = null!;

    /// <summary>النافذة النشطة (تحدّثها Shell/POS عند الإنشاء) — لمنشئي الملفات ومربعات الحوار التي تحتاج مقبض نافذة.</summary>
    public static Microsoft.UI.Xaml.Window? ActiveWindow { get; set; }

    /// <summary>النافذة الرئيسية الحالية (الدخول ثم Shell) — يستخدمها حارس التكرار لتفعيل النافذة القائمة.</summary>
    public static Microsoft.UI.Xaml.Window? MainWindow { get; set; }

    // ── نافذة واحدة فقط (منع النسخ المتكررة/العمليات الخاملة) ──
    private const string MutexName = @"Local\WERA_PRO_HD.Desktop.SingleInstance";
    private const string ActivateEventName = @"Local\WERA_PRO_HD.Desktop.Activate";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateSignal;

    private static readonly string LogDirectory = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "WERA_PRO_HD", "logs");

    public App()
    {
        InitializeComponent();

        // تحصين الاستثناءات قبل أي شيء آخر — لا فشل صامت مهما كان المصدر
        UnhandledException += OnUnhandledXamlException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 1) نافذة واحدة فقط: المحاولة الثانية تفعّل النسخة القائمة وتخرج فورًا
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            SignalFirstInstanceThenExit();
            return;
        }

        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var watcher = new Thread(WatchActivateSignal)
            {
                IsBackground = true,
                Name = "WeraSingleInstanceWatcher"
            };
            watcher.Start();

            // 2) مجلد التطبيق الفعلي هو مرجع كل المسارات النسبية — لا CWD أبدًا.
            //    (إطلاق التطبيق من اختصار يجعل CWD = System32 فيفقد appsettings.json)
            var appDir = AppContext.BaseDirectory;
            try { System.Environment.CurrentDirectory = appDir; } catch { /* محرك أقراص شبكي غير متاح — نتجاهل */ }

            Host = BuildHost(appDir);
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
            return;
        }

        var logger = Host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("WERA PRO HD Desktop — إقلاع ناجح (حزمة التثبيت 2026-11-07 · Unpackaged Self-Contained)");

        try
        {
            // البوابة الأولى: تسجيل الدخول — لا Shell قبل جلسة موثقة (§11.2)
            var login = ActivatorUtilities.CreateInstance<LoginWindow>(Host.Services);
            MainWindow = login;
            ActiveWindow = login;
            login.Activate();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "فشل إنشاء نافذة الدخول");
            ShowFatalError(ex, alreadyLogged: true);
        }
    }

    private static IHost BuildHost(string appDirectory)
    {
        var host = new HostBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                // مسار مطلق من مجلد التطبيق — محصّن ضد اختلاف دليل العمل
                config.AddJsonFile(
                    System.IO.Path.Combine(appDirectory, "appsettings.json"),
                    optional: false, reloadOnChange: false);
            })
            .UseSerilog((context, logger) =>
            {
                logger.MinimumLevel.Information()
                      .Enrich.FromLogContext()
                      .WriteTo.File(
                          System.IO.Path.Combine(LogDirectory, "desktop-.log"),
                          rollingInterval: RollingInterval.Day,
                          retainedFileTimeLimit: System.TimeSpan.FromDays(13));
            })
            .ConfigureServices((context, services) =>
            {
                // حالة الجلسة + الاتصال (Online-first — D-08 افتراضياً حتى الحسم)
                services.AddSingleton<SessionService>();
                services.AddSingleton<ConnectivityService>();

                // IHttpClientFactory: عميلان — الرئيسي بمعرف الترابط، وعميل التجديد
                // المستقل (منع دورة المعالجة عند 401 داخل CorrelationHandler)
                var baseUrl = context.Configuration["Api:BaseUrl"]
                    ?? throw new InvalidOperationException("Api:BaseUrl مفقود من appsettings.json");
                var timeout = TimeSpan.FromSeconds(context.Configuration.GetValue("Api:RequestTimeoutSeconds", 10));

                services.AddHttpClient(WeraApiClient.HttpClientName)
                    .ConfigureHttpClient(client => { client.BaseAddress = new Uri(baseUrl); client.Timeout = timeout; })
                    .AddHttpMessageHandler(sp => new CorrelationHandler(
                        sp.GetRequiredService<SessionService>()));

                services.AddHttpClient(SessionService.RefreshHttpClientName)
                    .ConfigureHttpClient(client => { client.BaseAddress = new Uri(baseUrl); client.Timeout = timeout; });

                services.AddSingleton<WeraApiClient>();
                services.AddSingleton<LoginViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<PosViewModel>();
                services.AddSingleton<ViewModels.CatalogViewModel>();
                services.AddSingleton<ViewModels.MediaLibraryViewModel>();
                services.AddTransient<ViewModels.ProductEditorViewModel>();
                services.AddTransient<ViewModels.MediaImportViewModel>();
                // ── G6: المخزون والمشتريات والأطراف ──
                services.AddTransient<ViewModels.InventoryDashboardViewModel>();
                services.AddTransient<ViewModels.MovementCardViewModel>();
                services.AddTransient<ViewModels.StocktakeViewModel>();
                services.AddTransient<ViewModels.TransfersViewModel>();
                services.AddTransient<ViewModels.WarehousesViewModel>();
                services.AddTransient<ViewModels.PurchasingViewModel>();
                services.AddTransient<ViewModels.PartiesViewModel>();
                services.AddTransient<ViewModels.FinanceViewModel>();
            })
            .Build();

        return host;
    }

    // ─────────────────────────────────────────────────────────────
    // نافذة واحدة فقط: تفعيل النسخة القائمة عند أي محاولة تشغيل ثانية
    // ─────────────────────────────────────────────────────────────
    private static void SignalFirstInstanceThenExit()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch
        {
            // النسخة الأولى في طور الإغلاق — لا شيء لتفعيله
        }
        Application.Current.Exit();
    }

    private void WatchActivateSignal()
    {
        var signal = _activateSignal;
        if (signal is null)
        {
            return;
        }

        while (true)
        {
            try
            {
                if (!signal.WaitOne())
                {
                    continue;
                }
            }
            catch
            {
                break; // قُطع المقبض أثناء الإغلاق
            }

            var queue = DispatcherQueue;
            if (queue is null)
            {
                break;
            }
            _ = queue.TryEnqueue(ActivateMainWindow);
        }
    }

    private static void ActivateMainWindow()
    {
        try
        {
            var window = MainWindow ?? ActiveWindow;
            if (window is null)
            {
                return;
            }

            window.AppWindow?.Show();

            if (window.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlapped &&
                overlapped.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                overlapped.Restore();
            }

            window.Activate();
        }
        catch
        {
            // نافذة قديمة أُغلقت — تجاهل بأمان
        }
    }

    // ─────────────────────────────────────────────────────────────
    // تحصين الاستثناءات: سجل دائم + نافذة مرئية — لا عملية خاملة أبدًا
    // ─────────────────────────────────────────────────────────────
    private void OnUnhandledXamlException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception, "XAML Unhandled");
        // نمنع الفشل الصامت: إما نُبقي التطبيق حيًا بنافذة قائمة، أو نعرض نافذة الخطأ
        e.Handled = MainWindow is not null || ActiveWindow is not null;
        if (!e.Handled)
        {
            ShowFatalError(e.Exception, alreadyLogged: true);
        }
    }

    private static void OnUnhandledDomainException(object sender, System.Runtime.ExceptionServices.UnhandledExceptionEventArgs e)
    {
        LogFatal(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "غير معروف"),
            "AppDomain Unhandled");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatal(e.Exception, "Unobserved Task");
        e.SetObserved();
    }

    private static void LogFatal(Exception ex, string source)
    {
        try
        {
            Host?.Services.GetRequiredService<ILogger<App>>().LogError(ex, "خطأ فادح — " + source);
        }
        catch
        {
            // Host لم يُبنَ بعد — السجل المباشر أدناه يغطي
        }

        try
        {
            System.IO.Directory.CreateDirectory(LogDirectory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(LogDirectory, "fatal-.log"),
                $"[{DateTimeOffset.Now:O}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // لا يمكن الكتابة على القرص — لا مجال لأكثر من ذلك
        }
    }

    /// <summary>نافذة خطأ فادح RTL مبنية بالكود: تظهر دائمًا بدل البقاء عملية خلفية بلا نافذة.</summary>
    private static void ShowFatalError(Exception ex, bool alreadyLogged = false)
    {
        if (!alreadyLogged)
        {
            LogFatal(ex, "Startup");
        }

        try
        {
            var logPath = System.IO.Path.Combine(LogDirectory, "fatal-.log");
            var message = ex.Message;
            var window = new Window
            {
                Title = "WERA PRO HD — خطأ في الإقلاع"
            };

            var root = new Grid { FlowDirection = FlowDirection.RightToLeft };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var surface = new SolidColorBrush(Microsoft.UI.Colors.White);
            var header = new SolidColorBrush(Microsoft.UI.Colors.FromArgb(255, 16, 34, 56));
            var accent = new SolidColorBrush(Microsoft.UI.Colors.FromArgb(255, 13, 131, 121));

            var card = new StackPanel
            {
                Padding = new Thickness(36, 28, 36, 24),
                Spacing = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Background = surface
            };

            var badge = new Border
            {
                Width = 46,
                Height = 46,
                CornerRadius = new CornerRadius(12),
                Background = header,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "W",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                }
            };

            card.Children.Add(badge);
            card.Children.Add(new TextBlock
            {
                Text = "تعذّر إقلاع WERA PRO HD",
                FontSize = 19,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.FromArgb(255, 23, 37, 54)),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
            });
            card.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.FromArgb(255, 89, 106, 125)),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
            });
            card.Children.Add(new TextBlock
            {
                Text = "تفاصيل الخطأ محفوظة في:" + Environment.NewLine + logPath,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.FromArgb(255, 130, 144, 160)),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
            });

            var closeBtn = new Button
            {
                Content = "إغلاق التطبيق",
                Height = 42,
                MinWidth = 190,
                FontSize = 14.5,
                Background = accent,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
            };
            closeBtn.Click += (_, _) =>
            {
                window.Close();
                Application.Current.Exit();
            };
            card.Children.Add(closeBtn);

            root.Children.Add(card);
            Grid.SetRow(card, 0);

            root.Background = new SolidColorBrush(Microsoft.UI.Colors.FromArgb(255, 241, 244, 247));
            window.Content = root;

            MainWindow = window;
            ActiveWindow = window;
            window.Activate();
        }
        catch
        {
            // آخر خط دفاع: إن فشلت حتى نافذة الخطأ فلا نترك العملية معلقة بلا هدف
            System.Environment.Exit(1);
        }
    }
}
