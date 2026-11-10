// Views/PosDialogs.cs — WERA PRO HD Desktop (G7)
// نوافذ POS: الوزن بالطرق الست (§9.3)، الدفع نقد/بطاقة/مختلط/آجل (§9.6)،
// المرتجع/الاستبدال (§9.8)، المعلقات (§9.5)، اختيار العميل، والوردية (§9.9) —
// كل نافذة ContentDialog بلا فقد للسلة، وكل الحسابات عبر الخادم (§9.0 ق10).
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

/// <summary>
/// نافذة الوزن (§9.3): ست طرق — قراءة الميزان (حالتها الصادقة)، إدخال يدوي،
/// مبلغ مستهدف (مثال D-07: 200 ج.م/كجم × 70 = 350 جم)، وزن سريع (أزرار على
/// خطوة الصنف فقط)، ملصق باركود (يحل في الالتقاط)، وعبوة معدة (تسلك PACK).
/// الحساب كله من خدمة المجال عبر /pos/weight/preview — لا حساب في النافذة.
/// </summary>
public sealed class WeightSaleDialog : ContentDialog
{
    private readonly WeraApiClient _api = App.Host.Services.GetRequiredService<WeraApiClient>();
    private readonly CartLineViewModel _line;

    private readonly TextBox _input = new() { PlaceholderText = "الوزن (كجم) أو المبلغ حسب الطريقة" };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _methodBox = new() { PlaceholderText = "الطريقة", HorizontalAlignment = HorizontalAlignment.Stretch };

    public decimal AcceptedWeightKg { get; private set; }
    public string AcceptedSource { get; private set; } = string.Empty;
    public decimal? AcceptedAmountTarget { get; private set; }
    public bool Accepted { get; private set; }

