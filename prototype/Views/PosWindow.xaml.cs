// PosWindow.xaml.cs — DEV DATA ONLY
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WeraPrototype.ViewModels;

namespace WeraPrototype.Views;

public sealed partial class PosWindow : Window
{
    public CartLine[] Cart => DevData.Cart;

    public PosWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — POS (DEV DATA — G2 PROTOTYPE)";
        // الحد الأدنى 1280×720 (POS_VISUAL_SPEC §1)
        if (Content is FrameworkElement fe) fe.MinWidth = 1280;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(1366, 768));
        // البؤرة الدائمة لحقل الباركود (keyboard-first §9)
        Loaded += (_, _) => BarcodeField.Focus(FocusState.Programmatic);
    }

    private void OpenWeight_Click(object sender, RoutedEventArgs e)
    {
        var w = new WeightWindow(DevData.RefPricePerKg) { FlowDirection = FlowDirection.RightToLeft };
        w.Activate();
    }

    private void OpenPayment_Click(object sender, RoutedEventArgs e)
    {
        var w = new PaymentWindow { FlowDirection = FlowDirection.RightToLeft };
        w.Activate();
    }
}
