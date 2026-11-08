// ViewModels/FinanceViewModels.cs — WERA PRO HD Desktop (G8)
// المالية (§14/§13): الخزائن والأدراج (PAGE-35) والسندات (PAGE-37) والمصروفات
// (PAGE-36) ورأس المال والأرصدة الافتتاحية (PAGE-38) + الورديات وإقفالها
// (PAGE-33/34) — كل الأمر عبر الخادم بذرات صلاحيات (لا منطق مالي في الواجهة).
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>حساب خزينة بأرصدة الدفتر المنفصلة (§14.2 — درج ≠ خزينة ≠ بنك).</summary>
public sealed class TreasuryAccountRow
{
    public required TreasuryAccountBalanceDto Source { get; init; }
    public long Id => Source.TreasuryAccountId;
    public string Code => Source.Code;
    public string NameAr => Source.NameAr;
    public string KindAr => Source.AccountKind switch
    {
        "CASH_DRAWER" => "درج نقد", "SAFE" => "خزينة", "BANK" => "بنك",
        "PAYMENT_PROVIDER" => "مزود دفع", _ => Source.AccountKind,
    };
    public string BalanceAr => Source.CurrentBalance.ToString("N2") + " ج.م";
    public string ScopeAr => Source.StationId is { } sid ? $"محطة #{sid}"
        : Source.BranchId is { } bid ? $"فرع #{bid}" : "مركزي";
}

/// <summary>حركة خزينة — لكل حركة مستند ومصدر وسبب (§14.2 ق4).</summary>
public sealed class TreasuryTxnRow
{
    public required TreasuryTxnDto Source { get; init; }
    public long TxnId => Source.TxnId;
    public string Account => Source.AccountCode;
    public string DirectionAr => Source.Direction == 1 ? "قبض ↑" : "صرف ↓";
    public string AmountAr => Source.Amount.ToString("N2");
    public string DocAr => Source.SourceDocType switch
    {
        "sales.payment" => "تحصيل بيع", "sales.refund" => "رد مرتجع",
        "finance.voucher" => "سند", "finance.expense" => "مصروف",
        "finance.expense.reversal" => "عكس مصروف", "finance.capital_event" => "حدث رأس مال",
        "purch.payment" => "سداد مورد", "purch.return" => "رد مورد", _ => Source.SourceDocType,
    };
    public string Reason => Source.Reason ?? "—";
    public string TimeAr => Source.OccurredAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string ByAr => Source.CreatedByNameAr;
}

/// <summary>سند قبض/صرف/تحويل (PAGE-37) — المرحّل نهائي والتصحيح بسند معاكس.</summary>
public sealed class VoucherRow
{
    public required VoucherDto Source { get; init; }
    public long Id => Source.VoucherId;
    public string Number => Source.VoucherNumber;
    public string TypeAr => Source.VoucherType switch
    {
        "RECEIPT" => "قبض", "DISBURSEMENT" => "صرف", "TRANSFER" => "تحويل", _ => Source.VoucherType,
    };
    public string AmountAr => Source.Amount.ToString("N2") + " ج.م";
    public string Account => Source.TreasuryCode + (string.IsNullOrEmpty(Source.CounterTreasuryCode) ? "" : " ← " + Source.CounterTreasuryCode);
    public string CounterpartyAr => Source.CounterpartyNameAr ?? (Source.CounterpartyType ?? "—");
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "POSTED" => "مرحّل", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string AllocationsAr => Source.Allocations.Count == 0 ? "—"
        : $"{Source.Allocations.Count} فاتورة ({Source.Allocations.Sum(a => a.Amount):N2})";
    public string Reason => Source.Reason;
}

/// <summary>مصروف (PAGE-36) — اعتماد ثم سداد وأثر مالي واحد وإلغاء المرحّل بعكس موثق.</summary>
public sealed class ExpenseRow
{
    public required ExpenseDto Source { get; init; }
    public long Id => Source.ExpenseId;
    public string Number => Source.ExpenseNumber;
    public string Category => Source.CategoryNameAr;
    public string AmountAr => Source.Amount.ToString("N2") + (Source.TaxAmount > 0 ? $" (+{Source.TaxAmount:N2} ض)" : "");
    public string OutflowAr => Source.CashOutflow.ToString("N2") + " ج.م";
    public string Beneficiary => Source.Beneficiary ?? "—";
    public string MethodAr => Source.PaymentMethod switch
    {
        "CASH" => "نقدي", "BANK_TRANSFER" => "تحويل بنكي", "CHEQUE" => "شيك", "CARD" => "بطاقة", _ => Source.PaymentMethod,
    };
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "APPROVED" => "معتمد", "POSTED" => "مرحّل", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string ShiftAr => Source.ShiftId is { } sid ? $"وردية #{sid}" : "عام";
}

