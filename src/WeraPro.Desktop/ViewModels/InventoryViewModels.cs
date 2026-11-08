// ViewModels/InventoryViewModels.cs — WERA PRO HD Desktop (G6)
// المخزون (§12.1/§12.2): لوحة الأرصدة (PAGE-18) والنواقص (PAGE-22) والدفعات
// (PAGE-23) والتقييم (D-02 WAC) + كرت الحركة (PAGE-19) + الجرد والتسويات
// (PAGE-20) + التحويلات بين المخازن (PAGE-21) + إدارة المخازن (PAGE-28) —
// كل البيانات من الخادم عبر WeraApiClient (§2.5 مصدر حقيقة واحد).
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;

namespace WeraPro.Desktop.ViewModels;

/// <summary>صف رصيد في لوحة المخزون: الرصيد من الدفتر والمتاح = الرصيد − المحجوز (§12.1).</summary>
public sealed class BalanceRow
{
    public required StockBalanceDto Source { get; init; }
    public long SkuId => Source.SkuId;
    public string SkuCode => Source.SkuCode;
    public string Product => Source.ProductNameAr + (string.IsNullOrEmpty(Source.SkuNameAr) ? "" : $" · {Source.SkuNameAr}");
    public string Warehouse => Source.WarehouseCode;
    public string SaleModeAr => Source.SaleMode switch
    {
        "COUNT" => "عددي", "WEIGHT" => "وزني", "PACK" => "عبوة", "SERIALIZED" => "سيريال", _ => Source.SaleMode,
    };
    public string BaseQtyAr => Source.BaseQty.ToString("0.######") + " أساس";
    public string ReservedAr => Source.ReservedQty == 0 ? "—" : Source.ReservedQty.ToString("0.######");
    public string AvailableAr => Source.AvailableQty.ToString("0.######");
    public string ReorderAr => Source.ReorderLevel.ToString("0.######");
    public string ValuationAr => Source.Valuation is { } v ? v.ToString("N2") + " ج.م" : "—";
    public bool BelowReorder => Source.BelowReorder;
}

/// <summary>توصية إعادة طلب (PAGE-22) — قاعدة حتمية: رفع المتاح إلى مستوى إعادة الطلب.</summary>
public sealed class ReorderRow
{
    public required ReorderSuggestionDto Source { get; init; }
    public string SkuCode => Source.SkuCode;
    public string Product => Source.ProductNameAr + " · " + Source.SkuNameAr;
    public string Warehouse => Source.WarehouseCode;
    public string AvailableAr => Source.AvailableQty.ToString("0.######");
    public string LevelAr => Source.ReorderLevel.ToString("0.######");
    public string SuggestedAr => Source.SuggestedQty.ToString("0.######");
}

/// <summary>دفعة/تشغيلة (PAGE-23) مع رصيدها من الدفتر وأصل الاستلام.</summary>
public sealed class LotRow
{
    public required LotDto Source { get; init; }
    public string SkuCode => Source.SkuCode;
    public string LotCode => Source.LotCode;
    public string ExpiryAr => Source.ExpiryDate?.ToString("yyyy-MM-dd") ?? "بلا صلاحية";
    public string OnHandAr => Source.OnHandQty.ToString("0.######");
    public string ReceiptAr => Source.ReceiptNumber ?? "—";
    public string ExpiryStateAr => Source.ExpiryDate is null ? "" : Source.IsExpiringSoon ? "قريبة الانتهاء" : "سارية";
    public bool ExpiringSoon => Source.IsExpiringSoon;
}

/// <summary>حركة في كرت المنتج (PAGE-19) — حقيقة موثقة بمصدرها ومعامل تحويلها ورصيدها الجاري.</summary>
public sealed class MovementRow
{
    public required StockMovementCardDto Source { get; init; }
    public long MovementId => Source.MovementId;
    public string TypeAr => Source.MovementType switch
    {
        "RECEIVE" => "استلام", "SALE" => "بيع", "SALE_RETURN" => "مرتجع بيع", "SUPPLIER_RETURN" => "مرتجع مورد",
        "TRANSFER_OUT" => "تحويل خروج", "TRANSFER_IN" => "تحويل دخول", "ADJUSTMENT_IN" => "تسوية زيادة",
        "ADJUSTMENT_OUT" => "تسوية نقص", "DAMAGE" => "هالك", "EXPIRY" => "تلف صلاحية", "OPENING" => "افتتاحي", _ => Source.MovementType,
    };
    public string DirectionAr => Source.Direction == 1 ? "دخول +" : "خروج −";
    public string QtyAr => Source.Direction == 1 ? "+" : "−" + Source.BaseQty.ToString("0.######");
    public string FactorAr => Source.ConversionFactor.ToString("0.######");
    public string SourceAr => Source.SourceDocType + " #" + Source.SourceDocId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string WhenAr => Source.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string BalanceAr => Source.RunningBalance.ToString("0.######");
    public string ReasonAr => Source.Reason ?? "—";
    public string ByAr => Source.Username;
}

