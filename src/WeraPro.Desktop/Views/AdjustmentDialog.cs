// Views/AdjustmentDialog.cs — WERA PRO HD Desktop (G6)
// تسوية هالك/تلف صلاحية/سريعة (PAGE-23): مسار اعتماد D-03 كاملاً —
// طلب الاعتماد، فبت المشرف، ثم الترحيل بالمفتاح (لا تسوية بلا اعتماد V003).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;

namespace WeraPro.Desktop.Views;

/// <summary>حوار تسوية موجه: طلب اعتماد → رقم الاعتماد المُوافَق → ترحيل.</summary>
public sealed class AdjustmentDialog : ContentDialog
{
    private readonly Services.WeraApiClient _api = App.Host.Services.GetRequiredService<Services.WeraApiClient>();

    private readonly TextBox _skuBox = new() { PlaceholderText = "كود الصيغة" };
    private readonly ComboBox _warehouseBox = new() { PlaceholderText = "المخزن", DisplayMemberPath = "NameAr", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _newQtyBox = new() { PlaceholderText = "الرصيد الصحيح بعد التسوية", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox _kindBox = new() { PlaceholderText = "نوع التسوية", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _reasonBox = new() { PlaceholderText = "السبب الموثق (≥ 3 محارف)" };
    private readonly NumberBox _approvalBox = new() { Header = "رقم الاعتماد المُوافَق عليه (أنشئ الطلب أولاً ثم دع المشرف يبت)", Minimum = 0, SpinButtonPlacementMode = Microsoft.UI.Xaml.Controls.NumberBoxSpinButtonPlacementMode.Compact };
    private readonly Button _requestApprovalButton = new() { Content = "1) أنشئ طلب اعتماد…" };

    public bool Posted { get; private set; }
    public string? PostedInfo { get; private set; }

    public AdjustmentDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "تسوية مخزنية (باعتماد مشرف — D-03)";
        PrimaryButtonText = "ترحيل التسوية";
        CloseButtonText = "إلغاء";

        _kindBox.Items.Add("هالك (DAMAGE)");
        _kindBox.Items.Add("تلف صلاحية (EXPIRY)");
        _kindBox.Items.Add("تسوية سريعة (QUICK)");
        _kindBox.SelectedIndex = 0;
        _requestApprovalButton.Click += OnRequestApprovalClick;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_skuBox);
        panel.Children.Add(_warehouseBox);
        panel.Children.Add(_kindBox);
        panel.Children.Add(_newQtyBox);
        panel.Children.Add(_reasonBox);
        panel.Children.Add(_requestApprovalButton);
        panel.Children.Add(_approvalBox);
        Content = panel;
    }

    private async void OnRequestApprovalClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_reasonBox.Text) || _reasonBox.Text.Trim().Length < 3)
        {
            return;
        }
        var created = await _api.CreateApprovalAsync(new CreateApprovalRequest(
            "inv.adjustment", 0, _reasonBox.Text.Trim()), default);
        if (created.Ok)
        {
            _approvalBox.Value = created.Value!.ApprovalId;
            _requestApprovalButton.Content = $"أُنشئ الطلب #{created.Value.ApprovalId} — بانتظار بت المشرف ثم الترحيل";
        }
    }

    public new async Task<ContentDialogResult> ShowAsync()
    {
        // حمّل المخازن قبل الفتح (قائمة حقيقية من الخادم)
        var warehouses = await _api.GetWarehousesAsync(false, default);
        if (warehouses.Ok)
        {
            foreach (var w in warehouses.Value!) _warehouseBox.Items.Add(w);
        }
        var result = await base.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await PostAsync();
        }
        return result;
    }

    private async Task PostAsync()
    {
        Posted = false;
        PostedInfo = null;
        if (string.IsNullOrWhiteSpace(_skuBox.Text) || _approvalBox.Value < 1)
        {
            PostedInfo = "أكمل الصيغة ورقم الاعتماد المُوافَق عليه";
            return;
        }
        var lookup = await _api.LookupByCodeAsync(_skuBox.Text.Trim(), default);
        if (!lookup.Ok)
        {
            PostedInfo = "كود الصيغة غير معروف";
            return;
        }
        if (_warehouseBox.SelectedItem is not WarehouseDto warehouse)
        {
            PostedInfo = "اختر المخزن";
            return;
        }
        var kind = _kindBox.SelectedIndex switch { 1 => "EXPIRY", 2 => "QUICK", _ => "DAMAGE" };
        var response = await _api.PostAdjustmentAsync(new PostAdjustmentRequest(
            lookup.Value!.Sku.SkuId, warehouse.WarehouseId, (decimal)_newQtyBox.Value,
            kind, _reasonBox.Text.Trim(), (long)_approvalBox.Value), default);
        if (response.Ok)
        {
            Posted = true;
            var qty = response.Value!.BaseQty.ToString("0.######");
            PostedInfo = response.Value.MovementId is { } m
                ? $"رُحّلت التسوية #{response.Value.AdjustmentId} — حركة {response.Value.MovementType} بكمية {qty} (حركة {m})"
                : "فرق صفر — لا حركة (لا ضجيج في الدفتر)";
        }
        else
        {
            PostedInfo = response.ErrorAr;
        }
    }
}