/// <summary>حدث رأس مال (PAGE-38) — موثق بكل مرة والافتتاحي باعتماد فقط (D-14).</summary>
public sealed class CapitalRow
{
    public required CapitalEventDto Source { get; init; }
    public long Id => Source.CapitalId;
    public string Number => Source.EventNumber;
    public string TypeAr => Source.EventType switch
    {
        "OWNER_CONTRIBUTION" => "مساهمة مالك", "OWNER_WITHDRAWAL" => "سحب مالك",
        "LOAN_RECEIVED" => "تمويل مستلم", "LOAN_REPAYMENT" => "سداد تمويل",
        "OPENING_BALANCE" => "رصيد افتتاحي", _ => Source.EventType,
    };
    public string AmountAr => ((Source.EventType is "OWNER_CONTRIBUTION" or "LOAN_RECEIVED" or "OPENING_BALANCE") ? "+" : "−")
        + Source.Amount.ToString("N2") + " ج.م";
    public string Treasury => Source.TreasuryCode ?? "غير نقدي";
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "POSTED" => "مرحّل", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string Description => Source.Description;
}

/// <summary>وردية (PAGE-33) — الفارق يظل ظاهراً حتى تسويته (بوابة القبول).</summary>
public sealed class ShiftRow
{
    public required ShiftListItemDto Source { get; init; }
    public long Id => Source.ShiftId;
    public string Station => Source.StationCode;
    public string OpenedAr => $"{Source.OpenedByDisplayAr} · {Source.OpenedAtUtc.ToLocalTime():MM-dd HH:mm}";
    public string FloatAr => Source.OpeningFloat.ToString("N2");
    public string StatusAr => Source.Status == "OPEN" ? "مفتوحة" : "مغلقة";
    public string ClosedAr => Source.ClosedAtUtc is { } t ? $"{Source.ClosedByDisplayAr} · {t.ToLocalTime():MM-dd HH:mm}" : "—";
    public string ExpectedAr => Source.ExpectedCash?.ToString("N2") ?? "—";
    public string CountedAr => Source.CountedCash?.ToString("N2") ?? "—";
    public string VarianceAr => Source.Variance is { } v
        ? (v == 0 ? "مطابقة ✓" : (v > 0 ? "+" : "") + v.ToString("N2") + (Source.VarianceSettledByVoucherId is null ? " (بانتظار التسوية)" : " (سُوّيت)"))
        : "—";
}

