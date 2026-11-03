// Views/MediaLinkDialog.cs — WERA PRO HD Desktop (G5)
// ربط صورة بهدف واحد: منتج أو صيغة بيع — لا الاثنين معاً (§6.3 رفض خادمي
// ONE_TARGET_ONLY). حل الكود: المطابقة الدقيقة (باركود/SKU/PLU) للصيغة،
// وبحث المنتجات بالكود للمنتج — ثم تأكيد الاسم المعروض قبل الحفظ.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.Views;

/// <summary>مربع حوار الربط — يعرض ProductId/SkuId (واحد فقط) وMakePrimary عند القبول.</summary>
public sealed class MediaLinkDialog : ContentDialog
{
    private readonly WeraApiClient _api;
    private readonly ComboBox _typeBox = new() { Width = 220, Height = 36 };
    private readonly TextBox _codeBox = new()
    {
        Width = 220,
        Height = 36,
        PlaceholderText = "الكود (صيغة: SKU/باركود/PLU · منتج: كود المنتج)",
    };
    private readonly CheckBox _primaryCheck = new() { Content = "تعيينها صورة رئيسية للهدف", IsChecked = false };
    private readonly TextBlock _resolvedText = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12.5,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["WeraTextSecondaryBrush"],
    };
    private readonly Button _resolveButton = new() { Content = "تحقق", CornerRadius = new CornerRadius(10) };

    private long? _resolvedProductId;
    private long? _resolvedSkuId;

    public long? ProductId => _resolvedProductId;
    public long? SkuId => _resolvedSkuId;
    public bool MakePrimary => _primaryCheck.IsChecked == true;

    public MediaLinkDialog(WeraApiClient api, long mediaId)
    {
        _api = api;
        Title = $"ربط الأصل #{mediaId} بهدف";
        PrimaryButtonText = "ربط";
        CloseButtonText = "إلغاء";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;

        _typeBox.Items.Add("منتج");
        _typeBox.Items.Add("صيغة بيع (SKU)");
        _typeBox.SelectedIndex = 1;   // الافتراضي: صيغة بيع (الأكثر استخداماً في الربط)
        _resolveButton.Click += async (_, _) => await ResolveAsync();

        var panel = new StackPanel { Spacing = 10, MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "هدف واحد فقط — الاثنان معاً يرفضهما الخادم (ONE_TARGET_ONLY)",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["WeraTextMutedBrush"],
        });
        panel.Children.Add(_typeBox);
        panel.Children.Add(_codeBox);
        panel.Children.Add(_resolveButton);
        panel.Children.Add(_primaryCheck);
        panel.Children.Add(_resolvedText);
        Content = panel;
    }

    /// <summary>حل الكود إلى هدف موثوق: المطابقة الدقيقة للصيغة، والبحث بالكود للمنتج.</summary>
    private async Task ResolveAsync()
    {
        _resolvedProductId = null;
        _resolvedSkuId = null;
        IsPrimaryButtonEnabled = false;
        var code = (_codeBox.Text ?? string.Empty).Trim();
        if (code.Length < 2)
        {
            _resolvedText.Text = "أدخل كوداً صالحاً (حرفان فأكثر)";
            return;
        }

        var isProduct = _typeBox.SelectedIndex == 0;
        if (isProduct)
        {
            var search = await _api.SearchProductsAsync(code, null, null, 1, 25, default);
            if (!search.Ok || search.Value is null)
            {
                _resolvedText.Text = search.ErrorAr ?? "تعذر البحث";
                return;
            }
            var match = search.Value.Items.FirstOrDefault(p =>
                string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _resolvedText.Text = "لا منتج بهذا الكود بالضبط — تحقق من الكود";
                return;
            }
            _resolvedProductId = match.ProductId;
            _resolvedText.Text = "✔ منتج: " + match.NameAr + " (" + match.Code + ")";
        }
        else
        {
            var lookup = await _api.LookupByCodeAsync(code, default);
            if (!lookup.Ok || lookup.Value is null)
            {
                _resolvedText.Text = lookup.ErrorAr ?? "لا مطابقة دقيقة لهذا الكود (باركود/SKU/PLU)";
                return;
            }
            var hit = lookup.Value;
            _resolvedSkuId = hit.Sku.SkuId;
            _resolvedText.Text = "✔ صيغة بيع: " + hit.Product.NameAr
                + (hit.Sku.NameAr is null ? "" : " — " + hit.Sku.NameAr)
                + " (" + hit.Sku.SkuCode + " · " + hit.MatchKind + ")";
        }
        IsPrimaryButtonEnabled = _resolvedProductId is not null || _resolvedSkuId is not null;
    }
}
