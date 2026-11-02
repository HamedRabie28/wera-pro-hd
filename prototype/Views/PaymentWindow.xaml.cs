// PaymentWindow.xaml.cs — DEV DATA ONLY
using Microsoft.UI.Xaml;

namespace WeraPrototype.Views;

public sealed partial class PaymentWindow : Window
{
    public PaymentWindow()
    {
        InitializeComponent();
        Title = "نافذة الدفع (DEV DATA — G2 PROTOTYPE)";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // في النموذج: لا ترحيل فعلي (التنفيذ الحقيقي بـIdempotency في G7).
        Close();
    }
}
