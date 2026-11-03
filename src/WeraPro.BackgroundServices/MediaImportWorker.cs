namespace WeraPro.BackgroundServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeraPro.Application.Media;

/// <summary>
/// عامل استيراد الصور (G5 — §6.1 «تحميل الصور ومعالجتها في background
/// workers»): يستقصي المهام RUNNING ويعالج ملفاً واحداً في كل دورة —
/// لا يوازي داخل المهمة (حدود الذاكرة)، والإلغاء يُفحص قبل كل التقاط.
/// الخمول 500ms؛ عند إنجاز ملف تعود الدورة فوراً.
/// </summary>
public sealed partial class MediaImportWorker(
    IServiceScopeFactory scopes,
    ILogger<MediaImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<MediaImportFileProcessor>();
                worked = await processor.RunOneCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCycleFailure(ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            if (!worked)
            {
                try
                {
                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        LogStoppedCleanly();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "عامل استيراد الوسائط بدأ")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "عامل استيراد الوسائط توقف بنظافة")]
    private partial void LogStoppedCleanly();

    [LoggerMessage(Level = LogLevel.Error, Message = "دورة عامل الاستيراد فشلت — إعادة المحاولة بعد ثانيتين")]
    private partial void LogCycleFailure(Exception ex);
}
