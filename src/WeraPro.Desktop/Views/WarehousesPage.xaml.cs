// Views/WarehousesPage.xaml.cs — WERA PRO HD Desktop (G6)
// إدارة المخازن: إنشاء رئيسي/فرعي/خارجي ضمن فرع، وتعطيل بلا حذف —
// الأزرار للمالك/المدير، والقائمة للجميع (رصد PAGE-28).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class WarehousesPage : Page
{
    private readonly WarehousesViewModel _vm;

    public WarehousesPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<WarehousesViewModel>();
        DataContext = _vm;
        CreateButton.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;
        ToggleStateButton.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WarehousesViewModel.IsLoading))
            {
                _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var branchBox = new ComboBox { PlaceholderText = "الفرع", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var codeBox = new TextBox { PlaceholderText = "كود المخزن (مثال: WH-SUB-01)" };
        var nameBox = new TextBox { PlaceholderText = "اسم المخزن" };
        var typeBox = new ComboBox { PlaceholderText = "النوع", HorizontalAlignment = HorizontalAlignment.Stretch };
        typeBox.Items.Add("فرعي (SUB) — داخل النشاط");
        typeBox.Items.Add("خارجي (EXTERNAL) — مخزن خارجي");
        typeBox.SelectedIndex = 0;
        panel.Children.Add(branchBox);
        panel.Children.Add(codeBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(typeBox);
        foreach (var b in _vm.Branches) branchBox.Items.Add(b);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "مخزن جديد — التعطيل لاحقاً لا الحذف أبداً",
            Content = panel, PrimaryButtonText = "إنشاء", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (branchBox.SelectedItem is not WeraPro.Contracts.V1.BranchDto branch
            || string.IsNullOrWhiteSpace(codeBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            _vm.ErrorAr = "أكمل الفرع والكود والاسم";
            return;
        }
        var whType = typeBox.SelectedIndex == 1 ? "EXTERNAL" : "SUB";
        await _vm.CreateAsync(branch, codeBox.Text.Trim().ToUpperInvariant(), nameBox.Text.Trim(), whType, default);
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (WarehousesList.SelectedItem is WarehouseRow row)
        {
            await _vm.ToggleAsync(row, default);
        }
    }
}
