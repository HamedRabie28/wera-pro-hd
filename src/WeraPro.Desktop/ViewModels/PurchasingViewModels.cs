// ViewModels/PurchasingViewModels.cs — WERA PRO HD Desktop (G6)
// المشتريات (§12.3): سلة المشتريات وأوامر الشراء (PAGE-24) والاستلامات
// (PAGE-25) وفواتير الموردين والسداد (PAGE-26) والمرتجعات (PAGE-27) +
// الموردون (PAGE-31/32) والعملاء (PAGE-29/30) — الاستلام هو الذي يزيد
// المخزون (لا الفاتورة) والسداد أثر خزينة واحد لا يُسجل مصروفاً.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>مورد (PAGE-31) — التعطيل لا الحذف (له مستندات تاريخية §13).</summary>
public sealed class SupplierRow
{
    public required SupplierDto Source { get; init; }
    public long SupplierId => Source.SupplierId;
    public string Code => Source.Code;
    public string NameAr => Source.NameAr;
    public string PhoneAr => Source.Phone ?? "—";
    public string TermsAr => Source.PaymentTermsDays == 0 ? "نقدي" : Source.PaymentTermsDays + " يوم";
    public string StatusAr => Source.AccountStatus switch
    {
        "ACTIVE" => "نشط", "SUSPENDED" => "معلّق", "ARCHIVED" => "مؤرشف", _ => Source.AccountStatus,
    };
}

/// <summary>عميل (PAGE-29) — الآجل معطل افتراضياً بانتظار D-05 (تعليق V005 الصادق).</summary>
public sealed class CustomerRow
{
    public required CustomerDto Source { get; init; }
    public long CustomerId => Source.CustomerId;
    public string Code => Source.Code;
    public string NameAr => Source.NameAr;
    public string PhoneAr => Source.Phone ?? "—";
    public string CreditAr => Source.CreditEnabled ? Source.CreditLimit.ToString("N0") + " ج.م (آجل)" : "نقدي";
    public string StatusAr => Source.AccountStatus switch
    {
        "ACTIVE" => "نشط", "SUSPENDED" => "معلّق", "ARCHIVED" => "مؤرشف", _ => Source.AccountStatus,
    };
}

/// <summary>سطر سلة/أمر شراء (PAGE-24) — مسودة قابلة للاستعادة لا تفتح مخزوناً ولا مالاً.</summary>
public sealed class PoLineRow
{
    public required PurchaseOrderLineDto Source { get; init; }
    public string Sku => Source.SkuCode + " · " + Source.SkuNameAr;
    public string QtyAr => Source.Qty.ToString("0.######") + " " + Source.UomCode;
    public string CostAr => Source.UnitCost.ToString("N2");
    public string TotalAr => (Source.Qty * Source.UnitCost).ToString("N2");
}

/// <summary>أمر شراء (PAGE-24) بحالته من السلة إلى الإقفال.</summary>
public sealed class PoRow
{
    public required PurchaseOrderDto Source { get; init; }
    public long PoId => Source.PoId;
    public string PoNumber => Source.PoNumber;
    public string Supplier => Source.SupplierNameAr;
    public string Warehouse => Source.WarehouseCode;
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "سلة/مسودة", "ORDERED" => "أمر صادر", "PARTIAL" => "استُلم بعضه",
        "RECEIVED" => "استُلم كاملاً", "CLOSED" => "مقفل بقصور", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string LinesAr => Source.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " سطراً";
    public string TotalAr => Source.LinesTotal.ToString("N2") + " ج.م";
}

/// <summary>سطر استلام (PAGE-25) بلقطة التحويل للوحدة الأساسية.</summary>
public sealed class ReceiptLineRow
{
    public required ReceiptLineDto Source { get; init; }
    public string Sku => Source.SkuCode + " · " + Source.SkuNameAr;
    public string QtyAr => Source.Qty.ToString("0.######") + " " + Source.UomCode;
    public string FactorAr => Source.ConversionFactor.ToString("0.######");
    public string BaseAr => Source.BaseQty.ToString("0.######");
    public string LotAr => Source.LotCode ?? "—";
    public string TotalAr => Source.LineTotal.ToString("N2");
}

/// <summary>استلام مورد (PAGE-25) — POSTED هو الذي زاد المخزون.</summary>
public sealed class ReceiptRow
{
    public required ReceiptDto Source { get; init; }
    public long ReceiptId => Source.ReceiptId;
    public string Number => Source.ReceiptNumber;
    public string Supplier => Source.SupplierNameAr;
    public string Warehouse => Source.WarehouseCode;
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة (لم ترحّل)", "POSTED" => "مرحّل (زاد المخزون)", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string LinesAr => Source.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " سطراً";
}

