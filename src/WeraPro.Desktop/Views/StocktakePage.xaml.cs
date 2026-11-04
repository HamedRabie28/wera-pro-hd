// Views/StocktakePage.xaml.cs — WERA PRO HD Desktop (G6)
// الجرد: التسجيل بلقطة الدفتر، والإغلاق والمطابقة والاعتماد من غير الجارّ —
// الأزرار تعكس صلاحيات الجلسة (خادمية القرارات، عرضية الإخفاءات §18).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class StocktakePage : Page
{
    private readonly StocktakeViewModel _vm;

    public StocktakePage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<StocktakeViewModel>();
        DataContext = _vm;
        RecordButton.Visibility = _vm.CanRun ? Visibility.Visible : Visibility.Collapsed;
        OpenMenuButton.Visibility = _vm.CanRun ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = _vm.CanRun ? Visibility.Visible : Visibility.Collapsed;
        ReconcileButton.Visibility = _vm.CanRun ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _vm.CanRun ? Visibility.Visible : Visibility.Collapsed;
        ApproveButton.Visibility = _vm.CanApprove ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StocktakeViewModel.IsLoading))
            {
                _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                    InfoText.Text = _vm.InfoAr is { Length: > 0 } ? _vm.InfoAr : "اختر جلسة لعرض أسطرها ولقطة الدفتر والفرق";
                });
            }
        };
        _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, BuildWarehouseMenu);
    }

    private void BuildWarehouseMenu()
    {
        WarehouseMenu.Items.Clear();
        foreach (var w in _vm.Warehouses)
        {
            var item = new MenuFlyoutItem { Text = w.NameAr + " (" + w.Code + ")", Tag = w };
            item.Click += async (_, _) =>
            {
                var notesInput = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "ملاحظات الجرد (اختيارية)",
                    Content = new TextBox { PlaceholderText = "مثال: جرد نهاية الأسبوع" },
                    PrimaryButtonText = "فتح", CloseButtonText = "إلغاء",
                };
                if (await notesInput.ShowAsync() == ContentDialogResult.Primary
                    && notesInput.Content is TextBox tb)
                {
                    await _vm.OpenSessionAsync(w, string.IsNullOrWhiteSpace(tb.Text) ? null : tb.Text.Trim(), default);
                }
            };
            WarehouseMenu.Items.Add(item);
        }
    }

    private async void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionsList.SelectedItem is StocktakeRow row)
        {
            await _vm.SelectAsync(row, default);
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null)
        {
            _vm.ErrorAr = "اختر جلسة مفتوحة أولاً";
            return;
        }
        var panel = new StackPanel { Spacing = 8 };
        var skuBox = new TextBox { PlaceholderText = "كود الصيغة (مثال: G6-SCN-01-BAG)" };
        var qtyBox = new NumberBox { PlaceholderText = "الكمية المعدودة", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.Primitives.NumberBoxSpinButtonPlacementMode.Compact };
        panel.Children.Add(skuBox);
        panel.Children.Add(qtyBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "تسجيل عدّ — بلقطة الدفتر وقت العد",
            Content = panel, PrimaryButtonText = "تسجيل", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // حل كود الصيغة إلى معرف عبر بحث الكتالوج الدقيق
        var api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
        var lookup = await api.LookupByCodeAsync(skuBox.Text.Trim(), default);
        if (!lookup.Ok)
        {
            _vm.ErrorAr = "كود الصيغة غير معروف: " + skuBox.Text.Trim();
            return;
        }
        await _vm.RecordAsync(lookup.Value!.SkuId, (decimal)qtyBox.Value, default);
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await _vm.ActionAsync("close", default);

    private async void Reconcile_Click(object sender, RoutedEventArgs e) => await _vm.ActionAsync("reconcile", default);

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await _vm.ActionAsync("cancel", default);

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "اعتماد تسوية الجرد",
            Content = "سيُنشأ مستند التسوية وتُرحّل حركة لكل صنف ذي فرق (الفرق الصفر بلا حركة)." +
                      "\nالمعتمد يجب أن يكون غير من قام بالجرد (قيد الخادم).",
            PrimaryButtonText = "اعتماد", CloseButtonText = "إلغاء",
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            await _vm.ActionAsync("approve", default);
        }
    }
}
