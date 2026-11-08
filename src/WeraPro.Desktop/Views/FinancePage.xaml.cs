// Views/FinancePage.xaml.cs — WERA PRO HD Desktop (G8)
// منطق صفحة المالية: تحميل خادمي فقط + حوارات الإنشاء/الاعتماد/الإقفال —
// كل قرار مالي من الخادم (الواجهة ليست مصدر الحقيقة §9.0 ق10).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class FinancePage : Page
{
    public FinanceViewModel Vm { get; }
    private readonly Services.WeraApiClient _api;

    public FinancePage()
    {
        InitializeComponent();
        Vm = App.Host.Services.GetRequiredService<FinanceViewModel>();
        _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
        _ = Vm.LoadAccountsAsync(default);
        _ = Vm.LoadShiftsAsync(default);
    }

    private void Pivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (sender as Pivot)?.SelectedIndex switch
        {
            0 => "accounts", 1 => "vouchers", 2 => "expenses", 3 => "capital", 4 => "shifts", _ => "",
        };
        _ = tag switch
        {
            "accounts" => Vm.LoadAccountsAsync(default),
            "vouchers" => Vm.LoadVouchersAsync(default),
            "expenses" => Vm.LoadExpensesAsync(default),
            "capital" => Vm.LoadCapitalAsync(default),
            "shifts" => Vm.LoadShiftsAsync(default),
            _ => Task.CompletedTask,
        };
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ListView)?.SelectedItem is TreasuryAccountRow row)
        {
            Vm.SelectedAccountId = row.Id;
            TxnHeader.Text = $"حركات {row.NameAr} — لكل حركة مستند وسبب (§14.2 ق4)";
            _ = Vm.LoadTransactionsAsync(default);
        }
    }

    private async void PostVoucher_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is long id)
        {
            await Vm.PostVoucherAsync(Vm.Vouchers.FirstOrDefault(v => v.Id == id), default);
        }
    }

    private async void ApproveExpense_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is long id)
        {
            await Vm.ApproveExpenseAsync(Vm.Expenses.FirstOrDefault(x => x.Id == id), default);
        }
    }

    private async void PostExpense_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is long id)
        {
            await Vm.PostExpenseAsync(Vm.Expenses.FirstOrDefault(x => x.Id == id), default);
        }
    }

    private async void CancelExpense_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is long id)
        {
            await Vm.CancelExpenseAsync(Vm.Expenses.FirstOrDefault(x => x.Id == id), default);
        }
    }

    private async void PostCapital_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is long id)
        {
            await Vm.PostCapitalAsync(Vm.CapitalEvents.FirstOrDefault(c => c.Id == id), default);
        }
    }

    private async void ReconcileShift_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not long shiftId) return;
        var rec = await _api.GetShiftReconciliationAsync(shiftId);
        if (!rec.Ok)
        {
            await ShowErrorAsync(rec.ErrorAr ?? "تعذر جلب المطابقة");
            return;
        }
        var dialog = new ShiftCloseDialog(Content.XamlRoot, rec.Value!);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var close = await _api.CloseShiftAsync(shiftId, new CloseShiftRequest(
            dialog.CountedCash, dialog.VarianceReason, dialog.Notes));
        if (close.Ok)
        {
            var r = close.Value!;
            await ShowInfoAsync($"أُقفلت الوردية — المتوقع {r.Reconciliation.ExpectedInDrawer:N2} · الفعلي {r.FinalCountedCash:N2} · الفارق {r.Variance:N2}"
                + (r.Variance == 0 ? "" : $"\nالسبب: {r.Reconciliation.Shift.VarianceReason}\nالفارق يظل ظاهراً حتى تسويته بسند موثق (بوابة G8)"));
            await Vm.LoadShiftsAsync(default);
        }
        else
        {
            await ShowErrorAsync(close.ErrorAr ?? "تعذر إقفال الوردية");
        }
    }

    private async void NewVoucher_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewVoucherDialog(Content.XamlRoot, Vm);
        await dialog.ShowAsync();
        await Vm.LoadVouchersAsync(default);
    }

    private async void NewExpense_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewExpenseDialog(Content.XamlRoot, Vm);
        await dialog.ShowAsync();
        await Vm.LoadExpensesAsync(default);
    }

    private async void NewCapital_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewCapitalEventDialog(Content.XamlRoot);
        await dialog.ShowAsync();
        await Vm.LoadCapitalAsync(default);
    }

    private async void NewAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewTreasuryAccountDialog(Content.XamlRoot);
        await dialog.ShowAsync();
        await Vm.LoadAccountsAsync(default);
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "تعذر إكمال العملية",
            Content = message,
            CloseButtonText = "حسناً",
        };
        await dialog.ShowAsync();
    }

    private async Task ShowInfoAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "تم",
            Content = message,
            CloseButtonText = "حسناً",
        };
        await dialog.ShowAsync();
    }
}

