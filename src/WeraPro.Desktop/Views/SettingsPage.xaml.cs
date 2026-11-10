// Views/SettingsPage.xaml.cs — WERA PRO HD Desktop (G-ADMIN)
// PAGE-52: تعديل إعداد بحوار يعرض القيمة الحالية ونوعها وقرارها المرجعي —
// التغيير يمر بالخادم (ops.settings.manage) ويوثق القديمة والجديدة في التدقيق.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm;

    public SettingsViewModel Vm => _vm;

    public SettingsPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<SettingsViewModel>();
        DataContext = _vm;

        foreach (var c in _vm.Categories)
        {
            CategoryFilter.Items.Add(new ComboBoxItem { Content = c, IsSelected = c == "الكل" });
        }
        CategoryFilter.SelectedIndex = 0;

        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ErrorAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    ErrorText.Text = _vm.ErrorAr ?? "";
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is ComboBoxItem item && item.Content is string category)
        {
            _vm.SelectedCategory = category;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.LoadAsync(default);

    private async void EditSetting_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SettingRow row) return;
        if (!_vm.CanManage)
        {
            _vm.ErrorAr = "تعديل الإعدادات يتطلب صلاحية ops.settings.manage — للمالك فقط (RBAC خادمي)";
            return;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = row.Key + " · النوع: " + row.ValueTypeAr + " · " + row.DecisionLabelAr,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 11.5, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraTextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = row.DescriptionAr, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        var valueBox = new TextBox { Header = "القيمة الجديدة", Text = row.ValueAr };
        var reasonBox = new TextBox { Header = "سبب التغيير (يُسجَّل في التدقيق مع القيمة القديمة والجديدة)" };
        panel.Children.Add(valueBox);
        panel.Children.Add(reasonBox);
        if (row.IsPending)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "هذا الإعداد توصية بانتظار حسم المالك — تعديلك هنا يحسمه ويُوثَّق باسمك.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusWarningBrush"],
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "تعديل إعداد نشاط", Content = panel,
            PrimaryButtonText = "حفظ التغيير", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(valueBox.Text))
        {
            _vm.ErrorAr = "القيمة مطلوبة — لا قيم فارغة لإعدادات نشاط";
            return;
        }
        await _vm.UpdateAsync(row, valueBox.Text.Trim(),
            string.IsNullOrWhiteSpace(reasonBox.Text) ? null : reasonBox.Text.Trim(), default);
    }
}
