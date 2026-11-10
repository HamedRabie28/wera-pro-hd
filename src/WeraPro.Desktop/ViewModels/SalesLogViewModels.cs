// ViewModels/SalesLogViewModels.cs — WERA PRO HD Desktop (G7 · سجلات البيع)
// سجل فواتير البيع (PAGE-03) + السلال المعلقة (PAGE-06) + المدفوعات والمطابقة
// (PAGE-08) + المرتجعات والاستردادات (PAGE-05) — كل البيانات من الخادم عبر
// WeraApiClient؛ الواجهة تعرض الحقيقة فقط (لا مفاتيح وهمية ولا مطابقة مزود
// قبل اعتماده B-02/D-11).
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

// ═══ سجل فواتير البيع (PAGE-03) ═══

/// <summary>سطر فاتورة بيع مرحّلة — الحالة بلونها الدلالي الصادق.
/// إظهار الأزرار من صلاحيات الجلسة التي تمررها الـViewModel عند البناء.</summary>
public sealed class InvoiceRow
{
    public required PosSaleSummaryDto Source { get; init; }
    /// <summary>الجلسة تملك إعادة الطباعة (printing.reprint) — يحدد إظهار زر الصف.</summary>
    public bool CanReprint { get; init; }
    /// <summary>الجلسة تملك إلغاء المسودات (sales.invoice.void) — يحدد إظهار زر الصف.</summary>
    public bool CanVoid { get; init; }
    public long InvoiceId => Source.InvoiceId;
    public string InvoiceNumber => Source.InvoiceNumber;
    public string WhenAr => Source.PostedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string StatusAr => Source.Status switch
    {
        "POSTED" => "مرحّلة", "PARTIAL_RETURN" => "مرتجع جزئي", "FULL_RETURN" => "مرتجع كامل",
        "VOID" => "ملغاة", "DRAFT" => "مسودة", _ => Source.Status,
    };
    public bool IsVoidable => Source.Status == "DRAFT";
    public bool CanReturn => Source.Status is "POSTED" or "PARTIAL_RETURN";
    /// <summary>إظهار زر إعادة الطباعة (بدون محولات — Visibility مباشرة من x:Bind).</summary>
    public Microsoft.UI.Xaml.Visibility ReprintVisibility => CanReprint
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    /// <summary>إظهار زر الإلغاء: صلاحية + مسودة فقط (المرحّلة تُرَد بالمرتجع).</summary>
    public Microsoft.UI.Xaml.Visibility VoidVisibility => CanVoid && IsVoidable
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string NetAr => Source.NetTotal.ToString("N2") + " ج.م";
    public string TaxAr => Source.TaxTotal.ToString("N2");
    public string DueAr => Source.DueAmount.ToString("N2");
    public string CustomerAr => Source.CustomerNameAr ?? "نقدي";
    public string LinesAr => Source.LineCount.ToString();
    /// <summary>لون الحالة — أخضر للمرحّلة، ذهبي للمرتجع جزئياً، أحمر للملغاة، رمادي لما عداها.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush StatusBrush => Source.Status switch
    {
        "POSTED" => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0x84, 0x5D)),
        "PARTIAL_RETURN" => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xB9, 0x95, 0x5C)),
        "FULL_RETURN" => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D)),
        "VOID" => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x48, 0x50)),
        _ => new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D)),
    };
}

// ═══ السلال المعلقة (PAGE-06) ═══

