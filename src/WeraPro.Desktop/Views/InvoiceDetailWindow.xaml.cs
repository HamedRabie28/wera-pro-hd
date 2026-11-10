// Views/InvoiceDetailWindow.xaml.cs — WERA PRO HD Desktop (G7 · PAGE-04)
// تفاصيل فاتورة مرحّلة: يعرض من الخادم فقط — السطور بلقطات السعر والدفعات
// بحالتها وسجل مهام الطباعة؛ إعادة الطباعة نسخة موسومة (§29.9) وإلغاء
// المسودة بسبب (المرحّلة تُرَد بالمرتجع من PAGE-05).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.Views;

/// <summary>سطر عرض لفاتورة البيع — نصوص جاهزة (بدون محولات).</summary>
public sealed class SaleLineDisplay
{
    public required PosPreviewLine Source { get; init; }
    public string LineNo => Source.LineNo.ToString();
    public string SkuCode => Source.SkuCode;
    public string ProductNameAr => Source.ProductNameAr;
    public string QtyText => Source.Qty.ToString("0.###") + " " + Source.UomCode +
        (Source.WeightKg is { } w ? " (" + w.ToString("0.###") + " كجم)" : "");
    public string PriceText => Source.UnitPrice.ToString("N2");
    public string TaxText => Source.TaxAmount.ToString("N2");
    public string TotalText => Source.LineTotal.ToString("N2");
}

/// <summary>مهمة طباعة — ترويسة وتفصيل جاهزان للعرض.</summary>
public sealed class PrintJobDisplay
{
    public required PrintJobDto Source { get; init; }
    public string HeaderText => $"#{Source.JobId} · {CopyTypeAr} · {StatusAr}";
    public string DetailText =>
        Source.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
        " · محاولات " + Source.AttemptCount +
        (Source.RelatedReprintOf is { } orig ? " · إعادة طباعة للمهمة #" + orig : "") +
        (string.IsNullOrWhiteSpace(Source.FailureReasonAr) ? "" : " · " + Source.FailureReasonAr);
    private string CopyTypeAr => Source.CopyType switch
    {
        "CUSTOMER" => "نسخة العميل", "REPRINT" => "إعادة طباعة موسومة", _ => Source.CopyType,
    };
    private string StatusAr => Source.Status switch
    {
        "QUEUED" => "في الانتظار", "PRINTED" => "طُبعت", "FAILED" => "فاشلة",
        "CANCELLED" => "ملغاة", "RETRYING" => "إعادة محاولة", _ => Source.Status,
    };
}

public sealed partial class InvoiceDetailWindow : Window
{
    private readonly WeraApiClient _api;
    private readonly long _invoiceId;
    private PosSaleDetailResponse? _sale;
    private readonly SessionService _session;

    public InvoiceDetailWindow(long invoiceId)
    {
        InitializeComponent();
        Title = "WERA PRO HD — تفاصيل الفاتورة " + invoiceId;
        App.ActiveWindow = this;
        _invoiceId = invoiceId;
        _api = App.Host.Services.GetRequiredService<WeraApiClient>();
        _session = App.Host.Services.GetRequiredService<SessionService>();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var sale = await _api.GetSaleAsync(_invoiceId);
        if (!sale.Ok || sale.Value is null)
        {
            ErrorText.Text = sale.ErrorAr ?? "الفاتورة غير موجودة";
            ErrorText.Visibility = Visibility.Visible;
            MetaText.Text = "تعذر التحميل";
            return;
        }
        var s = sale.Value;
        _sale = s;

        var statusAr = s.Status switch
        {
            "POSTED" => "مرحّلة", "PARTIAL_RETURN" => "مرتجع جزئي", "FULL_RETURN" => "مرتجع كامل",
            "VOID" => "ملغاة", "DRAFT" => "مسودة", _ => s.Status,
        };
        TitleText.Text = $"فاتورة {s.InvoiceNumber} — {statusAr}";
        MetaText.Text =
            "رقم " + s.InvoiceId + " · " + (s.PostedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "غير مرحّلة") +
            " · الكاشير " + s.CashierDisplayAr +
            (s.ShiftId is { } sh ? " · وردية #" + sh : "") +
            (s.CustomerNameAr is { } c ? " · الزبون " + c : " · نقدي");

        TotalsText.Text =
            $"الإجمالي قبل الخصم {s.Totals.GrossTotal:N2} · خصم السطور {s.Totals.LineDiscountTotal:N2} · خصم الفاتورة {s.Totals.InvoiceDiscount:N2} ج.م\n" +
            $"الضريبة {s.Totals.TaxTotal:N2} · الصافي {s.Totals.NetTotal:N2} · المستحق {s.Totals.DueAmount:N2} · المدفوع {s.Totals.PaidTotal:N2} ج.م\n" +
            $"المرتجع من هذه الفاتورة: {s.ReturnedTotal:N2} ج.م";

        ReturnHintText.Text = s.CanReturn
            ? "قابلة للرد — أنشئ المرتجع من صفحة «المرتجعات» بمعرف الفاتورة " + s.InvoiceId + " (بوسيلة الدفع الأصلية D-04)"
            : "غير قابلة للرد (مرتجعة كاملاً أو ملغاة) — D-04";

        PaymentsText.Text = s.Payments.Count == 0
            ? "لا دفعات مسجلة"
            : string.Join("\n", s.Payments.Select(p =>
                MethodAr(p.Method) + ": " + p.Amount.ToString("N2") + " ج.م — " + PaymentStatusAr(p.Status) +
                (string.IsNullOrWhiteSpace(p.ProviderName) ? "" : " · المزود " + p.ProviderName) +
                (string.IsNullOrWhiteSpace(p.ProviderReference) ? "" : " · مرجع " + p.ProviderReference)));

        LinesList.Items.Clear();
        foreach (var l in s.Lines)
        {
            LinesList.Items.Add(new SaleLineDisplay { Source = l });
        }

        // أزرار الصلاحية: إعادة الطباعة printing.reprint · إلغاء المسودة sales.invoice.void
        var canReprint = _session.User?.Permissions.Contains("printing.reprint") == true;
        var canVoid = _session.User?.Permissions.Contains("sales.invoice.void") == true;
        ReprintButton.Visibility = canReprint ? Visibility.Visible : Visibility.Collapsed;
        VoidButton.Visibility = canVoid && s.Status == "DRAFT" ? Visibility.Visible : Visibility.Collapsed;
        if (!canReprint)
        {
            StatusText.Text = "جلستك لا تملك printing.reprint — إعادة الطباعة مخفية (RBAC خادمي)";
        }

        await LoadPrintJobsAsync();
    }

