// Views/PosWindow.xaml.cs — WERA PRO HD Desktop (G7 · §9)
// محطة الكاشير: كل أمر عبر Use Cases وAPI (لا حسابات مالية هنا §9.0 ق10)،
// المجاميع من المعاينة الخادمية بعد كل تغيير، والاختصارات F1..F12 (§9.10).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class PosWindow : Window
{
    private readonly PosViewModel _vm;
    private readonly WeraApiClient _api;
    private readonly ConnectivityService _connectivity;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _heartbeatTimer;

    public PosWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — POS";
        MinWidth = 1280;
        MinHeight = 720;

        _vm = App.Host.Services.GetRequiredService<PosViewModel>();
        _api = App.Host.Services.GetRequiredService<WeraApiClient>();
        _connectivity = App.Host.Services.GetRequiredService<ConnectivityService>();

        ViewModel = _vm;
        SessionText.Text = $"{_vm.CashierDisplayAr} · {_vm.StationDisplayAr} · {_vm.ScopeDisplayAr}";
        _vm.PropertyChanged += (_, e) => _ = DispatcherQueue.TryRunAsync(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (e.PropertyName == nameof(PosViewModel.StatusAr))
                {
                    StatusText.Text = _vm.StatusAr;
                }
                else if (e.PropertyName == nameof(PosViewModel.Totals))
                {
                    RefreshTotals();
                }
                else if (e.PropertyName == nameof(PosViewModel.ShiftInfoAr))
                {
                    ShiftText.Text = _vm.ShiftInfoAr;
                }
            });

        _ = LoadFoundationAsync();

        _connectivity.StateChanged += () =>
            _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshConnection);

        _heartbeatTimer = DispatcherQueue.CreateTimer();
        _heartbeatTimer.Interval = TimeSpan.FromSeconds(30);
        _heartbeatTimer.Tick += async (_, _) => await _connectivity.ProbeAsync(default);
        _heartbeatTimer.Start();
        _ = _connectivity.ProbeAsync(default);
        RefreshConnection();
        BarcodeField.Focus(FocusState.Programmatic);   // بؤرة التركيز الدائمة (§9.1B)
    }

    public PosViewModel ViewModel { get; }

    private async Task LoadFoundationAsync()
    {
        await _vm.LoadFoundationAsync(default);
        await DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            ShiftText.Text = _vm.ShiftInfoAr;
            PrinterText.Text = _vm.PrinterInfoAr;
            CardText.Text = _vm.CardInfoAr;
            StatusText.Text = _vm.StatusAr;
        });
    }

    private void RefreshTotals()
    {
        var t = _vm.Totals;
        GrossText.Text = t is null ? "—" : $"{t.GrossTotal:0.00}";
        TaxText.Text = t is null ? "—" : $"{t.TaxTotal:0.00}";
        DiscountText.Text = t is null ? "—" : $"{t.LineDiscountTotal + t.InvoiceDiscount:0.00}";
        DueText.Text = t is null ? "—" : $"{t.DueAmount:0.00}";
        ChangeText.Text = t is null || t.ChangeDue <= 0 ? "الفكة للعميل: —" : $"الفكة للعميل: {t.ChangeDue:0.00}";
        PayButton.IsEnabled = _vm.Cart.Count > 0;
    }

    private void RefreshConnection()
    {
        ConnText.Text = _connectivity.DisplayAr;
        ConnDot.Fill = _connectivity.State == ConnectionState.Online
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraActionPrimaryInkBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"];
    }

    // ── الالتقاط: Enter يعتمد أمر الحقل النشط (§9.10) ───────────────────────

    private async void OnBarcodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }
        var code = BarcodeField.Text.Trim();
        if (code.Length == 0)
        {
            return;
        }
        BarcodeField.Text = string.Empty;
        await _vm.CaptureAsync(code, line => OpenWeightDialogAsync(line), default);
        await DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshTotals);
    }

    private async Task<bool> OpenWeightDialogAsync(CartLineViewModel line)
    {
        var dialog = new WeightSaleDialog(Content.XamlRoot, line);
        _ = await dialog.ShowAsync();
        if (dialog.Accepted)
        {
            line.WeightKg = dialog.AcceptedWeightKg;
            line.WeightSource = dialog.AcceptedSource;
            line.AmountTarget = dialog.AcceptedAmountTarget;
            await _vm.PreviewAsync();
            await DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshTotals);
            return true;
        }
        return false;
    }

    // ── أوامر الأزرار (§9.2 أدوات الكاشير) ──────────────────────────────────

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        var code = BarcodeField.Text.Trim();
        if (code.Length > 0)
        {
            await _vm.CaptureAsync(code, line => OpenWeightDialogAsync(line), default);
            BarcodeField.Text = string.Empty;
            await DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshTotals);
            return;
        }
        // بحث نصي (F2): قائمة نتائج محدودة خادمياً
        var searchDialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "بحث منتج (نتائج محدودة — ترقيم خادمي §9.2)",
            PrimaryButtonText = "إضافة",
            CloseButtonText = "إلغاء",
            Content = CreateSearchList(),
        };
        _ = await searchDialog.ShowAsync();
    }

    private ListView CreateSearchList()
    {
        var list = new ListView { DisplayMemberPath = nameof(PosItemDto.ProductNameAr), MaxHeight = 420 };
        _ = LoadSearchAsync(list, "ألبان");
        return list;
    }

    private async Task LoadSearchAsync(ListView list, string query)
    {
        var result = await _api.PosSearchAsync(query);
        if (result.Ok && result.Value is not null)
        {
            foreach (var item in result.Value.Items)
            {
                list.Items.Add(item);
            }
        }
    }

    private async void OnCustomerClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CustomerPickerDialog(Content.XamlRoot);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.SelectedCustomer is { } customer)
        {
            _vm.CustomerId = customer.CustomerId;              // اختيار العميل لا يفقد السلة (§9.4)
            _vm.CustomerNameAr = customer.NameAr;
            StatusText.Text = $"العميل: {customer.NameAr}" +
                (customer.CreditEnabled ? $" · آجل بحد {customer.CreditLimit:0.00}" : " · آجل معطل (D-05)");
            await _vm.PreviewAsync();
        }
    }

    private async void OnWeightClick(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveLine is { IsWeight: true } line)
        {
            _ = await OpenWeightDialogAsync(line);
        }
        else
        {
            StatusText.Text = "حدد سطراً وزنياً أولاً (F6)";
        }
    }

    private async void OnDrawerClick(object sender, RoutedEventArgs e)
    {
        var reason = new TextBox { PlaceholderText = "سبب فتح الدرج (§9.9)" };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "فتح درج النقد بغير حركة بيع",
            PrimaryButtonText = "فتح (بسبب مسجل)",
            CloseButtonText = "إلغاء",
            Content = reason,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(reason.Text))
        {
            var result = await _api.OpenDrawerAsync(reason.Text.Trim());
            StatusText.Text = result.Ok
                ? $"أُرسل أمر الدرج — الحالة {result.Value!.ResultStatus} (لا استجابة جهاز مفترضة حتى G10/B-01)"
                : result.ErrorAr ?? "تعذر تسجيل فتح الدرج";
        }
    }

    private async void OnShiftClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ShiftDialog(Content.XamlRoot, _vm);
        _ = await dialog.ShowAsync();
        ShiftText.Text = _vm.ShiftInfoAr;
    }

    private async void OnPayClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Cart.Count == 0)
        {
            StatusText.Text = "السلة فارغة";
            return;
        }
        PayButton.IsEnabled = false;                       // منع الإرسال المكرر (§2.4 ق10)
        try
        {
            var dialog = new PaymentDialog(Content.XamlRoot, _vm);
            var (posted, sale, error) = await dialog.ShowAndPostAsync();
            if (posted && sale is not null)
            {
                // الإيصال: مهمة §29.4 أُنشئت تلقائياً مع البيع (أو HOLD موثق) — نعرض حالتها
                StatusText.Text = _vm.LastResultAr ?? $"فورة {sale.InvoiceNumber}";
                if (sale.Receipt is { JobCreated: true })
                {
                    StatusText.Text += $" · إيصال: {sale.Receipt.JobStatus}";
                }
                _vm.ResetCartAfterSuccessfulPost();        // إعادة التهيئة بعد نجاح الترحيل فقط (§9.7 ق12)
            }
            else if (error is not null)
            {
                StatusText.Text = error;
            }
        }
        finally
        {
            PayButton.IsEnabled = _vm.Cart.Count > 0;
            BarcodeField.Focus(FocusState.Programmatic);
        }
    }

    private async void OnHoldClick(object sender, RoutedEventArgs e)
        => await _vm.HoldAsync($"تعليق بواسطة {_vm.CashierDisplayAr}");

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new HeldCartsDialog(Content.XamlRoot);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Selected is { } selected)
        {
            StatusText.Text = dialog.RestoredCartJson is null
                ? "تعذرت الاستعادة — " + (selected.NoteAr ?? "لا ملاحظة")
                : $"استُعيدت السلة #{selected.CartId} — أكمل البيع من جديد (لا تكرار §9.5)";
        }
    }

    private async void OnReturnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ReturnDialog(Content.XamlRoot);
        _ = await dialog.ShowAsync();
    }

    private async void OnRemoveLineClick(object sender, RoutedEventArgs e)
    {
        await _vm.RemoveActiveCommand.ExecuteAsync(null);
        await DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshTotals);
    }

    private void OnCartSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.ActiveLine = CartList.SelectedItem as CartLineViewModel;
        SelectedDetailsText.Text = _vm.ActiveLine is null
            ? "الصنف المحدد: —"
            : $"الصنف المحدد: {_vm.ActiveLine.ProductNameAr} ({_vm.ActiveLine.SkuCode})";
    }

    private async void OnCartDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // نقرة مزدوجة على سطر وزني: إعادة فتح نافذة الوزن له (POS_VISUAL_SPEC §2)
        if (_vm.ActiveLine is { IsWeight: true } line)
        {
            _ = await OpenWeightDialogAsync(line);
        }
    }
}