    public WeightSaleDialog(XamlRoot root, CartLineViewModel line)
    {
        XamlRoot = root;
        _line = line;
        Title = $"وزن: {line.ProductNameAr} ({line.UnitPrice:0.00} ج.م/كجم)";
        PrimaryButtonText = "إضافة للسلة";
        CloseButtonText = "إلغاء";

        _methodBox.Items.Add("يدوي (MANUAL_WEIGHT)");
        _methodBox.Items.Add("سريع 250 جم (QUICK_PICK)");
        _methodBox.Items.Add("سريع 500 جم (QUICK_PICK)");
        _methodBox.Items.Add("سريع 1 كجم (QUICK_PICK)");
        _methodBox.Items.Add("مبلغ مستهدف (AMOUNT_DERIVED)");
        _methodBox.Items.Add("قراءة ميزان (SCALE_READING — يتطلب جهازاً معتمداً في G10)");
        _methodBox.SelectedIndex = 0;
        _input.TextChanged += async (_, _) => await PreviewAsync();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_methodBox);
        panel.Children.Add(_input);
        panel.Children.Add(_result);
        Content = panel;
        Opened += Dialog_Opened;
    }

    private async Task PreviewAsync()
    {
        var method = MethodFromSelection();
        var request = new WeightPreviewRequest(
            _line.SkuId, method,
            WeightKg: method is "MANUAL_WEIGHT" or "QUICK_PICK" && decimal.TryParse(_input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : null,
            AmountTarget: method == "AMOUNT_DERIVED" && decimal.TryParse(_input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null,
            ScaleReadingKg: null, ScaleStable: null, ScaleConnected: null, ScaleReadAtUtc: null, TareKg: null);
        var preview = await _api.PosWeightPreviewAsync(request);
        if (preview.Ok && preview.Value is { } v)
        {
            _result.Text = v.Accepted
                ? $"الوزن المعتمد {v.WeightKg:0.###} كجم ({v.WeightSource}) — القيمة {v.Amount:0.00} ج.م" +
                  (v.ExactWeightGrams is { } ex ? $" · الدقيق {ex:0.###}g ↔ المعروض {v.DisplayWeightGrams:0.###}g (D-07)" : string.Empty)
                : $"مرفوض: {v.ErrorAr} ({v.ErrorCode})";
        }
        else
        {
            _result.Text = preview.ErrorAr ?? "تعذرت المعاينة";
        }
    }

    private string MethodFromSelection() => _methodBox.SelectedIndex switch
    {
        1 => "QUICK_PICK",
        2 => "QUICK_PICK",
        3 => "QUICK_PICK",
        4 => "AMOUNT_DERIVED",
        5 => "SCALE_READING",
        _ => "MANUAL_WEIGHT",
    };

    private decimal QuickPickGrams() => _methodBox.SelectedIndex switch
    {
        1 => 0.250m,
        2 => 0.500m,
        3 => 1.000m,
        _ => 0m,
    };

    private async void Dialog_Opened(ContentDialog sender, object args)
    {
        await PreviewAsync();
    }

    public new async Task<ContentDialogResult> ShowAsync()
    {
        var result = await base.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var preview = await _api.PosWeightPreviewAsync(new WeightPreviewRequest(
                _line.SkuId, MethodFromSelection(),
                WeightKg: QuickPickGrams() > 0 ? QuickPickGrams() / 1000m
                    : (decimal.TryParse(_input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : null),
                AmountTarget: MethodFromSelection() == "AMOUNT_DERIVED" && decimal.TryParse(_input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null,
                ScaleReadingKg: null, ScaleStable: null, ScaleConnected: null, ScaleReadAtUtc: null, TareKg: null));
            if (preview.Ok && preview.Value is { Accepted: true })
            {
                AcceptedWeightKg = preview.Value.WeightKg;
                AcceptedSource = preview.Value.WeightSource;
                AcceptedAmountTarget = MethodFromSelection() == "AMOUNT_DERIVED" && decimal.TryParse(_input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
                Accepted = true;
            }
            else
            {
                Accepted = false;
            }
        }
        return result;
    }
}

/// <summary>
/// نافذة الدفع (§9.6): المستحق من الخادم، نقد/بطاقة/مختلط/آجل — البطاقة تظهر
/// حالة المزود الصادقة (معطلة حتى B-02/D-11 أو محاكاة موسومة)، والفكة تظهر
/// منفصلة عن المستحق (§9.1F)، وزر التأكيد يعطّل أثناء الترحيل (منع التكرار §15.4).
/// </summary>
public sealed class PaymentDialog : ContentDialog
{
    private readonly PosViewModel _vm;
    private readonly WeraApiClient _api = App.Host.Services.GetRequiredService<WeraApiClient>();

    private readonly NumberBox _cashBox = new() { Header = "نقد (ج.م)", Minimum = 0 };
    private readonly NumberBox _cardBox = new() { Header = "بطاقة (ج.م)", Minimum = 0 };
    private readonly NumberBox _creditBox = new() { Header = "آجل (ج.م) — عميل بحد ائتمان (D-05)", Minimum = 0 };
    private readonly NumberBox _tenderedBox = new() { Header = "المسلَّم نقدياً (لحساب الفكة)", Minimum = 0 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    public IReadOnlyList<PosPaymentRequest> Payments { get; private set; } = [];
    public decimal? CashTendered { get; private set; }
    public bool Confirmed { get; private set; }

    public PaymentDialog(XamlRoot root, PosViewModel vm)
    {
        XamlRoot = root;
        _vm = vm;
        Title = $"الدفع — المستحق {(vm.Totals?.DueAmount ?? 0):0.00} ج.م";
        PrimaryButtonText = "تأكيد الدفع وترحيل الفاتورة";
        CloseButtonText = "رجوع";

        _cashBox.Value = (double)(vm.Totals?.DueAmount ?? 0);
        _cardBox.Value = 0;
        _creditBox.Value = 0;
        _tenderedBox.Value = (double)(vm.Totals?.DueAmount ?? 0);
        _cashBox.ValueChanged += (_, _) => UpdateSummary();
        _cardBox.ValueChanged += (_, _) => UpdateSummary();
        _creditBox.ValueChanged += (_, _) => UpdateSummary();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"المستحق {(vm.Totals?.DueAmount ?? 0):0.00} ج.م (شامل ضريبة {(vm.Totals?.TaxTotal ?? 0):0.00}) — {vm.CardInfoAr}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_cashBox);
        panel.Children.Add(_cardBox);
        panel.Children.Add(_creditBox);
        panel.Children.Add(_tenderedBox);
        panel.Children.Add(_summary);
        Content = panel;
        UpdateSummary();
        Opened += Dialog_Opened;
    }

    private void UpdateSummary()
    {
        var due = _vm.Totals?.DueAmount ?? 0;
        var sum = (decimal)_cashBox.Value + (decimal)_cardBox.Value + (decimal)_creditBox.Value;
        var change = (decimal)_tenderedBox.Value - (decimal)_cashBox.Value;
        _summary.Text =
            $"مجموع الدفعات {sum:0.00} من {due:0.00} (الباقي {due - sum:0.00}) · " +
            $"الفكة للعميل {(change > 0 ? change : 0):0.00} ج.م";
        IsPrimaryButtonEnabled = Math.Abs(sum - due) <= 0.005m && _vm.Cart.Count > 0;
    }

    private void Dialog_Opened(ContentDialog sender, object args)
    {
        UpdateSummary();
    }

    public async Task<(bool Posted, PostedSaleResponse? Sale, string? ErrorAr)> ShowAndPostAsync()
    {
        var result = await base.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return (false, null, null);
        }
        var payments = new List<PosPaymentRequest>();
        if (_cashBox.Value > 0)
        {
            payments.Add(new PosPaymentRequest("CASH", (decimal)_cashBox.Value, (decimal)_tenderedBox.Value));
        }
        if (_cardBox.Value > 0)
        {
            payments.Add(new PosPaymentRequest("CARD", (decimal)_cardBox.Value, null));
        }
        if (_creditBox.Value > 0)
        {
            payments.Add(new PosPaymentRequest("CREDIT", (decimal)_creditBox.Value, null));
        }
        Payments = payments;
        CashTendered = (decimal)_tenderedBox.Value;
        Confirmed = true;
        var (ok, sale, error) = await _vm.PostSaleAsync(payments, (decimal)_tenderedBox.Value);
        return (ok, sale, error);
    }
}

/// <summary>
/// نافذة المرتجع/الاستبدال (§9.8 · D-04): تبحث عن الفاتورة، تعرض سطورها مع
/// المتبقي القابل للرد، تتطلب سبباً + اعتماد مشرف (رقم الاعتماد بعد البت)،
/// وترد بوسيلة الدفع الأصلية (البطاقة للبطاقة) — الاستبدال = مرتجع + بيع جديد.
/// </summary>
public sealed class ReturnDialog : ContentDialog
{
    private readonly WeraApiClient _api = App.Host.Services.GetRequiredService<WeraApiClient>();

    private readonly TextBox _invoiceBox = new() { PlaceholderText = "رقم الفاتورة الأصلية (InvoiceId) أو رقم العرض (SI-…)" };
    private readonly TextBox _lineBox = new() { PlaceholderText = "معرف السطر (LineId) — من تفاصيل الفاتورة" };
    private readonly NumberBox _qtyBox = new() { Header = "الكمية المرتجعة", Minimum = 0.01 };
    private readonly ComboBox _methodBox = new() { PlaceholderText = "وسيلة الرد", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _restockBox = new() { Content = "إعادة للمخزون (ألغِها إن كان هالكاً)", IsChecked = true };
    private readonly TextBox _reasonBox = new() { PlaceholderText = "السبب (≥ 3 محارف)" };
    private readonly NumberBox _approvalBox = new() { Header = "رقم الاعتماد المُوافَق (اطلب ثم دع المشرف يبت — D-04)", Minimum = 0 };
    private readonly Button _requestButton = new() { Content = "1) أنشئ طلب اعتماد…" };
    private readonly Button _loadButton = new() { Content = "تحميل الفاتورة" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };

    public bool Posted { get; private set; }

    public ReturnDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "مرتجع / استبدال (باعتماد مشرف — D-04)";
        PrimaryButtonText = "2) ترحيل المرتجع";
        CloseButtonText = "إلغاء";

        _methodBox.Items.Add("نقد (CASH)");
        _methodBox.Items.Add("بطاقة (CARD)");
        _methodBox.Items.Add("عكس آجل (CREDIT_REVERSAL)");
        _methodBox.SelectedIndex = 0;
        _requestButton.Click += OnRequestApproval;
        _loadButton.Click += async (_, _) => await LoadInvoiceAsync();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_invoiceBox);
        panel.Children.Add(_loadButton);
        panel.Children.Add(_info);
        panel.Children.Add(_lineBox);
        panel.Children.Add(_qtyBox);
        panel.Children.Add(_methodBox);
        panel.Children.Add(_restockBox);
        panel.Children.Add(_reasonBox);
        panel.Children.Add(_requestButton);
        panel.Children.Add(_approvalBox);
        Content = panel;
    }

    private PosSaleDetailResponse? _sale;

    private async Task LoadInvoiceAsync()
    {
        var raw = _invoiceBox.Text.Trim();
        if (!long.TryParse(raw, out var invoiceId) && raw.StartsWith("SI-", StringComparison.Ordinal))
        {
            _info.Text = "أدخل معرف الفاتورة الرقمي (InvoiceId) من سجل الفواتير";
            return;
        }
        var sale = await _api.GetSaleAsync(invoiceId);
        if (!sale.Ok || sale.Value is null)
        {
            _info.Text = sale.ErrorAr ?? "الفاتورة غير موجودة";
            return;
        }
        _sale = sale.Value;
        // العقد يعرض الآن LineId وRefundedQty لكل سطر — الحد القابل للرد = qty − refunded.
        _info.Text = string.Join("\n", _sale.Lines.Select(l =>
            $"سطر {l.LineNo} (معرف {l.LineId}): {l.ProductNameAr} × {l.Qty:0.###} {l.UomCode}" +
            (l.RefundedQty > 0 ? $" — مردود سابقاً {l.RefundedQty:0.###} (المتبقي {Math.Max(0, l.Qty - l.RefundedQty):0.###})" : ""))) +
            $"\nالمرتجع سابقاً من الفاتورة: {_sale.ReturnedTotal:0.00} ج.م";
        if (_sale.Lines.Count > 0)
        {
            // المعرف الحقيقي للسطر (لا رقم العرض) — OriginalLineId في الخادم يقصد LineId
            _lineBox.Text = _sale.Lines[0].LineId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async void OnRequestApproval(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(_invoiceBox.Text.Trim(), out var invoiceId) || _sale is null)
        {
            _info.Text = "حمّل الفاتورة أولاً";
            return;
        }
        var created = await _api.CreateApprovalAsync(
            new CreateApprovalRequest("sales.refund", invoiceId, _reasonBox.Text.Trim()), CancellationToken.None);
        _approvalBox.Value = created.Ok && created.Value is not null ? created.Value.ApprovalId : 0;
        _info.Text = created.Ok
            ? "أُنشئ الطلب — دع المشرف يوافق عليه ثم رحّل"
            : created.ErrorAr ?? "تعذر إنشاء الطلب";
    }

    public new async Task<ContentDialogResult> ShowAsync()
    {
        var result = await base.ShowAsync();
        if (result == ContentDialogResult.Primary
            && _sale is not null
            && long.TryParse(_lineBox.Text.Trim(), out var lineId)
            && _approvalBox.Value > 0)
        {
            var method = _methodBox.SelectedIndex switch
            {
                1 => "CARD",
                2 => "CREDIT_REVERSAL",
                _ => "CASH",
            };
            var refund = await _api.PostRefundAsync(new PostRefundRequest(
                _sale.InvoiceId,
                [new RefundLineRequest(lineId, (decimal)_qtyBox.Value)],
                method,
                _restockBox.IsChecked == true,
                _reasonBox.Text.Trim(),
                (long)_approvalBox.Value,
                $"refund-{Guid.NewGuid():N}"), CancellationToken.None);
            Posted = refund.Ok;
            _info.Text = refund.Ok
                ? $"رُحّل المرتجع {refund.Value!.RefundNumber} بمبلغ {refund.Value.Total:0.00} — حالة الفاتورة {refund.Value.InvoiceStatusAfter}" +
                  (refund.Value.IdempotentReplay ? " (إعادة مفتاح — أعيد الأصل)" : string.Empty)
                : refund.ErrorAr ?? "تعذر ترحيل المرتجع";
        }
        return result;
    }
}

/// <summary>قائمة السلال المعلقة (§9.5): الرقم والوقت والكاشير والملاحظة — الاستعادة قراءة.</summary>
public sealed class HeldCartsDialog : ContentDialog
{
    private readonly WeraApiClient _api = App.Host.Services.GetRequiredService<WeraApiClient>();
    private readonly ListView _list = new();
    private readonly Button _restoreButton = new() { Content = "استعادة المحددة", IsEnabled = false };

    public HeldCartDto? Selected { get; private set; }
    public string? RestoredCartJson { get; private set; }

    public HeldCartsDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "الفواتير المعلقة — استعادة لا تنشئ بيعاً (§9.5)";
        PrimaryButtonText = "استعادة";
        CloseButtonText = "إلغاء";
        _list.SelectionChanged += (_, _) => _restoreButton.IsEnabled = _list.SelectedItem is HeldCartDto;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_list);
        panel.Children.Add(_restoreButton);
        Content = panel;
        Opened += Dialog_Opened;
    }

    private async void Dialog_Opened(ContentDialog sender, object args)
    {
        var carts = await _api.ListHeldCartsAsync();
        _list.Items.Clear();
        if (carts.Ok && carts.Value is not null)
        {
            foreach (var cart in carts.Value)
            {
                _list.Items.Add(cart);
            }
            _list.DisplayMemberPath = nameof(HeldCartDto.NoteAr);
        }
    }

    public new async Task<ContentDialogResult> ShowAsync()
    {
        var result = await base.ShowAsync();
        if (result == ContentDialogResult.Primary && _list.SelectedItem is HeldCartDto selected)
        {
            Selected = selected;
            var restored = await _api.RestoreCartAsync(selected.CartId);
            RestoredCartJson = restored.Ok && restored.Value is not null ? restored.Value.CartJson : null;
        }
        return result;
    }
}

/// <summary>اختيار عميل (§9.2): بحث بالاسم/الهاتف — اختيار العميل لا يفقد السلة (§9.4).</summary>
public sealed class CustomerPickerDialog : ContentDialog
{
    private readonly WeraApiClient _api = App.Host.Services.GetRequiredService<WeraApiClient>();
    private readonly TextBox _searchBox = new() { PlaceholderText = "اسم العميل / الهاتف / الكود" };
    private readonly ListView _list = new();

    public CustomerDto? SelectedCustomer { get; private set; }

    public CustomerPickerDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "اختيار عميل (إلزامي للبيع الآجل فقط — D-05)";
        PrimaryButtonText = "اختيار";
        CloseButtonText = "إلغاء";
        _searchBox.TextChanged += async (_, _) => await SearchAsync();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_searchBox);
        panel.Children.Add(_list);
        Content = panel;
        _list.DisplayMemberPath = nameof(CustomerDto.NameAr);
        _list.SelectionChanged += (_, _) => SelectedCustomer = _list.SelectedItem as CustomerDto;
    }

    private async Task SearchAsync()
    {
        var result = await _api.SearchCustomersAsync(_searchBox.Text.Trim(), 1, 20, CancellationToken.None);
        _list.Items.Clear();
        if (result.Ok && result.Value is not null)
        {
            foreach (var customer in result.Value.Items)
            {
                _list.Items.Add(customer);
            }
        }
    }
}