    private async Task LoadPrintJobsAsync()
    {
        var jobs = await _api.ListPrintJobsAsync(_invoiceId);
        PrintJobsList.Items.Clear();
        if (!jobs.Ok || jobs.Value is null)
        {
            PrintJobsList.Items.Add(new PrintJobDisplay
            {
                Source = new PrintJobDto(0, _invoiceId, "SALES_INVOICE", "—", "—", jobs.ErrorAr ?? "تعذر جلب السجل", 0, null, null, DateTimeOffset.UtcNow),
            });
            return;
        }
        foreach (var j in jobs.Value)
        {
            PrintJobsList.Items.Add(new PrintJobDisplay { Source = j });
        }
        if (PrintJobsList.Items.Count == 0)
        {
            PrintJobsList.Items.Add(new PrintJobDisplay
            {
                Source = new PrintJobDto(0, _invoiceId, "SALES_INVOICE", "—", "—", "لا مهام طباعة لهذه الفاتورة", 0, null, null, DateTimeOffset.UtcNow),
            });
        }
    }

    private static string MethodAr(string method) => method switch
    {
        "CASH" => "نقد", "CARD" => "بطاقة", "CREDIT" => "آجل", _ => method,
    };

    private static string PaymentStatusAr(string status) => status switch
    {
        "CAPTURED" => "مقبوضة", "PENDING" => "معلقة", "SETTLED" => "مسواة", "FAILED" => "فاشلة", _ => status,
    };

    private async void Reprint_Click(object sender, RoutedEventArgs e)
    {
        var result = await _api.PrintReceiptAsync(_invoiceId, "REPRINT");
        if (!result.Ok)
        {
            ErrorText.Text = result.ErrorAr ?? "تعذر إنشاء مهمة إعادة الطباعة";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        var job = result.Value!;
        StatusText.Text = job.JobId > 0
            ? "أُنشئت مهمة إعادة طباعة #" + job.JobId + " (" + ViewModels.SalesLogViewModel.PrintStatusAr(job.Status) + ") — نسخة موسومة REPRINT (§29.9)"
            : "تعذر التوجيه: " + (string.IsNullOrWhiteSpace(job.FailureReasonAr) ? job.Status : job.FailureReasonAr);
        await LoadPrintJobsAsync();
    }

    private async void Void_Click(object sender, RoutedEventArgs e)
    {
        if (_sale is null || _sale.Status != "DRAFT") return;
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        var reasonBox = new TextBox { Header = "سبب الإلغاء (يُوثَّق في التدقيق)" };
        panel.Children.Add(reasonBox);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "إلغاء مسودة الفاتورة " + _sale.InvoiceNumber,
            Content = panel, PrimaryButtonText = "إلغاء الفاتورة", CloseButtonText = "تراجع",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(reasonBox.Text))
        {
            ErrorText.Text = "سبب الإلغاء إلزامي";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var result = await _api.VoidSaleAsync(_invoiceId, reasonBox.Text.Trim());
        if (!result.Ok)
        {
            ErrorText.Text = result.ErrorAr ?? "تعذر الإلغاء";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = "أُلغيت المسودة — المرحّلة لا تُلغى بل يُنشأ لها مرتجع (D-04)";
        await LoadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
