// Views/SalesLogPage.xaml.cs — WERA PRO HD Desktop (G7 · سجلات البيع)
// PAGE-03/06/08: فواتير محطة الجلسة (تفاصيل/إعادة طباعة/إلغاء مسودة) +
// السلال المعلقة (عرض اللقطة — الاستعادة من POS) + مطابقة الورديات وحركات الدرج.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class SalesLogPage : Page
{
    public SalesLogViewModel Vm { get; }

    public SalesLogPage()
    {
        InitializeComponent();
        Vm = App.Host.Services.GetRequiredService<SalesLogViewModel>();
        DataContext = Vm;
        _ = Vm.LoadInvoicesAsync(default);
        _ = Vm.LoadShiftsAsync(default);

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SalesLogViewModel.ErrorAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    ErrorText.Text = Vm.ErrorAr ?? "";
                    ErrorText.Visibility = string.IsNullOrEmpty(Vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };

        // تعبئة قائمة الورديات في صندوق المطابقة (PAGE-08)
        ShiftsCombo.ItemsSource = Vm.Shifts;
    }

    private void Pivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return; // احتياط أثناء التهيئة — التحميل الافتتاحي في المنشئ
        switch ((sender as Pivot)?.SelectedIndex)
        {
            case 0: _ = Vm.LoadInvoicesAsync(default); break;
            case 1: _ = Vm.LoadHeldCartsAsync(default); break;
            case 2: _ = Vm.LoadShiftsAsync(default); break;
        }
    }

    private void LimitFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // قد يُطلق أثناء InitializeComponent (العنصر المحدد افتراضياً) قبل تهيئة Vm — التحميل الافتتاحي في المنشئ
        if (Vm is null) return;
        if ((sender as ComboBox)?.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var limit))
        {
            Vm.Limit = limit;
            _ = Vm.LoadInvoicesAsync(default);
        }
    }

    private async void RefreshInvoices_Click(object sender, RoutedEventArgs e) => await Vm.LoadInvoicesAsync(default);

    private void InvoicesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (InvoicesList.SelectedItem is InvoiceRow row)
        {
            OpenDetails(row.InvoiceId);
        }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is InvoiceRow row)
        {
            OpenDetails(row.InvoiceId);
        }
    }

    private static void OpenDetails(long invoiceId)
    {
        var window = new InvoiceDetailWindow(invoiceId);
        window.Activate();
    }

    private async void Reprint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InvoiceRow row) return;
        if (!Vm.CanReprint)
        {
            Vm.ErrorAr = "إعادة الطباعة تتطلب صلاحية printing.reprint";
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"إعادة طباعة {row.InvoiceNumber}",
            Content = new TextBlock
            {
                Text = "ستُنشأ نسخة موسومة REPRINT تختلف عن نسخة العميل الأصلية وتُوثَّق في سجل مهام الطباعة (§29.9).",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MinWidth = 320,
            },
            PrimaryButtonText = "إعادة الطباعة", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Vm.ReprintAsync(row.InvoiceId, default);
    }

    private async void Void_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InvoiceRow row) return;
        if (!Vm.CanVoid)
        {
            Vm.ErrorAr = "إلغاء الفواتير يتطلب صلاحية sales.invoice.void — OWNER/MANAGER";
            return;
        }
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        var reasonBox = new TextBox { Header = "سبب الإلغاء (يُوثَّق في التدقيق)" };
        panel.Children.Add(reasonBox);
        panel.Children.Add(new TextBlock
        {
            Text = "الإلغاء للمسودات فقط — الفاتورة المرحّلة لا تُلغى بل يُنشأ لها مرتجع (D-04).",
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusWarningBrush"],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"إلغاء المسودة {row.InvoiceNumber}",
            Content = panel, PrimaryButtonText = "إلغاء الفاتورة", CloseButtonText = "تراجع",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(reasonBox.Text))
        {
            Vm.ErrorAr = "سبب الإلغاء إلزامي — لا إلغاء بلا موثوقية";
            return;
        }
        await Vm.VoidAsync(row, reasonBox.Text.Trim(), default);
    }

    // ── السلال المعلقة (PAGE-06) ──

    private async void RefreshHeldCarts_Click(object sender, RoutedEventArgs e) => await Vm.LoadHeldCartsAsync(default);

    private void OpenPosForRestore_Click(object sender, RoutedEventArgs e)
    {
        // الاستعادة الحقيقية تعيد السلة إلى حالة الكاشير في POS (§9.5) — الشِل يفتح النافذة
        var pos = ActivatorUtilities.CreateInstance<PosWindow>(App.Host.Services);
        pos.Activate();
    }

    private async void ViewCart_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HeldCartRow row) return;
        var (customer, lines) = HeldCartRow.ParseSnapshot(row.Source.CartJson);
        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = $"سلة #{row.CartId} — علّقها {row.HeldBy} في {row.HeldAtAr}" +
                   (row.Source.NoteAr is { Length: > 0 } n ? "\nالملاحظة: " + n : ""),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraTextMutedBrush"],
        });
        panel.Children.Add(new TextBlock { Text = "الزبون: " + customer, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var list = new ListView { MaxHeight = 260, SelectionMode = ListViewSelectionMode.None };
        foreach (var l in lines) list.Items.Add(l);
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock
        {
            Text = "العرض فقط لا ينشئ حركة — الاستعادة تتم من نافذة نقطة البيع (تعيد السلة لحالة الكاشير).",
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusWarningBrush"],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "لقطة السلة المعلقة (§9.5)",
            Content = panel, CloseButtonText = "إغلاق",
        };
        _ = await dialog.ShowAsync();
    }

    // ── المدفوعات والمطابقة (PAGE-08) ──

    private void ShiftsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // الاختيار وحده لا يحمّل — زر «عرض المطابقة» يحدد النية صراحة
    }

    private async void LoadRecon_Click(object sender, RoutedEventArgs e)
    {
        if (ShiftsCombo.SelectedItem is not ShiftOptionRow shift)
        {
            Vm.ErrorAr = "اختر وردية أولاً — كل وردية وحدة مطابقة مستقلة (§14.1)";
            return;
        }
        Vm.ErrorAr = null;
        await Vm.LoadReconAsync(shift.ShiftId, default);
    }
}