/// <summary>مخزن في صفحة الإدارة (PAGE-28) — رئيسي/فرعي/خارجي، والتعطيل لا الحذف.</summary>
public sealed class WarehouseRow
{
    public required WarehouseDto Source { get; init; }
    public long WarehouseId => Source.WarehouseId;
    public string Code => Source.Code;
    public string NameAr => Source.NameAr;
    public string Branch => Source.BranchCode;
    public string TypeAr => Source.WhType switch
    {
        "MAIN" => "رئيسي", "BRANCH" => "فرع", "SUB" => "فرعي", "EXTERNAL" => "خارجي", _ => Source.WhType,
    };
    public string StateAr => Source.IsActive ? "نشط" : "معطّل";
}

/// <summary>سطر جرد (PAGE-20): لقطة الدفتر وقت العد والفرق محسوب لا مُدخل.</summary>
public sealed class CountLineRow
{
    public required StocktakeLineDto Source { get; init; }
    public long SkuId => Source.SkuId;
    public string SkuCode => Source.SkuCode;
    public string Name => Source.SkuNameAr;
    public string SystemAr => Source.SystemQty.ToString("0.######");
    public string CountedAr => Source.CountedQty.ToString("0.######");
    public string VarianceAr => (Source.VarianceQty == 0 ? "مطابق" : Source.VarianceQty.ToString("+0.######;-0.######"));
    public bool HasVariance => Source.VarianceQty != 0;
}

/// <summary>جلسة جرد (PAGE-20) بحالة سيرها وفصل واجبات اعتمادها.</summary>
public sealed class StocktakeRow
{
    public required StocktakeDto Source { get; init; }
    public long CountId => Source.CountId;
    public string Warehouse => Source.WarehouseCode;
    public string StatusAr => Source.Status switch
    {
        "OPEN" => "مفتوحة", "COUNTED" => "مُعدّة", "RECONCILED" => "مطابَقة", "APPROVED" => "معتمدة", "CANCELLED" => "ملغاة", _ => Source.Status,
    };
    public string CounterAr => Source.CountedByName;
    public string ApproverAr => Source.ApprovedByName ?? "—";
    public string OpenedAr => Source.OpenedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string LinesAr => Source.LineCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string VarianceAr => Source.TotalVariance.ToString("+0.######;-0.######;مطابق");
}

/// <summary>تحويل بين مخزنين (PAGE-21) بحالتي إرسال/استلام المستقلتين.</summary>
public sealed class TransferRow
{
    public required TransferDto Source { get; init; }
    public long TransferId => Source.TransferId;
    public string RouteAr => Source.FromWarehouseCode + " ← " + Source.ToWarehouseCode;
    public string StatusAr => Source.Status switch
    {
        "DRAFT" => "مسودة", "SENT" => "مُرسل", "RECEIVED" => "مُستلم", "CANCELLED" => "ملغى", _ => Source.Status,
    };
    public string LinesAr => Source.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " سطراً";
    public string CreatedAr => Source.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// نموذج لوحة المخزون (PAGE-18/22/23 + التقييم): قراءة الجميع، وعمليات الحجز
/// والهالك لمن يملك صلاحيته — الحماية الحاسمة خادمية (§18).
/// </summary>
public sealed partial class InventoryDashboardViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    public const int PageSize = 50;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private WarehouseDto? _warehouseFilter;

    [ObservableProperty]
    private bool _belowReorderOnly;

    [ObservableProperty]
    private int _page = 1;

    [ObservableProperty]
    private long _totalCount;

    [ObservableProperty]
    private decimal _totalValuation;