/// <summary>الوردية (§9.9): فتح برصيد افتتاحي وملخص المتوقع في الدرج — الإغلاق التام في G8.</summary>
public sealed class ShiftDialog : ContentDialog
{
    private readonly PosViewModel _vm;
    private readonly WeraApiClient _api = App.Host.Services.GetRequiredService<WeraApiClient>();

    private readonly NumberBox _floatBox = new() { Header = "الرصيد الافتتاحي (ج.م)", Minimum = 0 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    public ShiftDialog(XamlRoot root, PosViewModel vm)
    {
        XamlRoot = root;
        _vm = vm;
        Title = "الوردية — وردية واحدة مفتوحة لكل محطة (§14.1)";
        PrimaryButtonText = "فتح الوردية";
        CloseButtonText = "إغلاق";

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_floatBox);
        panel.Children.Add(_summary);
        Content = panel;
        Opened += Dialog_Opened;
    }

    private async void Dialog_Opened(ContentDialog sender, object args)
    {
        var current = await _vm.LoadCurrentShiftTextAsync();
        _summary.Text = current;
        IsPrimaryButtonEnabled = !_vm.HasOpenShift;
    }

    public new async Task<ContentDialogResult> ShowAsync()
    {
        var result = await base.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var opened = await _api.OpenShiftAsync((decimal)_floatBox.Value);
            if (opened.Ok && opened.Value is not null)
            {
                _summary.Text = $"فُتحت وردية #{opened.Value.ShiftNumber} بافتتاحي {opened.Value.OpeningFloat:0.00}";
            }
        }
        return result;
    }
}