/// <summary>سلة معلقة — لقطة السلة تُحلَّل للعرض (أصناف/كميات) بلا إنشاء أي حركة.</summary>
public sealed class HeldCartRow
{
    public required HeldCartDto Source { get; init; }
    public long CartId => Source.CartId;
    public string HeldAtAr => Source.HeldAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string HeldBy => Source.HeldByDisplayAr;
    public string NoteAr => string.IsNullOrWhiteSpace(Source.NoteAr) ? "—" : Source.NoteAr;
    public bool Restored => Source.Restored;
    public string RestoredAr => Source.Restored ? "مستعادة" : "معلّقة";
    /// <summary>لون حالة السلة — أخضر للمعلّقة القابلة للاستعادة، رمادي للمستعادة.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush RestoredBrush => Source.Restored
        ? new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D))
        : new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0x84, 0x5D));

    /// <summary>تحليل لقطة السلة (skuCode/name/qty) — المستخدمة في حوار العرض.</summary>
    public static (string CustomerName, List<string> Lines) ParseSnapshot(string? cartJson)
    {
        var lines = new List<string>();
        string customer = "—";
        if (string.IsNullOrWhiteSpace(cartJson))
        {
            return (customer, ["لقطة فارغة"]);
        }
        try
        {
            using var doc = JsonDocument.Parse(cartJson);
            if (doc.RootElement.TryGetProperty("customerName", out var cn) && cn.GetString() is { Length: > 0 } name)
            {
                customer = name;
            }
            if (doc.RootElement.TryGetProperty("lines", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in arr.EnumerateArray())
                {
                    var n = l.TryGetProperty("name", out var np) ? np.GetString() : null;
                    var code = l.TryGetProperty("skuCode", out var cp) ? cp.GetString() : null;
                    var qty = l.TryGetProperty("qty", out var qp) ? qp.GetDecimal() : 0m;
                    var weight = l.TryGetProperty("weightKg", out var wp) && wp.ValueKind != JsonValueKind.Null
                        ? wp.GetDecimal() : (decimal?)null;
                    lines.Add((n ?? code ?? "صنف") + " × " +
                        (weight is { } w ? w.ToString("0.###") + " كجم" : qty.ToString("0.###")));
                }
            }
            if (lines.Count == 0)
            {
                lines.Add("لا سطور في اللقطة");
            }
        }
        catch (JsonException)
        {
            lines.Add("تعذر تحليل لقطة السلة (JSON غير سليم)");
        }
        return (customer, lines);
    }
}

// ═══ المدفوعات والمطابقة (PAGE-08) ═══

/// <summary>خيار وردية للفلترة — الوردية كل وحدة مطابقة مستقلة (§14.1).</summary>
public sealed class ShiftOptionRow
{
    public required ShiftListItemDto Source { get; init; }
    public long ShiftId => Source.ShiftId;
    public string StatusAr => Source.Status switch { "OPEN" => "مفتوحة", "CLOSED" => "مغلقة", _ => Source.Status };
    public string LabelAr => $"وردية #{Source.ShiftNumber} — {Source.StationNameAr} — {StatusAr} — " +
        Source.OpenedAtUtc.ToLocalTime().ToString("MM-dd HH:mm") +
        (Source.ClosedAtUtc is { } c ? " → " + c.ToLocalTime().ToString("MM-dd HH:mm") : "");
}

/// <summary>سطر مطابقة: تسمية وقيمة ولون (الفارق أحمر إن ≠ 0).</summary>
public sealed class ReconLineRow
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsVariance { get; init; }
    public Microsoft.UI.Xaml.Media.SolidColorBrush Brush => IsVariance
        ? new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xC4, 0x48, 0x50))
        : new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x23, 0x25, 0x36));
}

/// <summary>حركة نقد درج الوردية — مستند وسبب لكل حركة (§14.2 ق4).</summary>
public sealed class DrawerTxnRow
{
    public required TreasuryTxnDto Source { get; init; }
    public string DirectionAr => Source.Direction > 0 ? "داخل" : "خارج";
    public string AmountAr => Source.Amount.ToString("N2") + " ج.م";
    public string DocAr => Source.SourceDocType switch
    {
        "SALES_INVOICE" => "فاتورة بيع", "SALES_REFUND" => "مرتجع بيع", "EXPENSE" => "مصروف",
        "TREASURY_VOUCHER" => "سند خزينة", "CAPITAL_EVENT" => "حدث رأس مال", _ => Source.SourceDocType,
    };
    public string ReasonAr => string.IsNullOrWhiteSpace(Source.Reason) ? "—" : Source.Reason;
    public string WhenAr => Source.OccurredAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
}

