// CatalogPage.xaml.cs — DEV DATA ONLY
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPrototype.ViewModels;

namespace WeraPrototype.Views;

public sealed partial class CatalogPage : Page
{
    public ProductRow[] Rows => DevData.Products;

    public CatalogPage() => InitializeComponent();

    private void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ProductEditorPage));
    }
}
