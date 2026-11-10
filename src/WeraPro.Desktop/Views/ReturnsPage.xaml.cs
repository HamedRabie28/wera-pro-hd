// Views/ReturnsPage.xaml.cs — WERA PRO HD Desktop (G7 · PAGE-05)
// سير المرتجع الكامل: تحميل الأصل → كميات لكل سطر بحد المتبقي → وسيلة الرد
// الأصلية (D-04) → طلب اعتماد وبتّه (V005) → ترحيل بمفتاح Idempotency —
// كل رفض خادمي يُعرض بنصه الصادق لا برسالة عامة.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class ReturnsPage : Page
{
    public ReturnsViewModel Vm { get; }

    public ReturnsPage()
    {
        InitializeComponent();
        Vm = App.Host.Services.GetRequiredService<ReturnsViewModel>();
        DataContext = Vm;
        _ = Vm.LoadApprovalsAsync(default);

        ApprovalsHint.Text = Vm.CanDecideApprovals
            ? "جلستك تملك البت (security.approval.decide) — وافق أو ارفض ثم رحّل من النموذج"
            : "البت يتطلب صلاحية security.approval.decide (مشرف فأعلى) — الطلبات هنا للمتابعة فقط";
        PostButton.IsEnabled = Vm.CanPostRefund;
        if (!Vm.CanPostRefund)
        {
            Vm.StatusAr = "ترحيل المرتجع يتطلب صلاحية sales.return.approve — OWNER/MANAGER/SUPERVISOR (D-04)";
        }

        Vm.PropertyChanged += (_, e) =>
        {
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (e.PropertyName == nameof(ReturnsViewModel.ErrorAr))
                {
                    ErrorText.Text = Vm.ErrorAr ?? "";
                    ErrorText.Visibility = string.IsNullOrEmpty(Vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                }
                if (e.PropertyName == nameof(ReturnsViewModel.ReadyApprovalId))
                {
                    ApprovalText.Text = Vm.ReadyApprovalId > 0
                        ? "اعتماد جاهز #" + Vm.ReadyApprovalId
                        : "";
                }
            });
        };
    }

    private async void LoadInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(InvoiceBox.Text.Trim(), out var invoiceId))
        {
            Vm.ErrorAr = "أدخل معرف الفاتورة الرقمي (InvoiceId) — تجده في سجل المبيعات (PAGE-03)";
            return;
        }
        Vm.ErrorAr = null;
        await Vm.LoadInvoiceAsync(invoiceId, default);
    }

    private async void RequestApproval_Click(object sender, RoutedEventArgs e)
    {
        await Vm.RequestApprovalAsync(ReasonBox.Text, default);
    }

    private async void PostRefund_Click(object sender, RoutedEventArgs e)
    {
        var method = (MethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "CASH";
        var posted = await Vm.PostRefundAsync(method, RestockBox.IsChecked == true, ReasonBox.Text, default);
        if (posted)
        {
            ReasonBox.Text = "";
        }
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ApprovalRow row) return;
        var reason = ReasonBox.Text is { Length: > 0 } ? ReasonBox.Text.Trim() : null;
        await Vm.DecideAsync(row, approve: true, reason, default);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ApprovalRow row) return;
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        var reasonBox = new TextBox { Header = "سبب الرفض (يُوثَّق في التدقيق)" };
        panel.Children.Add(reasonBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "رفض طلب اعتماد مرتجع #" + row.ApprovalId,
            Content = panel, PrimaryButtonText = "رفض الطلب", CloseButtonText = "تراجع",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Vm.DecideAsync(row, approve: false,
            string.IsNullOrWhiteSpace(reasonBox.Text) ? "رفض" : reasonBox.Text.Trim(), default);
    }
}