// ═══ ViewModel سجل المبيعات (PAGE-03/06/08) ═══

public sealed partial class SalesLogViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public SalesLogViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public ObservableCollection<InvoiceRow> Invoices { get; } = [];
    public ObservableCollection<HeldCartRow> HeldCarts { get; } = [];
    public ObservableCollection<ShiftOptionRow> Shifts { get; } = [];
    public ObservableCollection<ReconLineRow> ReconLines { get; } = [];
    public ObservableCollection<DrawerTxnRow> DrawerTxns { get; } = [];

    [ObservableProperty] private string _statusAr = "جاهز";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorAr;

    /// <summary>حد القائمة — الخادم يقيد 1..50 (ListRecentAsync يطبق Clamp).</summary>
    public int Limit { get; set; } = 25;

    // صلاحيات أزرار الصفحة (RBAC خادمي — الواجهة تعرض ولا تأمن)
    public bool CanReprint => _session.User?.Permissions.Contains("printing.reprint") == true;
    public bool CanVoid => _session.User?.Permissions.Contains("sales.invoice.void") == true;
    public bool CanUseHeldCarts => _session.User?.Permissions.Contains("sales.held_cart.use") == true;
    public bool CanViewShifts => _session.User?.Permissions.Contains("sales.shift.close") == true;

    [RelayCommand]
    public async Task LoadInvoicesAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.ListRecentSalesAsync(Limit, null, ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل سجل الفواتير — القائمة تعرض مبيعات محطة الجلسة فقط";
            StatusAr = "فشل التحميل";
            return;
        }
        ErrorAr = null;
        Invoices.Clear();
        foreach (var s in result.Value!.Items)
        {
            Invoices.Add(new InvoiceRow { Source = s, CanReprint = CanReprint, CanVoid = CanVoid });
        }
        StatusAr = Invoices.Count == 0
            ? "لا فواتير حديثة لمحطة الجلسة — سجل الفواتير محدد بالمحطة (§9.6)"
            : $"{Invoices.Count} فاتورة (آخر {Limit}) — القائمة لمحطة الجلسة الحالية";
    }

    [RelayCommand]
    public async Task LoadHeldCartsAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.ListHeldCartsAsync(ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل السلال المعلقة";
            StatusAr = "فشل التحميل";
            return;
        }
        ErrorAr = null;
        HeldCarts.Clear();
        foreach (var c in result.Value ?? []) HeldCarts.Add(new HeldCartRow { Source = c });
        var pending = HeldCarts.Count(c => !c.Restored);
        StatusAr = $"{pending} سلة معلقة — الاستعادة من نافذة POS تعيد السلة لحالة الكاشير (§9.5 لا تنشئ حركة)";
    }

    [RelayCommand]
    public async Task LoadShiftsAsync(CancellationToken ct)
    {
        var result = await _api.GetShiftsAsync(stationId: null, status: null, page: 1, pageSize: 30, ct: ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر تحميل الورديات";
            return;
        }
        ErrorAr = null;
        Shifts.Clear();
        foreach (var s in result.Value!.Items) Shifts.Add(new ShiftOptionRow { Source = s });
        StatusAr = $"{Shifts.Count} وردية — اختر وردية لعرض مطابقة مدفوعاتها";
    }

    /// <summary>مطابقة وردية: المتوقع من الدفتر مقابل المُحصَّل بكل وسيلة + حركات الدرج (PAGE-08).</summary>
    public async Task LoadReconAsync(long shiftId, CancellationToken ct)
    {
        IsBusy = true;
        var recon = await _api.GetShiftReconciliationAsync(shiftId, ct);
        var moves = await _api.GetShiftCashMovementsAsync(shiftId, ct);
        IsBusy = false;
        if (!recon.Ok)
        {
            ErrorAr = recon.ErrorAr ?? "تعذر جلب مطابقة الوردية";
            return;
        }
        ErrorAr = null;
        var r = recon.Value!;
        ReconLines.Clear();
        ReconLines.Add(new ReconLineRow { Label = "الوردية", Value = $"#{r.Shift.ShiftNumber} — {r.Shift.StationNameAr}" });
        ReconLines.Add(new ReconLineRow { Label = "عدد الفواتير", Value = r.InvoiceCount.ToString("N0") });
        ReconLines.Add(new ReconLineRow { Label = "نقدي محصَّل", Value = r.CashSales.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "بطاقة محصَّلة", Value = r.CardCollected.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "آجل مُصدر", Value = r.CreditIssued.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "مرتجعات نقدية", Value = "−" + r.CashRefunds.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "مصروفات الدرج", Value = "−" + r.DrawerExpenses.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "إيداعات سندات", Value = r.VoucherDeposits.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "سحوبات سندات", Value = "−" + r.VoucherWithdrawals.ToString("N2") + " ج.م" });
        ReconLines.Add(new ReconLineRow { Label = "تحويلات داخل/خارج", Value = "+" + r.TransfersIn.ToString("N2") + " / −" + r.TransfersOut.ToString("N2") });
        ReconLines.Add(new ReconLineRow { Label = "المتوقع في الدرج", Value = r.ExpectedInDrawer.ToString("N2") + " ج.م" });
        if (r.CountedCash is { } counted)
        {
            var variance = r.Variance ?? (counted - r.ExpectedInDrawer);
            ReconLines.Add(new ReconLineRow { Label = "المعدود فعلاً", Value = counted.ToString("N2") + " ج.م" });
            ReconLines.Add(new ReconLineRow
            {
                Label = "الفارق",
                Value = (variance >= 0 ? "+" : "") + variance.ToString("N2") + " ج.م" +
                    (string.IsNullOrWhiteSpace(r.VarianceReason) ? "" : " — " + r.VarianceReason),
                IsVariance = variance != 0,
            });
        }
        else
        {
            ReconLines.Add(new ReconLineRow { Label = "المعدود فعلاً", Value = "لم تُقفل الوردية بعد" });
        }
        DrawerTxns.Clear();
        if (moves.Ok && moves.Value is not null)
        {
            foreach (var m in moves.Value.Movements) DrawerTxns.Add(new DrawerTxnRow { Source = m });
        }
        StatusAr = moves.Ok
            ? $"مطابقة الوردية #{r.Shift.ShiftNumber} — {DrawerTxns.Count} حركة درج من الدفتر. مطابقة مزود البطاقة معلقة حتى اعتماد مزود (B-02/D-11)."
            : "عرض المطابقة دون حركات الدرج: " + (moves.ErrorAr ?? "");
    }

    /// <summary>إعادة طباعة موسومة (REPRINT) — تُوثَّق كمهمة طباعة مستقلة (§29.9).</summary>
    public async Task<bool> ReprintAsync(long invoiceId, CancellationToken ct)
    {
        var result = await _api.PrintReceiptAsync(invoiceId, "REPRINT", ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر إنشاء مهمة إعادة الطباعة";
            return false;
        }
        ErrorAr = null;
        var job = result.Value!;
        StatusAr = job.JobId > 0
            ? $"أُنشئت مهمة إعادة طباعة #{job.JobId} ({PrintStatusAr(job.Status)}) — نسخة موسومة REPRINT تختلف عن الأصل"
            : "تعذر توجيه الإعادة: " + (string.IsNullOrWhiteSpace(job.FailureReasonAr) ? job.Status : job.FailureReasonAr);
        return job.JobId > 0;
    }

    /// <summary>حالة مهمة الطباعة بالعربية — للعرض الصادق في نص الحالة.</summary>
    public static string PrintStatusAr(string? status) => status switch
    {
        "QUEUED" => "في الانتظار", "PRINTED" => "طُبعت", "FAILED" => "فاشلة",
        "CANCELLED" => "ملغاة", "RETRYING" => "إعادة محاولة", _ => status ?? "—",
    };

    /// <summary>إلغاء مسودة فاتورة بسبب موثق — المرحّلة تُرَد بالمرتجع لا بالإلغاء.</summary>
    public async Task<bool> VoidAsync(InvoiceRow row, string reason, CancellationToken ct)
    {
        var result = await _api.VoidSaleAsync(row.InvoiceId, reason, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر إلغاء الفاتورة";
            return false;
        }
        ErrorAr = null;
        StatusAr = $"أُلغيت الفاتورة {row.InvoiceNumber} — المرحّلة لا تُلغى بل تُرَد بالمرتجع (D-04)";
        await LoadInvoicesAsync(ct);
        return true;
    }
}

