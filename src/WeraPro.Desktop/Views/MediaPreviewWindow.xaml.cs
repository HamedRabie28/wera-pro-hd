// Views/MediaPreviewWindow.xaml.cs — WERA PRO HD Desktop (G5)
// معاينة أصل: تطلب نسخة preview (≤1024) من الخادم وتفكها على خيط UI —
// لا تحميل الأصل الكامل ولا مسارات محلية (بايتات عبر API فقط).
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.Views;

public sealed partial class MediaPreviewWindow : Window
{
    private readonly WeraApiClient _api;

    public MediaPreviewWindow(long mediaId)
    {
        InitializeComponent();
        Title = $"WERA PRO HD — معاينة الأصل #{mediaId}";
        App.ActiveWindow = this;
        _api = App.Host.Services.GetRequiredService<WeraApiClient>();
        _ = LoadAsync(mediaId);
    }

    private async Task LoadAsync(long mediaId)
    {
        LoadingRing.IsActive = true;
        StatusText.Text = "جارٍ جلب نسخة المعاينة…";
        try
        {
            var fetch = await _api.GetMediaVariantAsync(mediaId, "preview", default);
            if (fetch is not { Ok: true, Bytes: { Length: > 0 } bytes })
            {
                StatusText.Text = fetch.ErrorAr ?? "تعذر جلب المعاينة";
                return;
            }
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            PreviewImage.Source = image;
            StatusText.Text = $"{fetch.MimeType} · {bytes.Length / 1024.0:N0} KB — معاينة قابلة للتقريب";
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذر عرض المعاينة — " + ex.Message;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }
}