/// <summary>
/// نموذج المالية المركزي (G8): كل العمليات أوامر API خادمية — الواجهة تعرض وتطلب فقط.
/// </summary>
public sealed partial class FinanceViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public FinanceViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public ObservableCollection<TreasuryAccountRow> Accounts { get; } = [];
    public ObservableCollection<TreasuryTxnRow> Transactions { get; } = [];
    public ObservableCollection<VoucherRow> Vouchers { get; } = [];
    public ObservableCollection<ExpenseRow> Expenses { get; } = [];
    public ObservableCollection<CapitalRow> CapitalEvents { get; } = [];
    public ObservableCollection<ShiftRow> Shifts { get; } = [];
    public ObservableCollection<ExpenseCategoryDto> Categories { get; } = [];

    [ObservableProperty] private string _statusAr = "جاهز — اختر تبويباً للعرض";
    [ObservableProperty] private bool _isBusy;

    public long? SelectedAccountId { get; set; }

    [RelayCommand]
    public async Task LoadAccountsAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetTreasuryAccountsAsync(includeInactive: true, ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            StatusAr = result.ErrorAr ?? "تعذر تحميل الخزائن";
            return;
        }
        Accounts.Clear();
        foreach (var a in result.Value!) Accounts.Add(new TreasuryAccountRow { Source = a });
        StatusAr = $"{Accounts.Count} حساب خزينة — الأرصدة من الدفتر (درج ≠ خزينة ≠ بنك §14.2)";
    }

    [RelayCommand]
    public async Task LoadTransactionsAsync(CancellationToken ct)
    {
        if (SelectedAccountId is not { } id)
        {
            StatusAr = "اختر حساب الخزينة أولاً لعرض حركاته";
            return;
        }
        IsBusy = true;
        var result = await _api.GetTreasuryTransactionsAsync(id, ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            StatusAr = result.ErrorAr ?? "تعذر تحميل الحركات";
            return;
        }
        Transactions.Clear();
        foreach (var t in result.Value!.Items) Transactions.Add(new TreasuryTxnRow { Source = t });
        StatusAr = $"{result.Value.TotalCount} حركة — لكل حركة مستند وسبب (§14.2 ق4)";
    }

    [RelayCommand]
    public async Task LoadVouchersAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetVouchersAsync(ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            StatusAr = result.ErrorAr ?? "تعذر تحميل السندات";
            return;
        }
        Vouchers.Clear();
        foreach (var v in result.Value!.Items) Vouchers.Add(new VoucherRow { Source = v });
        StatusAr = $"{result.Value.TotalCount} سند — المرحّل نهائي والتصحيح بسند معاكس (§14.2)";
    }

    [RelayCommand]
    public async Task LoadExpensesAsync(CancellationToken ct)
    {
        IsBusy = true;
        if (Categories.Count == 0)
        {
            var cats = await _api.GetExpenseCategoriesAsync(ct);
            if (cats.Ok)
            {
                foreach (var c in cats.Value!) Categories.Add(c);
            }
        }
        var result = await _api.GetExpensesAsync(ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            StatusAr = result.ErrorAr ?? "تعذر تحميل المصروفات";
            return;
        }
        Expenses.Clear();
        foreach (var e in result.Value!.Items) Expenses.Add(new ExpenseRow { Source = e });
        StatusAr = $"{result.Value.TotalCount} مصروف — اعتماد ثم سداد وأثر واحد (§14.4)";
    }

    [RelayCommand]
    public async Task LoadCapitalAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetCapitalEventsAsync(ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            StatusAr = result.ErrorAr ?? "تعذر تحميل أحداث رأس المال";
            return;
        }
        CapitalEvents.Clear();
        foreach (var c in result.Value!.Items) CapitalEvents.Add(new CapitalRow { Source = c });
        StatusAr = $"{result.Value.TotalCount} حدث رأس مال — الأرصدة بمستندات معتمدة فقط (D-14)";
    }

    [RelayCommand]
    public async Task LoadShiftsAsync(CancellationToken ct)
    {
        IsBusy = true;
        var result = await _api.GetShiftsAsync(ct: ct);
        IsBusy = false;
        if (!result.Ok)
        {
            StatusAr = result.ErrorAr ?? "تعذر تحميل الورديات";
            return;
        }
        Shifts.Clear();
        foreach (var s in result.Value!.Items) Shifts.Add(new ShiftRow { Source = s });
        StatusAr = $"{result.Value.TotalCount} وردية — الفارق يظل ظاهراً حتى التسوية (بوابة G8)";
    }

    /// <summary>ترحيل سند مسودة (أثر مالي واحد) — لا منطق مالي في الواجهة.</summary>
    [RelayCommand]
    public async Task PostVoucherAsync(VoucherRow? row, CancellationToken ct)
    {
        if (row is null) return;
        IsBusy = true;
        var result = await _api.PostVoucherAsync(row.Id, ct);
        IsBusy = false;
        StatusAr = result.Ok
            ? $"رُحّل السند {row.Number} — أثر خزينة واحد (§14.2)"
            : result.ErrorAr ?? "تعذر الترحيل";
        if (result.Ok) await LoadVouchersAsync(ct);
    }

    /// <summary>اعتماد مصروف مسودة (اعتماد ثنائي — المعتمد ≠ المنشئ).</summary>
    [RelayCommand]
    public async Task ApproveExpenseAsync(ExpenseRow? row, CancellationToken ct)
    {
        if (row is null) return;
        IsBusy = true;
        var result = await _api.ApproveExpenseAsync(row.Id, "اعتماد من واجهة المالية", ct);
        IsBusy = false;
        StatusAr = result.Ok ? $"اعتُمد المصروف {row.Number}" : result.ErrorAr ?? "تعذر الاعتماد";
        if (result.Ok) await LoadExpensesAsync(ct);
    }

    /// <summary>ترحيل مصروف معتمد (أثر مالي واحد + سند طباعة §29.10).</summary>
    [RelayCommand]
    public async Task PostExpenseAsync(ExpenseRow? row, CancellationToken ct)
    {
        if (row is null) return;
        IsBusy = true;
        var result = await _api.PostExpenseAsync(row.Id, ct);
        IsBusy = false;
        StatusAr = result.Ok
            ? $"رُحّل المصروف {row.Number} — {result.Value!.PrintNoticeAr}"
            : result.ErrorAr ?? "تعذر الترحيل";
        if (result.Ok) await LoadExpensesAsync(ct);
    }

    /// <summary>إلغاء مصروف — المرحّل بعكس موثق بسبب إلزامي (§14.4).</summary>
    [RelayCommand]
    public async Task CancelExpenseAsync(ExpenseRow? row, CancellationToken ct)
    {
        if (row is null) return;
        IsBusy = true;
        var result = await _api.CancelExpenseAsync(row.Id, "إلغاء من واجهة المالية — موثق", ct);
        IsBusy = false;
        StatusAr = result.Ok
            ? $"أُلغي المصروف {row.Number}" + (row.StatusAr == "مرحّل" ? " بعكس موثق (أثر صافٍ صفر)" : "")
            : result.ErrorAr ?? "تعذر الإلغاء";
        if (result.Ok) await LoadExpensesAsync(ct);
    }

    /// <summary>ترحيل حدث رأس مال مسودة.</summary>
    [RelayCommand]
    public async Task PostCapitalAsync(CapitalRow? row, CancellationToken ct)
    {
        if (row is null) return;
        IsBusy = true;
        var result = await _api.PostCapitalEventAsync(row.Id, ct);
        IsBusy = false;
        StatusAr = result.Ok ? $"رُحّل الحدث {row.Number} (§14.3)" : result.ErrorAr ?? "تعذر الترحيل";
        if (result.Ok) await LoadCapitalAsync(ct);
    }
}