// ═══ المرتجعات والاستردادات (PAGE-05) ═══

/// <summary>سطر فاتورة قابل للرد — الكمية المرتجعة قابلة للتحرير (0 = لا رد من السطر).</summary>
public sealed partial class RefundLineRow : ObservableObject
{
    public required PosPreviewLine Source { get; init; }
    public long LineId => Source.LineId;
    public int LineNo => Source.LineNo;
    public string SkuCode => Source.SkuCode;
    public string NameAr => Source.ProductNameAr;
    public string QtyAr => Source.Qty.ToString("0.###") + " " + Source.UomCode +
        (Source.WeightKg is { } w ? " (" + w.ToString("0.###") + " كجم)" : "");
    public string PriceAr => Source.UnitPrice.ToString("N2");
    public string TotalAr => Source.LineTotal.ToString("N2");
    public decimal MaxReturnQty => Math.Max(0, Source.Qty - Source.RefundedQty);
    public string RemainingAr => MaxReturnQty == 0 ? "ردّ كامل" : MaxReturnQty.ToString("0.###");
    public bool Returnable => MaxReturnQty > 0;
    /// <summary>لون المتبقي — أخضر لقابل للرد، رمادي لسطر رُدّ كاملاً.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush ReturnableBrush => Returnable
        ? new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0x84, 0x5D))
        : new(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x59, 0x6A, 0x7D));
    /// <summary>الحد الأعلى لصندوق الكمية (double — NumberBox.Value لا يقبل decimal في x:Bind).</summary>
    public double MaxReturnQtyDouble => (double)MaxReturnQty;

    [ObservableProperty] private double _returnQty;
}