/// <summary>حوار سند جديد (قبض/صرف/تحويل §14.2) — التوزيع على فواتير العميل لسند القبض فقط (§13).</summary>
public sealed class NewVoucherDialog : ContentDialog
{
    private readonly Services.WeraApiClient _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
    private readonly FinanceViewModel _vm;

    private readonly ComboBox _typeBox = new() { PlaceholderText = "نوع السند", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _accountBox = new() { PlaceholderText = "الخزينة", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _counterAccountBox = new() { PlaceholderText = "خزينة الوجهة (للتحويل)", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _amountBox = new() { Header = "المبلغ", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox _counterpartyTypeBox = new() { PlaceholderText = "نوع الطرف (اختياري)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _counterpartyIdBox = new() { PlaceholderText = "معرف الطرف (رقم)" };
    private readonly TextBox _reasonBox = new() { PlaceholderText = "السبب الموثق (≥ 3 محارف)" };
    private readonly TextBox _allocationsBox = new() { PlaceholderText = "توزيع على فواتير: رقم الفاتورة:مبلغ، ... (سند قبض عميل فقط)" };

    public NewVoucherDialog(XamlRoot root, FinanceViewModel vm)
    {
        XamlRoot = root;
        _vm = vm;
        Title = "سند مالي جديد (§14.2)";
        PrimaryButtonText = "إنشاء مسودة";
        CloseButtonText = "إلغاء";

        _typeBox.Items.Add("قبض (RECEIPT)");
        _typeBox.Items.Add("صرف (DISBURSEMENT)");
        _typeBox.Items.Add("تحويل بين خزينتين (TRANSFER)");
        _typeBox.SelectedIndex = 0;
        foreach (var a in vm.Accounts)
        {
            _accountBox.Items.Add(a);
            _counterAccountBox.Items.Add(a);
        }
        _counterpartyTypeBox.Items.Add("عميل (CUSTOMER)");
        _counterpartyTypeBox.Items.Add("مورد (SUPPLIER)");
        _counterpartyTypeBox.Items.Add("موظف (EMPLOYEE)");
        _counterpartyTypeBox.Items.Add("مالك (OWNER)");
        _counterpartyTypeBox.Items.Add("طرف آخر (OTHER)");

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(_typeBox);
        panel.Children.Add(_accountBox);
        panel.Children.Add(_counterAccountBox);
        panel.Children.Add(_amountBox);
        panel.Children.Add(_counterpartyTypeBox);
        panel.Children.Add(_counterpartyIdBox);
        panel.Children.Add(_reasonBox);
        panel.Children.Add(_allocationsBox);
        Content = panel;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var type = (_typeBox.SelectedIndex switch
            {
                0 => "RECEIPT", 1 => "DISBURSEMENT", _ => "TRANSFER",
            });
            var counterpartyType = _counterpartyTypeBox.SelectedIndex switch
            {
                0 => "CUSTOMER", 1 => "SUPPLIER", 2 => "EMPLOYEE", 3 => "OWNER", 4 => "OTHER", _ => null as string,
            };
            var allocations = new List<VoucherAllocationRequest>();
            foreach (var part in (_allocationsBox.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bits = part.Split(':');
                if (bits.Length == 2 && long.TryParse(bits[0], out var invoiceId) && decimal.TryParse(bits[1], out var amount))
                {
                    allocations.Add(new VoucherAllocationRequest(invoiceId, amount));
                }
            }
            var request = new CreateVoucherRequest(
                type,
                (_accountBox.SelectedItem as ViewModels.TreasuryAccountRow)?.Id ?? 0,
                type == "TRANSFER" ? (_counterAccountBox.SelectedItem as ViewModels.TreasuryAccountRow)?.Id : null,
                (decimal)_amountBox.Value,
                counterpartyType,
                long.TryParse(_counterpartyIdBox.Text, out var cpId) && counterpartyType is ("CUSTOMER" or "SUPPLIER" or "EMPLOYEE") ? cpId : null,
                _reasonBox.Text,
                allocations.Count == 0 ? null : allocations);
            var result = await _api.CreateVoucherAsync(request);
            if (!result.Ok)
            {
                args.Cancel = true;
                Content = new TextBlock { Text = result.ErrorAr, TextWrapping = TextWrapping.Wrap };
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}

/// <summary>حوار مصروف جديد (§14.4) — الربط بالوردية لمصروف الدرج فقط (القاعدة الموثقة G8).</summary>
public sealed class NewExpenseDialog : ContentDialog
{
    private readonly Services.WeraApiClient _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
    private readonly ComboBox _categoryBox = new() { PlaceholderText = "الفئة", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _amountBox = new() { Header = "المبلغ", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _taxBox = new() { Header = "الضريبة (إن انطبقت)", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox _methodBox = new() { PlaceholderText = "وسيلة السداد", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _treasuryBox = new() { PlaceholderText = "من خزينة", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _beneficiaryBox = new() { PlaceholderText = "المستفيد" };
    private readonly TextBox _notesBox = new() { PlaceholderText = "ملاحظات / السبب" };
    private readonly ViewModels.FinanceViewModel _vm;

    public NewExpenseDialog(XamlRoot root, ViewModels.FinanceViewModel vm)
    {
        XamlRoot = root;
        _vm = vm;
        Title = "مصروف جديد (§14.4 — اعتماد ثم سداد)";
        PrimaryButtonText = "تسجيل";
        CloseButtonText = "إلغاء";

        foreach (var c in vm.Categories) _categoryBox.Items.Add(c);
        _methodBox.Items.Add("نقدي (CASH)");
        _methodBox.Items.Add("تحويل بنكي (BANK_TRANSFER)");
        _methodBox.Items.Add("شيك (CHEQUE)");
        _methodBox.Items.Add("بطاقة (CARD)");
        _methodBox.SelectedIndex = 0;
        foreach (var a in vm.Accounts) _treasuryBox.Items.Add(a);

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(_categoryBox);
        panel.Children.Add(_amountBox);
        panel.Children.Add(_taxBox);
        panel.Children.Add(_methodBox);
        panel.Children.Add(_treasuryBox);
        panel.Children.Add(_beneficiaryBox);
        panel.Children.Add(_notesBox);
        Content = panel;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var method = _methodBox.SelectedIndex switch { 0 => "CASH", 1 => "BANK_TRANSFER", 2 => "CHEQUE", _ => "CARD" };
            var request = new CreateExpenseRequest(
                (_categoryBox.SelectedItem as ExpenseCategoryDto)?.CategoryId ?? 0,
                (decimal)_amountBox.Value,
                (decimal)_taxBox.Value,
                method,
                (_treasuryBox.SelectedItem as ViewModels.TreasuryAccountRow)?.Id,
                string.IsNullOrWhiteSpace(_beneficiaryBox.Text) ? null : _beneficiaryBox.Text.Trim(),
                null,
                null,
                null,
                string.IsNullOrWhiteSpace(_notesBox.Text) ? null : _notesBox.Text.Trim());
            var result = await _api.CreateExpenseAsync(request);
            if (!result.Ok)
            {
                args.Cancel = true;
                Content = new TextBlock { Text = result.ErrorAr, TextWrapping = TextWrapping.Wrap };
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}

/// <summary>حوار حدث رأس مال (§14.3) — الافتتاحي يستلزم رقم اعتماد مسبق (D-14).</summary>
public sealed class NewCapitalEventDialog : ContentDialog
{
    private readonly Services.WeraApiClient _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
    private readonly ComboBox _typeBox = new() { PlaceholderText = "نوع الحدث", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _amountBox = new() { Header = "المبلغ", Minimum = 0.01, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox _treasuryBox = new() { PlaceholderText = "الخزينة (اختياري)", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _descriptionBox = new() { PlaceholderText = "الوصف الموثق (≥ 3 محارف)" };
    private readonly NumberBox _approvalBox = new() { Header = "رقم الاعتماد المُوافَق (إلزامي للرصيد الافتتاحي فقط)", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };

    public NewCapitalEventDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "حدث رأس مال (§14.3)";
        PrimaryButtonText = "تسجيل مسودة";
        CloseButtonText = "إلغاء";

        _typeBox.Items.Add("مساهمة مالك (OWNER_CONTRIBUTION)");
        _typeBox.Items.Add("سحب مالك (OWNER_WITHDRAWAL)");
        _typeBox.Items.Add("تمويل مستلم (LOAN_RECEIVED)");
        _typeBox.Items.Add("سداد تمويل (LOAN_REPAYMENT)");
        _typeBox.Items.Add("رصيد افتتاحي معتمد (OPENING_BALANCE)");
        _typeBox.SelectedIndex = 0;
        var financeVm = App.Host.Services.GetRequiredService<ViewModels.FinanceViewModel>();
        foreach (var a in financeVm.Accounts) _treasuryBox.Items.Add(a);

        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(_typeBox);
        panel.Children.Add(_amountBox);
        panel.Children.Add(_treasuryBox);
        panel.Children.Add(_descriptionBox);
        panel.Children.Add(_approvalBox);
        Content = panel;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var eventType = _typeBox.SelectedIndex switch
            {
                0 => "OWNER_CONTRIBUTION", 1 => "OWNER_WITHDRAWAL", 2 => "LOAN_RECEIVED",
                3 => "LOAN_REPAYMENT", _ => "OPENING_BALANCE",
            };
            var request = new CreateCapitalEventRequest(
                eventType,
                (decimal)_amountBox.Value,
                (_treasuryBox.SelectedItem as ViewModels.TreasuryAccountRow)?.Id,
                null,
                _descriptionBox.Text,
                _approvalBox.Value > 0 ? (long)_approvalBox.Value : null);
            var result = await _api.CreateCapitalEventAsync(request);
            if (!result.Ok)
            {
                args.Cancel = true;
                Content = new TextBlock { Text = result.ErrorAr, TextWrapping = TextWrapping.Wrap };
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}

/// <summary>حوار حساب خزينة جديد (§14.2) — الدرج يرتبط بمحطة (الحقيقة الفيزيائية G8).</summary>
public sealed class NewTreasuryAccountDialog : ContentDialog
{
    private readonly Services.WeraApiClient _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();
    private readonly TextBox _codeBox = new() { PlaceholderText = "الكود (≥ محرفين)" };
    private readonly TextBox _nameBox = new() { PlaceholderText = "الاسم (≥ 3 محارف)" };
    private readonly ComboBox _kindBox = new() { PlaceholderText = "النوع", HorizontalAlignment = HorizontalAlignment.Stretch };

    public NewTreasuryAccountDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "حساب خزينة جديد (§14.2 — إدارة: مالك/مدير)";
        PrimaryButtonText = "إنشاء";
        CloseButtonText = "إلغاء";

        _kindBox.Items.Add("خزينة (SAFE)");
        _kindBox.Items.Add("بنك (BANK)");
        _kindBox.Items.Add("درج محطة (CASH_DRAWER)");
        _kindBox.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 8, MinWidth = 400 };
        panel.Children.Add(_codeBox);
        panel.Children.Add(_nameBox);
        panel.Children.Add(_kindBox);
        Content = panel;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var kind = _kindBox.SelectedIndex switch { 1 => "BANK", 2 => "CASH_DRAWER", _ => "SAFE" };
            var result = await _api.CreateTreasuryAccountAsync(new CreateTreasuryAccountRequest(
                _codeBox.Text, _nameBox.Text, kind, null, null));
            if (!result.Ok)
            {
                args.Cancel = true;
                Content = new TextBlock { Text = result.ErrorAr, TextWrapping = TextWrapping.Wrap };
            }
        }
        finally
        {
            deferral.Complete();
        }
    }
}
