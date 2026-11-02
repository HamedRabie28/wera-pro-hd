// App.xaml.cs — WERA PRO HD Desktop (G3)
// الإقلاع: Generic Host + DI + Serilog + IHttpClientFactory، ثم نافذة الدخول.
// لا اتصال مباشر بقاعدة البيانات (ADR-002) — كل البيانات عبر WeraApiClient.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;
using WeraPro.Desktop.Views;

namespace WeraPro.Desktop;

public sealed partial class App : Application
{
    public static IHost Host { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Host = BuildHost();

        // البوابة الأولى: تسجيل الدخول — لا Shell قبل جلسة موثقة (§11.2)
        var login = ActivatorUtilities.CreateInstance<LoginWindow>(Host.Services);
        login.Activate();
    }

    private static IHost BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
            })
            .UseSerilog((context, logger) =>
            {
                logger.MinimumLevel.Information()
                      .Enrich.FromLogContext()
                      .WriteTo.File(
                          System.IO.Path.Combine(
                              System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                              "WERA_PRO_HD", "logs", "desktop-.log"),
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
            })
            .Build();

        var logger = host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("WERA PRO HD Desktop بدأ الإقلاع (G3 — Online-first)");
        return host;
    }
}