/// <summary>طلب اعتماد مرتجع معلق — البت للمشرف بصلاحية security.approval.decide.</summary>
public sealed class ApprovalRow
{
    public required ApprovalDto Source { get; init; }
    public long ApprovalId => Source.ApprovalId;
    public string EntityAr => Source.EntityType switch
    {
        "sales.refund" => "مرتجع بيع — فاتورة #" + Source.EntityId, _ => Source.EntityType + " #" + Source.EntityId,
    };
    public string RequestedBy => Source.RequestedByName;
    public string ReasonAr => string.IsNullOrWhiteSpace(Source.Reason) ? "—" : Source.Reason;
    public string WhenAr => Source.RequestedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string DecisionAr => Source.Decision switch
    {
        "PENDING" => "بانتظار البت", "APPROVED" => "موافق", "REJECTED" => "مرفوض", _ => Source.Decision,
    };
}

public sealed partial class ReturnsViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public ReturnsViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public ObservableCollection<RefundLineRow> Lines { get; } = [];
    public ObservableCollection<ApprovalRow> Approvals { get; } = [];

    [ObservableProperty] private string _statusAr = "ابحث عن فاتورة بمعرفها الرقمي لبدء مرتجع";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorAr;

    private PosSaleDetailResponse? _detail;

    /// <summary>الفاتورة المحمّلة — ملخص الترويسة (فارغ قبل التحميل).</summary>
    [ObservableProperty] private string _invoiceHeaderAr = "لم تُحمَّل فاتورة بعد";

    /// <summary>رقم الاعتماد الجاهز للترحيل (0 = لا اعتماد بعد).</summary>
    [ObservableProperty] private long _readyApprovalId;

    // الصلاحيات — البت بالاعتماد غير كافٍ للترحيل: الترحيل يتطلب sales.return.approve
    public bool CanDecideApprovals => _session.User?.Permissions.Contains("security.approval.decide") == true;
    public bool CanPostRefund => _session.User?.Permissions.Contains("sales.return.approve") == true;

    public bool HasInvoice => _detail is not null;

    /// <summary>تحميل فاتورة بالمعرف الرقمي — القبول فقط للحالات POSTED/PARTIAL_RETURN.</summary>
    [RelayCommand]
    public async Task LoadInvoiceAsync(long invoiceId, CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetSaleAsync(invoiceId, ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "الفاتورة غير موجودة";
            InvoiceHeaderAr = "تعذر التحميل";
            return;
        }
        var d = result.Value!;
        _detail = d;
        ErrorAr = null;
        ReadyApprovalId = 0;
        Lines.Clear();
        foreach (var l in d.Lines) Lines.Add(new RefundLineRow { Source = l, ReturnQty = 0 });
        InvoiceHeaderAr = $"{d.InvoiceNumber} — {d.Status switch
        {
            "POSTED" => "مرحّلة", "PARTIAL_RETURN" => "مرتجع جزئي", "FULL_RETURN" => "مرتجع كامل",
            "VOID" => "ملغاة", _ => d.Status,
        }} · {d.Totals.NetTotal:N2} ج.م · مرتجع سابقاً {d.ReturnedTotal:N2} ج.م · الكاشير {d.CashierDisplayAr}" +
            (d.CustomerNameAr is { } c ? " · الزبون " + c : "");
        StatusAr = d.CanReturn
            ? $"حمّلت الفاتورة — حدد الكميات المرتجعة لكل سطر (الحد = المباع − المردود سابقاً)"
            : "هذه الفاتورة غير قابلة للرد (مرتجعة كاملاً أو ملغاة) — D-04";
    }

    [RelayCommand]
    public async Task LoadApprovalsAsync(CancellationToken ct)
    {
        var result = await _api.GetApprovalsAsync(null, ct);
        if (!result.Ok)
        {
            return; // قسم الاعتمادات إضافي — لا يُفشل الصفحة
        }
        Approvals.Clear();
        foreach (var a in result.Value ?? [])
        {
            if (a.EntityType == "sales.refund" && a.Decision == "PENDING")
            {
                Approvals.Add(new ApprovalRow { Source = a });
            }
        }
    }

    /// <summary>طلب اعتماد مرتجع — ينشئ طلباً بانتظار بت المشرف (D-04/V005).</summary>
    [RelayCommand]
    public async Task RequestApprovalAsync(string reason, CancellationToken ct)
    {
        if (_detail is null)
        {
            ErrorAr = "حمّل الفاتورة أولاً";
            return;
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            ErrorAr = "سبب المرتجع إلزامي (3 محارف على الأقل) — يُوثَّق في الاعتماد والتدقيق";
            return;
        }
        IsBusy = true;
        var result = await _api.CreateApprovalAsync(
            new CreateApprovalRequest("sales.refund", _detail.InvoiceId, reason.Trim()), ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر إنشاء طلب الاعتماد";
            return;
        }
        ErrorAr = null;
        var approval = result.Value!;
        StatusAr = CanDecideApprovals
            ? $"أُنشئ طلب الاعتماد #{approval.ApprovalId} — بِته الآن من قائمة الطلبات المعلقة ثم رحّل"
            : $"أُنشئ طلب الاعتماد #{approval.ApprovalId} — بانتظار بت المشرف ثم رحّل";
        await LoadApprovalsAsync(ct);
    }

    /// <summary>بت طلب اعتماد (موافقة/رفض) — للمشرف بصلاحية security.approval.decide.</summary>
    public async Task<bool> DecideAsync(ApprovalRow row, bool approve, string reason, CancellationToken ct)
    {
        if (!CanDecideApprovals)
        {
            ErrorAr = "البت في الاعتمادات يتطلب صلاحية security.approval.decide";
            return false;
        }
        var result = await _api.DecideApprovalAsync(row.ApprovalId,
            new DecideApprovalRequest(approve ? "APPROVED" : "REJECTED",
                string.IsNullOrWhiteSpace(reason) ? (approve ? "موافقة" : "رفض") : reason.Trim()), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر البت في الطلب";
            return false;
        }
        ErrorAr = null;
        if (approve && result.Value!.Decision == "APPROVED")
        {
            ReadyApprovalId = result.Value.ApprovalId;
            StatusAr = $"وُافق على الاعتماد #{result.Value.ApprovalId} — جاهز للترحيل من نموذج المرتجع";
        }
        else
        {
            StatusAr = $"رُفض الاعتماد #{row.ApprovalId}" + (ReadyApprovalId == row.ApprovalId ? " — ألغِ جاهزية الترحيل" : "");
            if (ReadyApprovalId == row.ApprovalId) ReadyApprovalId = 0;
        }
        await LoadApprovalsAsync(ct);
        return true;
    }

    /// <summary>ترحيل المرتجع — وسيلة الرد كوسيلة الدفع الأصلية (D-04) والاعتماد موافق إلزامياً.</summary>
    public async Task<bool> PostRefundAsync(string method, bool restock, string reason, CancellationToken ct)
    {
        if (_detail is null || !_detail.CanReturn)
        {
            ErrorAr = "حمّل فاتورة مرحّلة قابلة للرد أولاً";
            return false;
        }
        if (!CanPostRefund)
        {
            ErrorAr = "ترحيل المرتجع يتطلب صلاحية sales.return.approve — OWNER/MANAGER/SUPERVISOR (D-04)";
            return false;
        }
        if (ReadyApprovalId <= 0)
        {
            ErrorAr = "لا ترحيل بلا اعتماد موافق — أنشئ الطلب وبتّه أولاً (V005 refund_approval_required)";
            return false;
        }
        var selected = Lines.Where(l => l.ReturnQty > 0).ToList();
        if (selected.Count == 0)
        {
            ErrorAr = "حدد كمية مرتجعة موجبة لسطر واحد على الأقل";
            return false;
        }
        foreach (var l in selected)
        {
            if (l.LineId <= 0)
            {
                ErrorAr = "سطر بلا معرف حقيقي — أعد تحميل الفاتورة (تحديث العقد يمرر LineId)";
                return false;
            }
            if ((decimal)l.ReturnQty > l.MaxReturnQty + 0.0000001m)
            {
                ErrorAr = $"الكمية المرتجعة من «{l.NameAr}» تتجاوز المتبقي ({l.MaxReturnQty:0.###})";
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            ErrorAr = "سبب المرتجع إلزامي (3 محارف على الأقل)";
            return false;
        }
        IsBusy = true;
        var result = await _api.PostRefundAsync(new PostRefundRequest(
            _detail.InvoiceId,
            [.. selected.Select(l => new RefundLineRequest(l.LineId, (decimal)l.ReturnQty))],
            method, restock, reason.Trim(), ReadyApprovalId,
            $"refund-{Guid.NewGuid():N}"), ct);
        IsBusy = false;
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr ?? "تعذر ترحيل المرتجع";
            return false;
        }
        var r = result.Value!;
        ErrorAr = null;
        StatusAr = $"رُحّل المرتجع {r.RefundNumber} بمبلغ {r.Total:N2} ج.م — حالة الفاتورة الآن: " +
            (r.InvoiceStatusAfter == "FULL_RETURN" ? "مرتجعة كاملاً" : "مرتجع جزئي") +
            (r.IdempotentReplay ? " (إعادة مفتاح — أُعيدت النتيجة الأصلية)" : "") +
            (restock ? " · أُعيد للمخزون" : " · لم يُعد للمخزون (هالك)");
        ReadyApprovalId = 0;
        // أعد تحميل الفاتورة لعرض المتبقي المحدث
        await LoadInvoiceAsync(_detail.InvoiceId, ct);
        await LoadApprovalsAsync(ct);
        return true;
    }
}
