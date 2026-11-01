// HomePage.xaml.cs — DEV DATA ONLY
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WeraPrototype.ViewModels;

namespace WeraPrototype.Views;

public sealed partial class HomePage : Page
{
    public AlertItem[] Alerts => DevData.Alerts;

    public HomePage() => InitializeComponent();

    private void OpenPos_Click(object sender, RoutedEventArgs e) => (App.Shell as ShellWindow)?.OpenPos();

    private void NewPurchaseInvoice_Click(object sender, RoutedEventArgs e)
    {
        // فاتورة مشتريات داخلية — البناء الفعلي في G6 (PAGE-24). في النموذج: لا فعل وهمي.
    }
}
