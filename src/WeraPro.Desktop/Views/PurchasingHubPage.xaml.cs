// Views/PurchasingHubPage.xaml.cs — WERA PRO HD Desktop (G6)
// المشتريات: السلة (مسودة قابلة للاستعادة) والاستلام (هو الذي يزيد المخزون)
// والفاتورة (التزام مالي) والسداد (أثر خزينة واحد) والمرتجع (بحد ما استُلم) —
// الأزرار تعكس صلاحيات الجلسة، والقرارات كلها خادمية (§18).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class PurchasingHubPage : Page
{
    private readonly PurchasingViewModel _vm;

    public PurchasingHubPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<PurchasingViewModel>();
        DataContext = _vm;
        NewBasketButton.Visibility = _vm.CanManageBasket ? Visibility.Visible : Visibility.Collapsed;
        NewReceiptButton.Visibility = _vm.CanReceive ? Visibility.Visible : Visibility.Collapsed;
        NewInvoiceButton.Visibility = _vm.CanInvoice ? Visibility.Visible : Visibility.Collapsed;
        NewPaymentButton.Visibility = _vm.CanPay ? Visibility.Visible : Visibility.Collapsed;
        NewReturnButton.Visibility = _vm.CanReturn ? Visibility.Visible : Visibility.Collapsed;
        PostReceiptButton.Visibility = _vm.CanReceive ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PurchasingViewModel.IsLoading))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
    }

    private async void OrdersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OrdersList.SelectedItem is PoRow row) await _vm.SelectOrderAsync(row, default);
    }

    private async void ReceiptsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReceiptsList.SelectedItem is ReceiptRow row) await _vm.SelectReceiptAsync(row, default);
    }

    private async void NewBasket_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var supplierBox = new ComboBox { PlaceholderText = "المورد", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var warehouseBox = new ComboBox { PlaceholderText = "مخزن الاستلام", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var skuBox = new TextBox { PlaceholderText = "كود الصيغة" };
        var qtyBox = new NumberBox { PlaceholderText = "الكمية بوحدة الشراء", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var costBox = new NumberBox { PlaceholderText = "تكلفة الوحدة", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        panel.Children.Add(supplierBox);
        panel.Children.Add(warehouseBox);
        panel.Children.Add(skuBox);
        panel.Children.Add(qtyBox);
        panel.Children.Add(costBox);
        foreach (var s in _vm.Suppliers) supplierBox.Items.Add(s.Source);
        foreach (var w in _vm.Warehouses) warehouseBox.Items.Add(w);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "سلة مشتريات (مسودة — لا مخزون ولا مال)",
            Content = panel, PrimaryButtonText = "إنشاء", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (supplierBox.SelectedItem is not WeraPro.Contracts.V1.SupplierDto supplier
            || warehouseBox.SelectedItem is not WeraPro.Contracts.V1.WarehouseDto warehouse
            || string.IsNullOrWhiteSpace(skuBox.Text))
        {
            _vm.ErrorAr = "أكمل المورد والمخزن والصيغة";
            return;
        }
        var api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
        var lookup = await api.LookupByCodeAsync(skuBox.Text.Trim(), default);
        if (!lookup.Ok)
        {
            _vm.ErrorAr = "كود الصيغة غير معروف";
            return;
        }
        await _vm.CreateBasketAsync(supplier.SupplierId, warehouse.WarehouseId,
            [new WeraPro.Contracts.V1.CreatePurchaseOrderLineRequest(lookup.Value!.Sku.SkuId, (decimal)qtyBox.Value, lookup.Value.Sku.BaseUomId, (decimal)costBox.Value)], default);
    }

    private async void SubmitOrder_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersList.SelectedItem is PoRow row) await _vm.SubmitOrderAsync(row, default);
    }

    private async void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        if (OrdersList.SelectedItem is PoRow row) await _vm.CancelOrderAsync(row, default);
    }

    private async void NewReceipt_Click(object sender, RoutedEventArgs e)
    {
        var api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
        var panel = new StackPanel { Spacing = 8 };
        var supplierBox = new ComboBox { PlaceholderText = "المورد", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var warehouseBox = new ComboBox { PlaceholderText = "المخزن", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var skuBox = new TextBox { PlaceholderText = "كود الصيغة" };
        var qtyBox = new NumberBox { PlaceholderText = "الكمية بوحدة الاستلام", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var costBox = new NumberBox { PlaceholderText = "تكلفة الوحدة المستلمة", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var lotBox = new TextBox { PlaceholderText = "رمز الدفعة (اختياري)" };
        panel.Children.Add(supplierBox);
        panel.Children.Add(warehouseBox);
        panel.Children.Add(skuBox);
        panel.Children.Add(qtyBox);
        panel.Children.Add(costBox);
        panel.Children.Add(lotBox);
        foreach (var s in _vm.Suppliers) supplierBox.Items.Add(s.Source);
        foreach (var w in _vm.Warehouses) warehouseBox.Items.Add(w);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "استلام جديد (المسودة لا تزيد المخزون — الترحيل يزيدها)",
            Content = panel, PrimaryButtonText = "إنشاء مسودة", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (supplierBox.SelectedItem is not WeraPro.Contracts.V1.SupplierDto supplier
            || warehouseBox.SelectedItem is not WeraPro.Contracts.V1.WarehouseDto warehouse
            || string.IsNullOrWhiteSpace(skuBox.Text))
        {
            _vm.ErrorAr = "أكمل المورد والمخزن والصيغة";
            return;
        }
        var lookup = await api.LookupByCodeAsync(skuBox.Text.Trim(), default);
        if (!lookup.Ok)
        {
            _vm.ErrorAr = "كود الصيغة غير معروف";
            return;
        }
        var receipt = await api.CreateReceiptAsync(new WeraPro.Contracts.V1.CreateReceiptRequest(
            supplier.SupplierId, warehouse.WarehouseId, null, "استلام من صفحة المشتريات",
            [new WeraPro.Contracts.V1.CreateReceiptLineRequest(
                lookup.Value!.Sku.SkuId, (decimal)qtyBox.Value, lookup.Value.Sku.BaseUomId, (decimal)costBox.Value,
                string.IsNullOrWhiteSpace(lotBox.Text) ? null : lotBox.Text.Trim(), null)]), default);
        if (!receipt.Ok)
        {
            _vm.ErrorAr = receipt.ErrorAr;
            return;
        }
        // ترحيل فوري: الاستلام هو الحدث الذي يزيد المخزون (لا الفاتورة §12.3 ق1)
        var posted = await api.PostReceiptAsync(receipt.Value!.ReceiptId, "rc-desktop-" + receipt.Value.ReceiptId.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
        _vm.ErrorAr = posted.Ok ? null : posted.ErrorAr;
        await _vm.LoadDocumentsAsync(default);
    }

    private async void PostReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (ReceiptsList.SelectedItem is ReceiptRow row) await _vm.PostReceiptAsync(row, default);
    }

    private async void NewInvoice_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var supplierBox = new ComboBox { PlaceholderText = "المورد", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var receiptBox = new ComboBox { PlaceholderText = "الاستلام المرجعي (اختياري)", DisplayMemberPath = "ReceiptNumber", HorizontalAlignment = HorizontalAlignment.Stretch };
        var numberBox = new TextBox { PlaceholderText = "رقم فاتورة المورد (كما عليها)" };
        var subtotalBox = new NumberBox { PlaceholderText = "الإجمالي قبل الضريبة", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var taxBox = new NumberBox { PlaceholderText = "الضريبة", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        panel.Children.Add(supplierBox);
        panel.Children.Add(receiptBox);
        panel.Children.Add(numberBox);
        panel.Children.Add(subtotalBox);
        panel.Children.Add(taxBox);
        foreach (var s in _vm.Suppliers) supplierBox.Items.Add(s.Source);
        foreach (var r in _vm.Receipts.Where(r => r.Source.Status == "POSTED")) receiptBox.Items.Add(r.Source);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "فاتورة مورد — التزام مالي لا حركة مخزون",
            Content = panel, PrimaryButtonText = "إنشاء وترحيل", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (supplierBox.SelectedItem is not WeraPro.Contracts.V1.SupplierDto supplier || string.IsNullOrWhiteSpace(numberBox.Text))
        {
            _vm.ErrorAr = "أكمل المورد ورقم الفاتورة";
            return;
        }
        var receiptId = (receiptBox.SelectedItem as WeraPro.Contracts.V1.ReceiptDto)?.ReceiptId;
        await _vm.CreateInvoiceAsync(supplier.SupplierId, receiptId, numberBox.Text.Trim(),
            (decimal)subtotalBox.Value, (decimal)taxBox.Value, default);
        // ترحيل فوري بعد الإنشاء
        if (_vm.Invoices.FirstOrDefault(i => i.Number == numberBox.Text.Trim()) is { } created)
        {
            await _vm.PostInvoiceAsync(created, default);
        }
    }

    private async void NewPayment_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        var supplierBox = new ComboBox { PlaceholderText = "المورد", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var treasuryBox = new ComboBox { PlaceholderText = "حساب الخزينة", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var amountBox = new NumberBox { PlaceholderText = "المبلغ", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var methodBox = new ComboBox { PlaceholderText = "الوسيلة", HorizontalAlignment = HorizontalAlignment.Stretch };
        methodBox.Items.Add("نقدي"); methodBox.Items.Add("تحويل بنكي"); methodBox.Items.Add("شيك");
        methodBox.SelectedIndex = 0;
        var invoiceNumberBox = new TextBox { PlaceholderText = "رقم فاتورة للتخصيص (اختياري)" };
        panel.Children.Add(supplierBox);
        panel.Children.Add(treasuryBox);
        panel.Children.Add(amountBox);
        panel.Children.Add(methodBox);
        panel.Children.Add(invoiceNumberBox);
        foreach (var s in _vm.Suppliers) supplierBox.Items.Add(s.Source);
        foreach (var t in _vm.TreasuryAccounts) treasuryBox.Items.Add(t);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "سداد مورد — أثر خزينة واحد (لا يُسجل مصروفاً)",
            Content = panel, PrimaryButtonText = "سداد وترحيل", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (supplierBox.SelectedItem is not WeraPro.Contracts.V1.SupplierDto supplier
            || treasuryBox.SelectedItem is not WeraPro.Contracts.V1.TreasuryAccountDto treasury)
        {
            _vm.ErrorAr = "أكمل المورد وحساب الخزينة";
            return;
        }
        var method = methodBox.SelectedIndex switch { 1 => "BANK_TRANSFER", 2 => "CHEQUE", _ => "CASH" };
        var allocations = new List<WeraPro.Contracts.V1.CreatePaymentAllocationRequest>();
        if (!string.IsNullOrWhiteSpace(invoiceNumberBox.Text)
            && _vm.Invoices.FirstOrDefault(i => i.Number == invoiceNumberBox.Text.Trim()) is { } invoice)
        {
            allocations.Add(new WeraPro.Contracts.V1.CreatePaymentAllocationRequest(invoice.InvoiceId, (decimal)amountBox.Value));
        }
        await _vm.CreatePaymentAsync(supplier.SupplierId, treasury.TreasuryAccountId, (decimal)amountBox.Value, method, allocations, default);
    }

    private async void NewReturn_Click(object sender, RoutedEventArgs e)
    {
        var api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
        var panel = new StackPanel { Spacing = 8 };
        var supplierBox = new ComboBox { PlaceholderText = "المورد", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
        var receiptBox = new ComboBox { PlaceholderText = "الاستلام الأصلي (إلزامي)", DisplayMemberPath = "ReceiptNumber", HorizontalAlignment = HorizontalAlignment.Stretch };
        var skuBox = new TextBox { PlaceholderText = "كود الصيغة المرجَّعة" };
        var qtyBox = new NumberBox { PlaceholderText = "الكمية بوحدة الاستلام", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var costBox = new NumberBox { PlaceholderText = "تكلفة الوحدة", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
        var refundBox = new ComboBox { PlaceholderText = "طريقة الرد", HorizontalAlignment = HorizontalAlignment.Stretch };
        refundBox.Items.Add("إشعار دائن (تخفيض الذمم)");
        refundBox.Items.Add("رد نقدي (خزينة)");
        refundBox.SelectedIndex = 0;
        panel.Children.Add(supplierBox);
        panel.Children.Add(receiptBox);
        panel.Children.Add(skuBox);
        panel.Children.Add(qtyBox);
        panel.Children.Add(costBox);
        panel.Children.Add(refundBox);
        foreach (var s in _vm.Suppliers) supplierBox.Items.Add(s.Source);
        foreach (var r in _vm.Receipts.Where(r => r.Source.Status == "POSTED")) receiptBox.Items.Add(r.Source);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "مرتجع مورد — بحد ما استُلم من الأصل (§12.3 ق4)",
            Content = panel, PrimaryButtonText = "إنشاء وترحيل", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (supplierBox.SelectedItem is not WeraPro.Contracts.V1.SupplierDto supplier
            || receiptBox.SelectedItem is not WeraPro.Contracts.V1.ReceiptDto receipt
            || string.IsNullOrWhiteSpace(skuBox.Text))
        {
            _vm.ErrorAr = "أكمل المورد والاستلام الأصلي والصيغة";
            return;
        }
        var lookup = await api.LookupByCodeAsync(skuBox.Text.Trim(), default);
        if (!lookup.Ok)
        {
            _vm.ErrorAr = "كود الصيغة غير معروف";
            return;
        }
        var refund = refundBox.SelectedIndex == 1 ? "CASH" : "CREDIT_NOTE";
        var created = await api.CreateReturnAsync(new WeraPro.Contracts.V1.CreateSupplierReturnRequest(
            supplier.SupplierId, receipt.ReceiptId, null, receipt.WarehouseId, refund,
            [new WeraPro.Contracts.V1.CreateSupplierReturnLineRequest(lookup.Value!.Sku.SkuId, (decimal)qtyBox.Value, lookup.Value.Sku.BaseUomId, (decimal)costBox.Value)]), default);
        if (!created.Ok)
        {
            _vm.ErrorAr = created.ErrorAr;
            return;
        }
        var posted = await api.PostReturnAsync(created.Value!.ReturnId, "ret-desktop-" + created.Value.ReturnId.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
        _vm.ErrorAr = posted.Ok ? null : posted.ErrorAr;
        await _vm.LoadDocumentsAsync(default);
    }
}