    public ObservableCollection<WarehouseDto> Warehouses { get; } = [];
    public ObservableCollection<BalanceRow> Balances { get; } = [];
    public ObservableCollection<ReorderRow> Reorder { get; } = [];
    public ObservableCollection<LotRow> Lots { get; } = [];
    public ObservableCollection<ValuationLineDto> ValuationLines { get; } = [];

    public InventoryDashboardViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public bool CanReserve => _session.User?.Permissions.Contains("inventory.reservation.manage") == true;
    public bool CanAdjust => _session.User?.Permissions.Contains("inventory.movement.post") == true;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        try
        {
            await LoadWarehousesAsync(ct);
            await LoadBalancesAsync(ct);
            await LoadAdvisoriesAsync(ct);
        }
        catch (Exception ex)
        {
            ErrorAr = "تعذر تحميل لوحة المخزون: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadWarehousesAsync(CancellationToken ct)
    {
        var result = await _api.GetWarehousesAsync(includeInactive: false, ct);
        if (result.Ok)
        {
            Warehouses.Clear();
            foreach (var w in result.Value!) Warehouses.Add(w);
        }
    }

    public async Task LoadBalancesAsync(CancellationToken ct)
    {
        var result = await _api.SearchBalancesAsync(SearchText, WarehouseFilter?.WarehouseId, BelowReorderOnly, Page, PageSize, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        Balances.Clear();
        foreach (var b in result.Value!.Items) Balances.Add(new BalanceRow { Source = b });
        TotalCount = result.Value.TotalCount;
    }

    public async Task LoadAdvisoriesAsync(CancellationToken ct)
    {
        var reorder = await _api.GetReorderAsync(WarehouseFilter?.WarehouseId, ct);
        if (reorder.Ok)
        {
            Reorder.Clear();
            foreach (var r in reorder.Value!) Reorder.Add(new ReorderRow { Source = r });
        }
        var lots = await _api.GetLotsAsync(expiringInDays: 30, ct);
        if (lots.Ok)
        {
            Lots.Clear();
            foreach (var l in lots.Value!) Lots.Add(new LotRow { Source = l });
        }
        var valuation = await _api.GetValuationAsync(WarehouseFilter?.WarehouseId, ct);
        if (valuation.Ok)
        {
            ValuationLines.Clear();
            foreach (var line in valuation.Value!.Lines) ValuationLines.Add(line);
            TotalValuation = valuation.Value.TotalValuation;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken ct)
    {
        Page = 1;
        IsLoading = true;
        await LoadBalancesAsync(ct);
        await LoadAdvisoriesAsync(ct);
        IsLoading = false;
    }

    [RelayCommand]
    public async Task NextPageAsync(CancellationToken ct)
    {
        if (Page * PageSize < TotalCount)
        {
            Page++;
            await LoadBalancesAsync(ct);
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync(CancellationToken ct)
    {
        if (Page > 1)
        {
            Page--;
            await LoadBalancesAsync(ct);
        }
    }
}

/// <summary>
/// نموذج كرت الحركة (PAGE-19): تسلسل حركات موثقة بمصدرها ومعامل تحويلها
/// ورصيدها الجاري — تقرير يطابق الدفتر حرفياً (بوابة القبول).
/// </summary>
public sealed partial class MovementCardViewModel : ObservableObject
{
    private readonly WeraApiClient _api;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _skuCode = string.Empty;

    [ObservableProperty]
    private string _titleAr = string.Empty;

    public ObservableCollection<MovementRow> Movements { get; } = [];

    public MovementCardViewModel(WeraApiClient api) => _api = api;

    public async Task LoadAsync(long skuId, long? warehouseId, string skuCode, CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        SkuCode = skuCode;
        TitleAr = $"كرت حركة {skuCode}";
        try
        {
            var result = await _api.GetMovementCardAsync(skuId, warehouseId, ct);
            if (!result.Ok)
            {
                ErrorAr = result.ErrorAr;
                return;
            }
            Movements.Clear();
            foreach (var m in result.Value!) Movements.Add(new MovementRow { Source = m });
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>
/// نموذج الجرد والتسويات (PAGE-20): فتح وتسجيل وإغلاق ومطابقة لمن يملك
/// inventory.stocktake.run، والاعتماد لمن يملك inventory.stocktake.approve
/// (المعتمد ≠ الجارّ — قيد الخادم) — الفرق الصفر بلا حركة.
/// </summary>
public sealed partial class StocktakeViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private StocktakeRow? _selected;

    [ObservableProperty]
    private string _infoAr = string.Empty;

    public ObservableCollection<StocktakeRow> Sessions { get; } = [];
    public ObservableCollection<CountLineRow> SelectedLines { get; } = [];
    public ObservableCollection<WarehouseDto> Warehouses { get; } = [];

    public StocktakeViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public bool CanRun => _session.User?.Permissions.Contains("inventory.stocktake.run") == true;
    public bool CanApprove => _session.User?.Permissions.Contains("inventory.stocktake.approve") == true;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        try
        {
            var warehouses = await _api.GetWarehousesAsync(false, ct);
            if (warehouses.Ok)
            {
                Warehouses.Clear();
                foreach (var w in warehouses.Value!) Warehouses.Add(w);
            }
            var sessions = await _api.GetStocktakesAsync(1, 50, ct);
            if (!sessions.Ok)
            {
                ErrorAr = sessions.ErrorAr;
                return;
            }
            Sessions.Clear();
            foreach (var s in sessions.Value!) Sessions.Add(new StocktakeRow { Source = s });
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenSessionAsync(WarehouseDto warehouse, string? notes, CancellationToken ct)
    {
        var result = await _api.OpenStocktakeAsync(new OpenStocktakeRequest(warehouse.WarehouseId, notes), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        await LoadAsync(ct);
        await SelectAsync(new StocktakeRow { Source = result.Value! }, ct);
    }

    public async Task SelectAsync(StocktakeRow row, CancellationToken ct)
    {
        Selected = row;
        var details = await _api.GetStocktakeAsync(row.CountId, ct);
        if (!details.Ok)
        {
            ErrorAr = details.ErrorAr;
            return;
        }
        SelectedLines.Clear();
        foreach (var line in details.Value!.Lines) SelectedLines.Add(new CountLineRow { Source = line });
        InfoAr = $"الجلسة #{row.CountId} · {row.StatusAr} · جارّها {row.CounterAr}"
            + (row.Source.ApprovedByName is null ? "" : $" · اعتمدها {row.Source.ApprovedByName}");
    }

    public async Task RecordAsync(long skuId, decimal counted, CancellationToken ct)
    {
        if (Selected is null) return;
        var result = await _api.RecordCountLinesAsync(Selected.CountId,
            new RecordCountLinesRequest([new RecordCountLineRequest(skuId, counted)]), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        SelectedLines.Clear();
        foreach (var line in result.Value.Lines) SelectedLines.Add(new CountLineRow { Source = line });
    }

    public async Task ActionAsync(string action, CancellationToken ct)
    {
        if (Selected is null) return;
        ErrorAr = null;
        if (action == "approve")
        {
            var result = await _api.ApproveStocktakeAsync(Selected.CountId, ct);
            if (!result.Ok)
            {
                ErrorAr = result.ErrorAr;
                return;
            }
            InfoAr = $"اعتُمدت الجلسة: أسطر معدلة {result.Value.AdjustedLines} · فرق صفر {result.Value.ZeroVarianceLines} · حركات {result.Value.MovementIds.Length}";
        }
        else
        {
            var result = await _api.StocktakeActionAsync(Selected.CountId, action, ct);
            if (!result.Ok)
            {
                ErrorAr = result.ErrorAr;
                return;
            }
        }
        await LoadAsync(ct);
        // أعد اختيار الجلسة بعد تحديث حالتها
        if (Sessions.FirstOrDefault(s => s.CountId == Selected?.CountId) is { } refreshed)
        {
            await SelectAsync(refreshed, ct);
        }
    }
}

/// <summary>
/// نموذج التحويلات (PAGE-21): إنشاء وإرسال واستلام — حركتان مستقلتان
/// في الدفتر (خروج ثم دخول)؛ والنقص المؤقت يتطلب اعتماد مشرف (D-03).
/// </summary>
public sealed partial class TransfersViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _infoAr = string.Empty;

    public ObservableCollection<TransferRow> Transfers { get; } = [];
    public ObservableCollection<WarehouseDto> Warehouses { get; } = [];

    public TransfersViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public bool CanOperate => _session.User?.Permissions.Contains("inventory.movement.post") == true;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        try
        {
            var warehouses = await _api.GetWarehousesAsync(false, ct);
            if (warehouses.Ok)
            {
                Warehouses.Clear();
                foreach (var w in warehouses.Value!) Warehouses.Add(w);
            }
            var transfers = await _api.GetTransfersAsync(null, 1, 50, ct);
            if (!transfers.Ok)
            {
                ErrorAr = transfers.ErrorAr;
                return;
            }
            Transfers.Clear();
            foreach (var t in transfers.Value!) Transfers.Add(new TransferRow { Source = t });
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CreateAsync(WarehouseDto from, WarehouseDto to, long skuId, decimal qty, CancellationToken ct)
    {
        var result = await _api.CreateTransferAsync(new CreateTransferRequest(
            from.WarehouseId, to.WarehouseId, [new CreateTransferLineRequest(skuId, qty)]), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"أُنشئ التحويل {result.Value!.TransferId} ({result.Value.FromWarehouseCode} ← {result.Value.ToWarehouseCode}) — أرسله ليخرج من الدفتر";
        await LoadAsync(ct);
    }

    public async Task SendAsync(TransferRow row, CancellationToken ct)
    {
        ErrorAr = null;
        var result = await _api.SendTransferAsync(row.TransferId, null, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr + (result.ErrorAr.Contains("اعتماد", StringComparison.Ordinal)
                ? " — أنشئ طلب اعتماد ودع المشرف يبت ثم أعد الإرسال بمفتاحه" : "");
            return;
        }
        InfoAr = $"أُرسل التحويل {row.TransferId}: {result.Value!.MovementIds.Length} حركة خروج";
        await LoadAsync(ct);
    }

    public async Task SendWithApprovalAsync(TransferRow row, long approvalId, CancellationToken ct)
    {
        ErrorAr = null;
        var result = await _api.SendTransferAsync(row.TransferId, approvalId, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"أُرسل التحويل {row.TransferId} باعتماد D-03: {result.Value!.MovementIds.Length} حركة خروج";
        await LoadAsync(ct);
    }

    public async Task ReceiveAsync(TransferRow row, CancellationToken ct)
    {
        ErrorAr = null;
        var result = await _api.ReceiveTransferAsync(row.TransferId, ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"استُلم التحويل {row.TransferId}: {result.Value!.MovementIds.Length} حركة دخول";
        await LoadAsync(ct);
    }
}

/// <summary>
/// نموذج إدارة المخازن (PAGE-28): تعريف رئيسي/فرعي/خارجي وتعطيل بلا حذف —
/// الأرشفة قرار تشغيلي والإدارة للمالك/المدير (رصد الصفحة).
/// </summary>
public sealed partial class WarehousesViewModel : ObservableObject
{
    private readonly WeraApiClient _api;
    private readonly SessionService _session;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorAr;

    [ObservableProperty]
    private string _infoAr = string.Empty;

    public ObservableCollection<WarehouseRow> Warehouses { get; } = [];
    public ObservableCollection<BranchDto> Branches { get; } = [];

    public WarehousesViewModel(WeraApiClient api, SessionService session)
    {
        _api = api;
        _session = session;
    }

    public bool CanManage => _session.User?.Permissions.Contains("inventory.warehouse.manage") == true;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        ErrorAr = null;
        try
        {
            var branches = await _api.GetBranchesAsync(ct);
            if (branches.Ok)
            {
                Branches.Clear();
                foreach (var b in branches.Value!) Branches.Add(b);
            }
            var result = await _api.GetWarehousesAsync(includeInactive: true, ct);
            if (!result.Ok)
            {
                ErrorAr = result.ErrorAr;
                return;
            }
            Warehouses.Clear();
            foreach (var w in result.Value!) Warehouses.Add(new WarehouseRow { Source = w });
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task CreateAsync(BranchDto branch, string code, string name, string whType, CancellationToken ct)
    {
        var result = await _api.CreateWarehouseAsync(new CreateWarehouseRequest(branch.BranchId, code, name, whType), ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        InfoAr = $"أُنشئ المخزن {code} ({name})";
        await LoadAsync(ct);
    }

    public async Task ToggleAsync(WarehouseRow row, CancellationToken ct)
    {
        var result = await _api.SetWarehouseStatusAsync(row.WarehouseId, !row.Source.IsActive, "تعطيل/تنشيط من إدارة المخازن", ct);
        if (!result.Ok)
        {
            ErrorAr = result.ErrorAr;
            return;
        }
        await LoadAsync(ct);
    }
}