/// <summary>فاتورة مورد (PAGE-26) — التزام مالي لا حركة مخزون، مع متبقي السداد.</summary>
public sealed class InvoiceRow
{
    public required SupplierInvoiceDto Source { get; init; }
    public long InvoiceId => Source.InvoiceId;
    public string Number => Source.InvoiceNumber;
    public string Supplier => Source.SupplierNameAr;
    public string DateAr => Source.InvoiceDate.ToString("yyyy-MM-dd");
    public string TotalAr => Source.GrandTotal.ToString("N2");
    public string PaidAr => Source.PaidAmount.ToString("N2");
    public string RemainingAr => Source.RemainingAmount.ToString("N2");
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "POSTED" => "مرحّلة", "PARTIALLY_PAID" => "مسددة جزئياً",
        "PAID" => "مسددة", "CANCELLED" => "ملغاة", _ => Source.Status,
    };
}

/// <summary>سند سداد (PAGE-26) — أثر خزينة واحد مهما توزع على الفواتير.</summary>
public sealed class PaymentRow
{
    public required SupplierPaymentDto Source { get; init; }
    public long PaymentId => Source.PaymentId;
    public string Voucher => Source.VoucherNumber;
    public string Supplier => Source.SupplierNameAr;
    public string AmountAr => Source.Amount.ToString("N2") + " ج.م";
    public string MethodAr => Source.PaymentMethod switch
    {
        "CASH" => "نقدي", "BANK_TRANSFER" => "تحويل بنكي", "CHEQUE" => "شيك", "CARD" => "بطاقة", _ => Source.PaymentMethod,
    };
    public string DateAr => Source.PaymentDate.ToString("yyyy-MM-dd");
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "POSTED" => "مرحّل (أثر خزينة واحد)", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string AllocationsAr => Source.Allocations.Count == 0
        ? "على الحساب"
        : string.Join("، ", System.Linq.Enumerable.Select(Source.Allocations, a => a.InvoiceNumber + ": " + a.Amount.ToString("N2")));
}

/// <summary>مرتجع مورد (PAGE-27) مرتبط بالاستلام الأصلي.</summary>
public sealed class ReturnRow
{
    public required SupplierReturnDto Source { get; init; }
    public long ReturnId => Source.ReturnId;
    public string Number => Source.ReturnNumber;
    public string Supplier => Source.SupplierNameAr;
    public string OriginAr => Source.ReceiptNumber ?? "—";
    public string RefundAr => Source.RefundMethod switch
    {
        "CASH" => "رد نقدي (خزينة)", "CREDIT_NOTE" => "إشعار دائن (ذمم)", "BANK_TRANSFER" => "تحويل بنكي (خزينة)", _ => Source.RefundMethod,
    };
    public string TotalAr => Source.Total.ToString("N2");
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "POSTED" => "مرحّل", "CANCELLED" => "ملغى", _ => Source.Status,
    };
}

/// <summary>بند كشف حساب طرف (PAGE-30/32) — رصيد افتتاحي + حركة + ختامي.</summary>
public sealed class StatementLineRow
{
    public required PartyStatementEntryDto Source { get; init; }
    public string DocTypeAr => Source.DocType switch
    {
        "purch.invoice" => "فاتورة مورد", "purch.payment" => "سداد", "purch.return" => "إشعار دائن",
        "sales.invoice" => "فاتورة بيع", _ => Source.DocType,
    };
    public string Number => Source.DocNumber;
    public string DateAr => Source.Date.ToString("yyyy-MM-dd");
    public string AmountAr => Source.Amount.ToString("+N2;-N2;0.00");
    public string Detail => Source.Detail;
}

