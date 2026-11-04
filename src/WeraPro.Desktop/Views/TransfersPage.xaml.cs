// Views/TransfersPage.xaml.cs — WERA PRO HD Desktop (G6)
// التحويلات: إنشاء مسودة وإرسالها (خروج من الدفتر) واستلامها في الوجهة —
// عند رفض D-03 (نقص مؤقت) يفتح مسار اعتماد المشرف ثم إعادة الإرسال بمفتاحه.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class TransfersPage : Page
{
    private readonly TransfersViewModel _vm;

    public TransfersPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<TransfersViewModel>();
        DataContext = _vm;
        CreateButton.Visibility = _vm.CanOperate ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = _vm.CanOperate ? Visibility.Visible : Visibility.Collapsed;
        ReceiveButton.Visibility = _vm.CanOperate ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransfersViewModel.IsLoading))
            {
                _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                    if (_vm.InfoAr is { Length: > 0 }) InfoText.Text = _vm.InfoAr;
                });
            }
        };
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
        var panel = new StackPanel { Spacing = 8 };
        var fromBox = new ComboBox { PlaceholderText = "من مخزن", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var toBox = new ComboBox { PlaceholderText = "إلى مخزن", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var skuBox = new TextBox { PlaceholderText = "كود الصيغة" };
        var qtyBox = new NumberBox { PlaceholderText = "الكمية بالوحدة الأساسية", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.Primitives.NumberBoxSpinButtonPlacementMode.Compact };
        panel.Children.Add(fromBox);
        panel.Children.Add(toBox);
        panel.Children.Add(skuBox);
        panel.Children.Add(qtyBox);
        foreach (var w in _vm.Warehouses)
        {
            fromBox.Items.Add(w);
            toBox.Items.Add(w);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "تحويل جديد (مسودة — لا حركة قبل الإرسال)",
            Content = panel, PrimaryButtonText = "إنشاء", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (fromBox.SelectedItem is not WeraPro.Contracts.V1.WarehouseDto from
            || toBox.SelectedItem is not WeraPro.Contracts.V1.WarehouseDto to
            || from.WarehouseId == to.WarehouseId)
        {
            _vm.ErrorAr = "اختر مخزنين مختلفين";
            return;
        }
        var lookup = await api.LookupByCodeAsync(skuBox.Text.Trim(), default);
        if (!lookup.Ok)
        {
            _vm.ErrorAr = "كود الصيغة غير معروف";
            return;
        }
        await _vm.CreateAsync(from, to, lookup.Value!.SkuId, (decimal)qtyBox.Value, default);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (TransfersList.SelectedItem is not TransferRow row) return;
        await _vm.SendAsync(row, default);

        // عند رفض D-03: افتح مسار الاعتماد (طلب → بت مشرف → إعادة إرسال بالمفتاح)
        if (_vm.ErrorAr is { } err && err.Contains("اعتماد", StringComparison.Ordinal))
        {
            var requestApproval = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "التحويل ينزل الرصيد تحت الصفر (D-03)",
                Content = err + "\n\nأنشئ طلب اعتماد الآن؟ سيبت فيه من يملك security.approval.decide ثم تُعيد الإرسال بمفتاحه.",
                PrimaryButtonText = "أنشئ طلب اعتماد", CloseButtonText = "لاحقاً",
            };
            if (await requestApproval.ShowAsync() != ContentDialogResult.Primary) return;
            var api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
            var created = await api.CreateApprovalAsync(new WeraPro.Contracts.V1.CreateApprovalRequest(
                "inv.transfer", row.TransferId, "نقص مؤقت بتحويل مخزني — موافقة مطلوبة"), default);
            if (!created.Ok)
            {
                _vm.ErrorAr = created.ErrorAr;
                return;
            }
            _vm.InfoAr = $"أُنشئ طلب الاعتماد #{created.Value!.ApprovalId} — بعد موافقة المشرف أعد الإرسال بالمفتاح";
            _vm.ErrorAr = null;
            var approvalBox = new NumberBox { Minimum = 1, Header = "رقم الاعتماد المُوافَق عليه من المشرف", SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.Primitives.NumberBoxSpinButtonPlacementMode.Compact };
            var resend = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "أعد الإرسال باعتماد",
                Content = approvalBox,
                PrimaryButtonText = "إرسال", CloseButtonText = "لاحقاً",
            };
            if (await resend.ShowAsync() == ContentDialogResult.Primary && approvalBox.Value >= 1)
            {
                await _vm.SendWithApprovalAsync(row, (long)approvalBox.Value, default);
            }
        }
    }

    private async void Receive_Click(object sender, RoutedEventArgs e)
    {
        if (TransfersList.SelectedItem is TransferRow row) await _vm.ReceiveAsync(row, default);
    }
}
