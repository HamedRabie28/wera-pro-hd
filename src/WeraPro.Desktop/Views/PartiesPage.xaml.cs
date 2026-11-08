// Views/PartiesPage.xaml.cs — WERA PRO HD Desktop (G6)
// الأطراف: الموردون والعملاء — التعطيل لا الحذف، وكشف الحساب بنافذة
// مستقلة (نقرة مزدوجة) بفلترة الفترة ونوع المستند (PAGE-30/32).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class PartiesPage : Page
{
    private readonly PartiesViewModel _vm;

    /// <summary>كشف الـ ViewModel لربط x:Bind في XAML (تمييز الأحرف إلزامي — _vm خاصة لا تُرى).</summary>
    public PartiesViewModel Vm => _vm;

    public PartiesPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<PartiesViewModel>();
        DataContext = _vm;
        NewSupplierButton.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;
        NewCustomerButton.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PartiesViewModel.ErrorAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
    }

    private async void SupplierSearch_Click(object sender, RoutedEventArgs e) => await _vm.LoadSuppliersAsync(default);

    private void SupplierSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _vm.SupplierSearch = SupplierSearchBox.Text;
            _ = _vm.LoadSuppliersAsync(default);
        }
    }

    private async void CustomerSearch_Click(object sender, RoutedEventArgs e) => await _vm.LoadCustomersAsync(default);

    private void CustomerSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _vm.CustomerSearch = CustomerSearchBox.Text;
            _ = _vm.LoadCustomersAsync(default);
        }
    }

    private async void NewSupplier_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var codeBox = new TextBox { PlaceholderText = "كود المورد" };
        var nameBox = new TextBox { PlaceholderText = "الاسم" };
        var phoneBox = new TextBox { PlaceholderText = "الهاتف" };
        var termsBox = new NumberBox { PlaceholderText = "أيام السداد (0 = نقدي)", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        panel.Children.Add(codeBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(phoneBox);
        panel.Children.Add(termsBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "مورد جديد", Content = panel,
            PrimaryButtonText = "إنشاء", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(codeBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            _vm.ErrorAr = "أكمل الكود والاسم";
            return;
        }
        await _vm.CreateSupplierAsync(codeBox.Text.Trim().ToUpperInvariant(), nameBox.Text.Trim(),
            string.IsNullOrWhiteSpace(phoneBox.Text) ? null : phoneBox.Text.Trim(), (int)termsBox.Value, default);
    }

    private async void NewCustomer_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var codeBox = new TextBox { PlaceholderText = "كود العميل" };
        var nameBox = new TextBox { PlaceholderText = "الاسم" };
        var phoneBox = new TextBox { PlaceholderText = "الهاتف" };
        panel.Children.Add(codeBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(phoneBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "عميل جديد — الآجل معطل بانتظار حسم D-05", Content = panel,
            PrimaryButtonText = "إنشاء", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(codeBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            _vm.ErrorAr = "أكمل الكود والاسم";
            return;
        }
        await _vm.CreateCustomerAsync(codeBox.Text.Trim().ToUpperInvariant(), nameBox.Text.Trim(),
            string.IsNullOrWhiteSpace(phoneBox.Text) ? null : phoneBox.Text.Trim(), default);
    }

    private async void SuppliersList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // كشف حساب المورد (PAGE-32): افتتاحي + حركة + ختامي قابل للتصفية
        if (SuppliersList.SelectedItem is SupplierRow row)
        {
            var statement = await _vm.GetSupplierStatementAsync(row, DateOnly.FromDateTime(DateTime.Today.AddDays(-90)), DateOnly.FromDateTime(DateTime.Today), default);
            if (statement is not null)
            {
                var window = ActivatorUtilities.CreateInstance<PartyStatementWindow>(App.Host.Services);
                // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
                window.Show(statement, $"كشف حساب المورد — {row.NameAr}");
                window.Activate();
            }
        }
    }

    private async void CustomersList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // كشف حساب العميل (PAGE-30): فواتير آجلة G7 وسندات G8 تغذيه — في G6 البنية جاهزة بصدق
        if (CustomersList.SelectedItem is CustomerRow row)
        {
            var statement = await _vm.GetCustomerStatementAsync(row, DateOnly.FromDateTime(DateTime.Today.AddDays(-90)), DateOnly.FromDateTime(DateTime.Today), default);
            if (statement is not null)
            {
                var window = ActivatorUtilities.CreateInstance<PartyStatementWindow>(App.Host.Services);
                // RTL مضبوط في جذر نافذة XAML (Window لا تملك FlowDirection)
                window.Show(statement, $"كشف حساب العميل — {row.NameAr}");
                window.Activate();
            }
        }
    }
}