/// <summary>
/// نموذج المشتريات (PAGE-24..27): كل الكتابة بصلاحياتها (السلة للمخازني
/// والإدارة، الفواتير للمحاسب، السداد للمالك/المحاسب) — الحماية خادمية.
/// </summary>
public sealed partial class PurchasingViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _infoAr = string.Empty;

    public ObservableCollection<PoRow> Orders { get; } = [];
    public ObservableCollection<PoLineRow> SelectedOrderLines { get; } = [];
    public ObservableCollection<ReceiptRow> Receipts { get; } = [];
    public ObservableCollection<ReceiptLineRow> SelectedReceiptLines { get; } = [];
    public ObservableCollection<InvoiceRow> Invoices { get; } = [];
    public ObservableCollection<PaymentRow> Payments { get; } = [];
    public ObservableCollection<ReturnRow> Returns { get; } = [];
    public ObservableCollection<SupplierRow> Suppliers { get; } = [];
    public ObservableCollection<WarehouseDto> Warehouses { get; } = [];
    public ObservableCollection<TreasuryAccountDto> TreasuryAccounts { get; } = [];

    public PurchasingViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public bool CanManageBasket => _session.User?.Permissions.Contains("purchasing.manage") == true;
    public bool CanReceive => _session.User?.Permissions.Contains("purchasing.receive") == true;
    public bool CanInvoice => _session.User?.Permissions.Contains("purchasing.invoice.manage") == true;
    public bool CanPay => _session.User?.Permissions.Contains("purchasing.payment.post") == true;
    public bool CanReturn => _session.User?.Permissions.Contains("purchasing.return.post") == true;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        try
        {
            var suppliers = await _api.SearchSuppliersAsync(null, 1, 100, ct);
            if (suppliers.Ok)
            {
                Suppliers.Clear();
                foreach (var s in suppliers.Value!.Items) Suppliers.Add(new SupplierRow { Source = s });
            }
            var warehouses = await _api.GetWarehousesAsync(false, ct);
            if (warehouses.Ok)
            {
                Warehouses.Clear();
                foreach (var w in warehouses.Value!) Warehouses.Add(w);
            }
            var treasury = await _api.GetTreasuryAccountsAsync(ct);
            if (treasury.Ok)
            {
                TreasuryAccounts.Clear();
                foreach (var t in treasury.Value!) TreasuryAccounts.Add(t);
            }
            await LoadDocumentsAsync(ct);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadDocumentsAsync(CancellationToken ct)
    {
        var orders = await _api.GetPurchaseOrdersAsync(null, 1, 100, ct);
        if (orders.Ok)
        {
            Orders.Clear();
            foreach (var o in orders.Value!) Orders.Add(new PoRow { Source = o });
        }
        var receipts = await _api.GetReceiptsAsync(null, 1, 100, ct);
        if (receipts.Ok)
        {
            Receipts.Clear();
            foreach (var r in receipts.Value!) Receipts.Add(new ReceiptRow { Source = r });
        }
        var invoices = await _api.GetInvoicesAsync(null, null, 1, 100, ct);
        if (invoices.Ok)
        {
            Invoices.Clear();
            foreach (var i in invoices.Value!) Invoices.Add(new InvoiceRow { Source = i });
        }
        var payments = await _api.GetPaymentsAsync(null, null, ct);
        if (payments.Ok)
        {
            Payments.Clear();
            foreach (var p in payments.Value!) Payments.Add(new PaymentRow { Source = p });
        }
        var returns = await _api.GetReturnsAsync(null, null, ct);
        if (returns.Ok)
        {
            Returns.Clear();
            foreach (var r in returns.Value!) Returns.Add(new ReturnRow { Source = r });
        }
    }

    public async Task SelectOrderAsync(PoRow row, CancellationToken ct)
    {
        var details = await _api.GetPurchaseOrderAsync(row.PoId, ct);
        if (!details.Ok)
        {
            ErrorAr = details.ErrorAr;
            return;
        }
        SelectedOrderLines.Clear();
        foreach (var line in details.Value!.Lines) SelectedOrderLines.Add(new PoLineRow { Source = line });
    }

    public async Task SelectReceiptAsync(ReceiptRow row, CancellationToken ct)
    {
        var details = await _api.GetReceiptAsync(row.ReceiptId, ct);
        if (!details.Ok)
        {
            ErrorAr = details.ErrorAr;
            return;
        }
        SelectedReceiptLines.Clear();
        foreach (var line in details.Value!.Lines) SelectedReceiptLines.Add(new ReceiptLineRow { Source = line });
    }

    public async Task CreateBasketAsync(long supplierId, long warehouseId, List<CreatePurchaseOrderLineRequest> lines, CancellationToken ct)
    {
        var result = await _api.CreatePurchaseOrderAsync(new CreatePurchaseOrderRequest(supplierId, warehouseId, null, lines), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"أُنشئت السلة {result.Value!.PoNumber} — استرجعها متى شئت (لا مخزون ولا مال قبل الاستلام)";
        await LoadDocumentsAsync(ct);
    }

    public async Task SubmitOrderAsync(PoRow row, CancellationToken ct)
    {
        var result = await _api.PoActionAsync(row.PoId, "submit", null, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"صُدر الأمر {row.PoNumber}";
        await LoadDocumentsAsync(ct);
    }

    public async Task CancelOrderAsync(PoRow row, CancellationToken ct)
    {
        var result = await _api.PoActionAsync(row.PoId, "cancel", null, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"أُلغي {row.PoNumber}";
        await LoadDocumentsAsync(ct);
    }

    public async Task PostReceiptAsync(ReceiptRow row, CancellationToken ct)
    {
        var result = await _api.PostReceiptAsync(row.ReceiptId, "rc-" + row.ReceiptId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"رُحّل الاستلام {row.Number}: {result.Value!.MovementIds.Length} حركة دخول بإجمالي قاعدي {result.Value.TotalBaseQty:0.######}";
        await LoadDocumentsAsync(ct);
    }

    public async Task CreateInvoiceAsync(long supplierId, long? receiptId, string number, decimal subtotal, decimal tax, CancellationToken ct)
    {
        var result = await _api.CreateInvoiceAsync(new CreateSupplierInvoiceRequest(
            supplierId, receiptId, number, DateOnly.FromDateTime(DateTime.Now), null, subtotal, tax, []), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"أُنشئت الفاتورة {number} بإجمالي {result.Value!.GrandTotal:N2} — التزام مالي بلا حركة مخزون";
        await LoadDocumentsAsync(ct);
    }

    public async Task PostInvoiceAsync(InvoiceRow row, CancellationToken ct)
    {
        var result = await _api.PostInvoiceAsync(row.InvoiceId, "inv-" + row.InvoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"رُحّلت الفاتورة {row.Number}";
        await LoadDocumentsAsync(ct);
    }

    public async Task CreatePaymentAsync(long supplierId, long treasuryId, decimal amount, string method, List<CreatePaymentAllocationRequest> allocations, CancellationToken ct)
    {
        var result = await _api.CreatePaymentAsync(new CreateSupplierPaymentRequest(
            supplierId, treasuryId, amount, method, DateOnly.FromDateTime(DateTime.Now), null, allocations), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        var paymentId = result.Value!.PaymentId;
        var posted = await _api.PostPaymentAsync(paymentId, "pay-" + paymentId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        if (!posted.Ok)
        {
            ErrorAr = posted.ErrorAr;
            return;
        }
        InfoAr = $"رُحّل السداد {result.Value.VoucherNumber}: أثر خزينة واحد #{posted.Value!.TreasuryTxnId}";
        await LoadDocumentsAsync(ct);
    }
}

/// <summary>
/// نموذج الأطراف (PAGE-29..32): سجل العملاء والموردين والتعطيل لا الحذف،
/// وكشوف الحساب بفلترة الفترة ونوع المستند — إدارة السجل للمالك/المدير.
/// </summary>
public sealed partial class PartiesViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _supplierSearch = string.Empty;

    [ObservableProperty]
    private string _customerSearch = string.Empty;

    public ObservableCollection<SupplierRow> Suppliers { get; } = [];
    public ObservableCollection<CustomerRow> Customers { get; } = [];

    public PartiesViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public bool CanManage => _session.User?.Permissions.Contains("suppliers.manage") == true;

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        try
        {
            await LoadSuppliersAsync(ct);
            await LoadCustomersAsync(ct);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadSuppliersAsync(CancellationToken ct)
    {
        var result = await _api.SearchSuppliersAsync(SupplierSearch, 1, 100, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        Suppliers.Clear();
        foreach (var s in result.Value!.Items) Suppliers.Add(new SupplierRow { Source = s });
    }

    [RelayCommand]
    private async Task LoadCustomersAsync(CancellationToken ct)
    {
        var result = await _api.SearchCustomersAsync(CustomerSearch, 1, 100, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        Customers.Clear();
        foreach (var c in result.Value!.Items) Customers.Add(new CustomerRow { Source = c });
    }

    public async Task CreateSupplierAsync(string code, string name, string? phone, int terms, CancellationToken ct)
    {
        var result = await _api.CreateSupplierAsync(new CreateSupplierRequest(code, name, null, phone, null, null, terms, null, null), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        await LoadSuppliersAsync(ct);
    }

    public async Task CreateCustomerAsync(string code, string name, string? phone, CancellationToken ct)
    {
        var result = await _api.CreateCustomerAsync(new CreateCustomerRequest(code, name, phone, null, null, 0m, false, 0, null), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        await LoadCustomersAsync(ct);
    }

    public async Task SetSupplierStatusAsync(SupplierRow row, string status, string reason, CancellationToken ct)
    {
        var result = await _api.SetSupplierStatusAsync(row.SupplierId, status, reason, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        await LoadSuppliersAsync(ct);
    }

    public async Task SetCustomerStatusAsync(CustomerRow row, string status, string reason, CancellationToken ct)
    {
        var result = await _api.SetCustomerStatusAsync(row.CustomerId, status, reason, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        await LoadCustomersAsync(ct);
    }

    public async Task<PartyStatementResponse?> GetSupplierStatementAsync(SupplierRow row, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = await _api.GetSupplierStatementAsync(row.SupplierId, from, to, null, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return null;
        }
        return result.Value!;
    }

    public async Task<PartyStatementResponse?> GetCustomerStatementAsync(CustomerRow row, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = await _api.GetCustomerStatementAsync(row.CustomerId, from, to, null, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return null;
        }
        return result.Value!;
    }
}
