// Views/ShiftCloseDialog.cs — WERA PRO HD Desktop (G8)
// إقفال الوردية ومراجعة النقدية (PAGE-34 · §14.1): المتوقع من الدفتر خادمياً،
// والفعلي من عدّ موثق، والفارق يستلزم سبباً واعتماد مشرف — كل التحقق في الخادم.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;

namespace WeraPro.Desktop.Views;

/// <summary>حوار إقفال الوردية: مطابقة حية من الخادم ثم إغلاق بعدّ وسبب عند الفارق.</summary>
public sealed class ShiftCloseDialog : ContentDialog
{
    private readonly Services.WeraApiClient _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();

    private readonly TextBlock _expectedText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _componentsText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
    private readonly NumberBox _countedBox = new()
    {
        Header = "النقدية الفعلية المعدودة",
        Minimum = 0,
        SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact,
    };
    private readonly TextBox _reasonBox = new() { PlaceholderText = "سبب الفارق (إلزامي عند فارق ≠ 0 — ≥ 3 محارف)" };
    private readonly TextBox _notesBox = new() { PlaceholderText = "ملاحظات العدّ (اختياري)" };
    private readonly TextBlock _hintText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };

    public ShiftCloseResponse? CloseResult { get; private set; }

    public ShiftCloseDialog(XamlRoot root, ShiftReconciliationDto reconciliation)
    {
        XamlRoot = root;
        Title = $"إقفال الوردية #{reconciliation.Shift.ShiftNumber} — {reconciliation.Shift.StationNameAr}";
        PrimaryButtonText = "إغلاق الوردية";
        CloseButtonText = "إلغاء";

        var r = reconciliation;
        _expectedText.Text = $"المتوقع في الدرج: {r.ExpectedInDrawer:N2} ج.م (من الدفتر §14.1)";
        _componentsText.Text = $"افتتاحي {r.OpeningFloat:N2} + نقدي {r.CashSales:N2} − ردود {r.CashRefunds:N2} − مصروفات درج {r.DrawerExpenses:N2}"
            + $" + إيداعات {r.VoucherDeposits:N2} − سحوبات {r.VoucherWithdrawals:N2} + وارد {r.TransfersIn:N2} − صادر {r.TransfersOut:N2}"
            + $"\nالبطاقة {r.CardCollected:N2} (لدى المزود — ليست في الدرج) · الآجل {r.CreditIssued:N2} (ذمم) · {r.InvoiceCount} فاتورة";
        _hintText.Text = "فصل الوسائل (§14.1 ق4): البطاقة والآجل لا يدخلان متوقع الدرج — والفارق يظل ظاهراً على الوردية حتى تسويته بسند موثق.";
        _countedBox.Value = (double)r.ExpectedInDrawer;

        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        panel.Children.Add(_expectedText);
        panel.Children.Add(_componentsText);
        panel.Children.Add(_countedBox);
        panel.Children.Add(_reasonBox);
        panel.Children.Add(_notesBox);
        panel.Children.Add(_hintText);
        Content = panel;
    }

    public decimal CountedCash => (decimal)_countedBox.Value;
    public string? VarianceReason => string.IsNullOrWhiteSpace(_reasonBox.Text) ? null : _reasonBox.Text.Trim();
    public string? Notes => string.IsNullOrWhiteSpace(_notesBox.Text) ? null : _notesBox.Text.Trim();
}
