using Microsoft.EntityFrameworkCore;
using Npgsql;
using WeraPro.Application.Sales;
using WeraPro.Domain.Purchasing;
using WeraPro.Domain.Sales;
using WeraPro.Domain.SharedKernel;
using WeraPro.Infrastructure.Persistence.Entities;

namespace WeraPro.Infrastructure.Persistence.Repositories;

/// <summary>
/// مستودع المبيعات (G7 · §9/§15.4): كل الترحيل ذري داخل معاملة واحدة —
/// فاتورة + سطور بلقطاتها + مدفوعات + آثار خزينة + حركات دفتر SALE على صنف
/// المحتوى + تحديث طبقة WAC + حدث درج + مهمة إيصال §29.4 — والمشغلات (V003/V005)
/// هي خط الحماية الأخير: DRAFT→POSTED لا يمر إلا بمجاميع متسقة وتكلفة ووزن ومدفوع.
/// </summary>
public sealed class SalesRepository : ISalesRepository
{
    private readonly WeraDbContext _db;
    private readonly TimeProvider _clock;

    public SalesRepository(WeraDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // ═══════════════ القراءات الأساسية ═══════════════════════════════════════

    public async Task<PosStationData?> GetStationAsync(long stationId, CancellationToken ct = default)
    {
        var row = await (from st in _db.Stations.AsNoTracking()
                         join br in _db.Branches.AsNoTracking() on st.BranchId equals br.BranchId
                         where st.StationId == stationId
                         select new { st.StationId, st.Code, st.NameAr, br.BranchId, BranchName = br.NameAr })
                        .FirstOrDefaultAsync(ct);
        return row is null ? null : new PosStationData(
            row.StationId, row.Code, row.NameAr, row.BranchId, row.BranchName);
    }

    public async Task<DrawerAccountData?> GetDrawerAccountAsync(long branchId, long? stationId = null, CancellationToken ct = default)
    {
        // G8/V019: درج المحطة أولاً (الحقيقة الفيزيائية — درج لكل محطة) ثم درج الفرع (توافق خلفي G7)
        if (stationId is { } sid)
        {
            var stationDrawer = await _db.Set<TreasuryAccountRow>().AsNoTracking()
                .Where(t => t.IsActive && t.AccountKind == "CASH_DRAWER" && t.StationId == sid)
                .OrderBy(t => t.TreasuryAccountId)
                .Select(t => new DrawerAccountData(t.TreasuryAccountId, t.Code, t.NameAr))
                .FirstOrDefaultAsync(ct);
            if (stationDrawer is not null)
            {
                return stationDrawer;
            }
        }
        return await _db.Set<TreasuryAccountRow>().AsNoTracking()
            .Where(t => t.IsActive && t.AccountKind == "CASH_DRAWER" && t.StationId == null && t.BranchId == branchId)
            .OrderBy(t => t.TreasuryAccountId)
            .Select(t => new DrawerAccountData(t.TreasuryAccountId, t.Code, t.NameAr))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DrawerAccountData?> GetProviderAccountAsync(CancellationToken ct = default) =>
        await _db.Set<TreasuryAccountRow>().AsNoTracking()
            .Where(t => t.IsActive && t.AccountKind == "PAYMENT_PROVIDER")
            .OrderBy(t => t.TreasuryAccountId)
            .Select(t => new DrawerAccountData(t.TreasuryAccountId, t.Code, t.NameAr))
            .FirstOrDefaultAsync(ct);

    public async Task<long?> GetPosWarehouseIdAsync(long branchId, CancellationToken ct = default) =>
        await _db.Warehouses.AsNoTracking()
            .Where(w => w.BranchId == branchId && w.IsActive)
            .OrderBy(w => w.WhType != "MAIN").ThenBy(w => w.WarehouseId)   // MAIN أولاً
            .Select(w => (long?)w.WarehouseId)
            .FirstOrDefaultAsync(ct);

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default) =>
        await _db.BusinessSettings.AsNoTracking()
            .Where(s => s.SettingKey == key)
            .Select(s => s.ValueText)
            .FirstOrDefaultAsync(ct);

    public async Task<CustomerCreditData?> GetCustomerCreditAsync(long customerId, CancellationToken ct = default) =>
        await _db.Customers.AsNoTracking()
            .Where(c => c.CustomerId == customerId)
            .Select(c => new CustomerCreditData(c.CustomerId, c.NameAr, c.CreditEnabled, c.CreditLimit, c.AccountStatus))
            .FirstOrDefaultAsync(ct);

    /// <summary>الرصيد القائم للعميل: آجل مُرحّل − مرتجعات آجلة — من الدفتر لا رقم مخزن (§13).</summary>
    public async Task<decimal> GetCustomerOutstandingCreditAsync(long customerId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE((SELECT SUM(p.amount) FROM sales.payments p
                              JOIN sales.invoices i ON i.invoice_id = p.invoice_id
                             WHERE i.customer_id = @cust AND i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
                               AND p.payment_method = 'CREDIT' AND p.status IN ('CAPTURED','SETTLED')), 0)
                 - COALESCE((SELECT SUM(r.total) FROM sales.refunds r
                              JOIN sales.invoices i2 ON i2.invoice_id = r.original_invoice_id
                             WHERE i2.customer_id = @cust AND r.status = 'POSTED'
                               AND r.refund_method = 'CREDIT_REVERSAL'), 0)
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("cust", customerId);
        return await cmd.ExecuteScalarAsync(ct) is decimal d ? d : 0m;
    }

    public async Task<long?> FindInvoiceIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.IdempotencyKey == idempotencyKey)
            .Select(i => (long?)i.InvoiceId)
            .FirstOrDefaultAsync(ct);

    public async Task<long?> FindRefundIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        await _db.SalesRefunds.AsNoTracking()
            .Where(r => r.IdempotencyKey == idempotencyKey)
            .Select(r => (long?)r.RefundId)
            .FirstOrDefaultAsync(ct);

    // ═══════════════ بيانات صنف البيع (التسعير + المتاح + WAC — استعلام واحد) ═══

    public async Task<SaleItemData?> GetSaleItemDataAsync(long skuId, long warehouseId, long? priceListId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.sku_id, s.sku_code, s.sale_mode, s.base_uom_id, s.sale_uom_id, s.allow_negative,
                   p.product_id, p.name_ar,
                   su.code AS sale_uom_code, bu.code AS base_uom_code,
                   pr.unit_price, pr.price_list_id, pr.unit_id AS price_uom_id, pu.code AS price_uom_code,
                   pr.tax_treatment,
                   pkg.package_id, pkg.units_per_package, pkg.content_sku_id,
                   cs.sku_code AS content_sku_code, cs.base_uom_id AS content_base_uom_id,
                   s.weight_precision_g, s.weight_step_g, s.allow_manual_weight, s.allow_amount_target,
                   s.min_weight_kg, s.max_weight_kg, s.default_tare_kg,
                   COALESCE(sb.base_qty, 0) AS balance_qty, COALESCE(sb.reserved_qty, 0) AS reserved_qty,
                   COALESCE(cl.avg_unit_cost, 0) AS cost_avg,
                   img.media_id
              FROM catalog.skus s
              JOIN catalog.products p ON p.product_id = s.product_id
              JOIN catalog.units su ON su.uom_id = s.sale_uom_id
              JOIN catalog.units bu ON bu.uom_id = s.base_uom_id
              LEFT JOIN LATERAL (
                  SELECT pp.unit_price, pp.price_list_id, pp.unit_id, pp.tax_treatment
                    FROM catalog.product_prices pp
                    JOIN catalog.price_lists pl ON pl.price_list_id = pp.price_list_id AND pl.is_active
                   WHERE pp.sku_id = s.sku_id
                     AND (pp.valid_to IS NULL OR pp.valid_to > now())
                     AND ((@list::bigint IS NULL AND pl.is_default) OR pl.price_list_id = @list)
                   ORDER BY pp.valid_from DESC
                   LIMIT 1) pr ON true
              LEFT JOIN catalog.units pu ON pu.uom_id = pr.unit_id
              LEFT JOIN catalog.packages pkg
                     ON pkg.package_sku_id = s.sku_id AND (pkg.valid_to IS NULL OR pkg.valid_to > now())
              LEFT JOIN catalog.skus cs ON cs.sku_id = pkg.content_sku_id
              LEFT JOIN inv.stock_balances sb
                     ON sb.sku_id = COALESCE(pkg.content_sku_id, s.sku_id) AND sb.warehouse_id = @wh
              LEFT JOIN finance.cost_layers cl
                     ON cl.sku_id = COALESCE(pkg.content_sku_id, s.sku_id) AND cl.warehouse_id = @wh
              LEFT JOIN LATERAL (
                  SELECT pi.media_id FROM catalog.product_images pi
                   WHERE pi.sku_id = s.sku_id OR pi.product_id = s.product_id
                   ORDER BY pi.sku_id IS NULL, pi.is_primary DESC, pi.sort_order LIMIT 1) img ON true
             WHERE s.sku_id = @sku AND s.status = 'ACTIVE'
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sku", skuId);
        cmd.Parameters.AddWithValue("wh", warehouseId);
        cmd.Parameters.AddWithValue("list", (object?)priceListId ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return null;
        }

        var taxRate = 0.1400m;
        if (decimal.TryParse(await GetSettingAsync("tax.standard_rate", ct) ?? "0.1400",
                System.Globalization.CultureInfo.InvariantCulture, out var parsedRate))
        {
            taxRate = parsedRate;
        }

        var balance = GetDecimal(r, "balance_qty");
        var reserved = GetDecimal(r, "reserved_qty");
        var mediaId = r["media_id"] is DBNull ? null : (long?)r.GetInt64(r.GetOrdinal("media_id"));

        return new SaleItemData(
            SkuId: skuId,
            SkuCode: (string)r["sku_code"],
            ProductId: r.GetInt64(r.GetOrdinal("product_id")),
            ProductNameAr: (string)r["name_ar"],
            SaleMode: (string)r["sale_mode"],
            SaleUomId: r.GetInt64(r.GetOrdinal("sale_uom_id")),
            SaleUomCode: (string)r["sale_uom_code"],
            BaseUomId: r.GetInt64(r.GetOrdinal("base_uom_id")),
            BaseUomCode: (string)r["base_uom_code"],
            UnitPrice: r["unit_price"] is DBNull ? 0m : GetDecimal(r, "unit_price"),
            PriceListId: r["price_list_id"] is DBNull ? null : r.GetInt64(r.GetOrdinal("price_list_id")),
            PriceUomId: r["price_uom_id"] is DBNull ? 0 : r.GetInt64(r.GetOrdinal("price_uom_id")),
            PriceUomCode: r["price_uom_code"] is DBNull ? string.Empty : (string)r["price_uom_code"],
            TaxTreatment: r["tax_treatment"] is DBNull ? "TAXABLE" : (string)r["tax_treatment"],
            TaxRate: taxRate,
            AllowNegative: r["allow_negative"] is DBNull ? null : (bool)r["allow_negative"],
            PackageId: r["package_id"] is DBNull ? null : r.GetInt64(r.GetOrdinal("package_id")),
            UnitsPerPackage: r["units_per_package"] is DBNull ? null : GetDecimal(r, "units_per_package"),
            ContentSkuId: r["content_sku_id"] is DBNull ? null : r.GetInt64(r.GetOrdinal("content_sku_id")),
            ContentSkuCode: r["content_sku_code"] is DBNull ? null : (string)r["content_sku_code"],
            ContentBaseUomId: r["content_base_uom_id"] is DBNull ? null : r.GetInt64(r.GetOrdinal("content_base_uom_id")),
            ContentCostAvg: GetDecimal(r, "cost_avg"),
            BalanceQty: balance,
            ReservedQty: reserved,
            AvailableQty: balance - reserved,
            ImageUrl: mediaId is null ? null : $"/api/v1/media/{mediaId}/content/thumb",
            WeightPrecisionG: r["weight_precision_g"] is DBNull ? null : r.GetInt32(r.GetOrdinal("weight_precision_g")),
            WeightStepG: r["weight_step_g"] is DBNull ? null : r.GetInt32(r.GetOrdinal("weight_step_g")),
            AllowManualWeight: r["allow_manual_weight"] is DBNull ? null : (bool)r["allow_manual_weight"],
            AllowAmountTarget: r["allow_amount_target"] is DBNull ? null : (bool)r["allow_amount_target"],
            MinWeightKg: r["min_weight_kg"] is DBNull ? null : GetDecimal(r, "min_weight_kg"),
            MaxWeightKg: r["max_weight_kg"] is DBNull ? null : GetDecimal(r, "max_weight_kg"),
            DefaultTareKg: GetDecimal(r, "default_tare_kg"));
    }

    public async Task<IReadOnlyList<VwProfileData>> GetActiveVwProfilesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT profile_id, name, prefix, total_length, plu_start_pos, plu_length,
                   value_type, value_start_pos, value_length, value_decimals, value_unit_id, check_digit_rule
              FROM catalog.vw_barcode_profiles WHERE is_active
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<VwProfileData>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new VwProfileData(
                r.GetInt64(r.GetOrdinal("profile_id")), (string)r["name"], (string)r["prefix"],
                r.GetInt32(r.GetOrdinal("total_length")), r.GetInt32(r.GetOrdinal("plu_start_pos")), r.GetInt32(r.GetOrdinal("plu_length")),
                (string)r["value_type"], r.GetInt32(r.GetOrdinal("value_start_pos")), r.GetInt32(r.GetOrdinal("value_length")),
                r.GetInt32(r.GetOrdinal("value_decimals")),
                r["value_unit_id"] is DBNull ? null : r.GetInt64(r.GetOrdinal("value_unit_id")),
                (string)r["check_digit_rule"]));
        }
        return list;
    }

    public async Task<StationReceiptPrinterData?> GetStationReceiptPrinterAsync(long stationId, CancellationToken ct = default) =>
        await (from role in _db.PrinterDocumentRoles.AsNoTracking()
               join prn in _db.PrinterProfiles.AsNoTracking() on role.PrinterProfileId equals prn.PrinterProfileId
               where role.StationId == stationId && role.DocumentRole == "CUSTOMER_RECEIPT" && prn.IsActive
               select new StationReceiptPrinterData(
                   prn.PrinterProfileId, prn.ProfileVersion, prn.MediaClass, prn.PrintableWidthMm,
                   prn.DpiX, prn.VerifiedMediaWidthMm, prn.IsCalibrated, role.IsAutoRoutingEnabled))
              .FirstOrDefaultAsync(ct);

    // ═══════════════ الالتقاط والبحث ═══════════════════════════════════════════

    public async Task<long?> FindSkuIdByBarcodeAsync(string barcodeNormalized, CancellationToken ct = default) =>
        await _db.Barcodes.AsNoTracking()
            .Where(b => b.BarcodeNormalized == barcodeNormalized && b.Status == "ACTIVE")
            .Select(b => (long?)b.SkuId)
            .FirstOrDefaultAsync(ct);

    public async Task<long?> FindSkuIdBySkuCodeAsync(string skuCode, CancellationToken ct = default) =>
        await _db.Skus.AsNoTracking()
            .Where(s => s.SkuCode == skuCode && s.Status == "ACTIVE")
            .Select(s => (long?)s.SkuId)
            .FirstOrDefaultAsync(ct);

    public async Task<long?> FindSkuIdByPluAsync(string pluCode, CancellationToken ct = default) =>
        await (from plu in _db.Plus.AsNoTracking()
               join sku in _db.Skus.AsNoTracking() on plu.SkuId equals sku.SkuId
               where plu.PluCode == pluCode && plu.IsActive && sku.Status == "ACTIVE"
               select (long?)plu.SkuId)
              .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SaleItemData>> SearchSaleItemsAsync(
        string query, int page, int pageSize, long warehouseId, CancellationToken ct = default)
    {
        var (items, _) = await SearchSaleItemsPagedAsync(query, page, pageSize, warehouseId, ct);
        return items;
    }

    public async Task<(IReadOnlyList<SaleItemData> Items, long TotalCount)> SearchSaleItemsPagedAsync(
        string query, int page, int pageSize, long warehouseId, CancellationToken ct = default)
    {
        const string sql = """
            WITH matches AS (
                SELECT s.sku_id
                  FROM catalog.skus s
                  JOIN catalog.products p ON p.product_id = s.product_id
                 WHERE s.status = 'ACTIVE'
                   AND (@q = '' OR p.name_ar ILIKE '%' || @q || '%' OR s.sku_code ILIKE '%' || @q || '%')
                 ORDER BY s.sku_id
                 LIMIT 200
            )
            SELECT (SELECT COUNT(*) FROM matches) AS total, m.sku_id FROM matches m ORDER BY m.sku_id LIMIT @lim OFFSET @off
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        var skuIds = new List<long>();
        long total = 0;
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("q", query);
            cmd.Parameters.AddWithValue("lim", pageSize);
            cmd.Parameters.AddWithValue("off", (page - 1) * pageSize);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(r.GetOrdinal("total"));
                skuIds.Add(r.GetInt64(r.GetOrdinal("sku_id")));
            }
        }
        var items = new List<SaleItemData>();
        foreach (var skuId in skuIds)
        {
            var item = await GetSaleItemDataAsync(skuId, warehouseId, null, ct);
            if (item is not null && item.UnitPrice > 0)
            {
                items.Add(item);
            }
        }
        return (items, total);
    }

    // ═══════════════ إتمام البيع — المعاملة الذرية الكاملة (§9.7) ═════════════

    public async Task<PostedSaleResult> PostSaleAsync(SalePostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // 0) Idempotency: مفتاح مستخدم داخل المعاملة يعيد الأصل (لا بيع مزدوج §15.4)
                if (!string.IsNullOrWhiteSpace(plan.IdempotencyKey))
                {
                    var existing = await _db.SalesInvoices.AsNoTracking()
                        .Where(i => i.IdempotencyKey == plan.IdempotencyKey)
                        .FirstOrDefaultAsync(ct);
                    if (existing is not null)
                    {
                        return new PostedSaleResult(
                            existing.InvoiceId, existing.InvoiceNumber, [], [], [], null,
                            new PrintOutcome(false, null, null, null, "IDEMPOTENT_REPLAY",
                                "فاتورة بهذا المفتاح موجودة سابقاً — أعيدت كما هي"),
                            IdempotentReplay: true);
                    }
                }

                var now = _clock.GetUtcNow();

                // 1) الوردية (إن أُرسلت): مفتوحة فعلاً — لا ترحيل على وردية مغلقة
                if (plan.ShiftId is { } shiftId)
                {
                    var shift = await _db.SalesShifts
                        .FromSql($"SELECT * FROM sales.shifts WHERE shift_id = {shiftId} FOR UPDATE")
                        .FirstAsync(ct);
                    if (shift.Status != "OPEN")
                    {
                        throw new SalesPolicyException("SHIFT_NOT_OPEN", "الوردية مغلقة — لا ترحيل فواتير عليها");
                    }
                }

                // 2) اعتماد السالب D-03 عند الحاجة: APPROVED وقرار ≠ طالب (قيد V003 approvals_no_self)
                long? negativeApprovalId = null;
                if (plan.NegativeApprovalId is { } approvalId)
                {
                    var approval = await _db.Approvals
                        .FromSql($"SELECT * FROM core.approvals WHERE approval_id = {approvalId} FOR UPDATE")
                        .FirstAsync(ct);
                    if (approval.Decision != "APPROVED")
                    {
                        throw new SalesPolicyException("APPROVAL_NOT_APPROVED", "اعتماد البيع السالب غير موافق عليه");
                    }
                    // فصل الواجبات يحرسه قيد V001 approvals_no_self (المعتمد ≠ الطالب)
                    negativeApprovalId = approval.ApprovalId;
                }

                // 3) رأس الفاتورة DRAFT + رقم من تسلسل V018
                var invoice = new SalesInvoiceRow
                {
                    InvoiceNumber = await NextNumberAsync("sales.invoice_number_seq", "SI-", ct),
                    BranchId = plan.BranchId,
                    StationId = plan.StationId,
                    ShiftId = plan.ShiftId,
                    CashierUserId = plan.CashierUserId,
                    CustomerId = plan.CustomerId,
                    Status = "DRAFT",
                    Currency = plan.Currency,
                    TaxModeSnapshot = plan.TaxMode,
                    GrossTotal = plan.GrossTotal,
                    LineDiscountTotal = plan.LineDiscountTotal,
                    InvoiceDiscount = plan.InvoiceDiscount,
                    TaxTotal = plan.TaxTotal,
                    NetTotal = plan.NetTotal,
                    PaidTotal = plan.PaidTotal,
                    IdempotencyKey = plan.IdempotencyKey,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.SalesInvoices.Add(invoice);
                await _db.SaveChangesAsync(ct);

                // 4) السطور بلقطاتها الكاملة + حركة SALE واحدة لكل سطر على صنف المحتوى (§4.3 ق5)
                var movementIds = new List<long>();
                var paymentIds = new List<long>();
                var treasuryIds = new List<long>();
                var warehouseId = await GetPosWarehouseIdAsync(plan.BranchId, ct)
                    ?? throw new SalesPolicyException("POS_WAREHOUSE_NOT_FOUND", "لا مخزن بيع للفرع");

                foreach (var line in plan.Lines)
                {
                    var lineRow = new SalesInvoiceLineRow
                    {
                        InvoiceId = invoice.InvoiceId,
                        LineNo = line.LineNo,
                        SkuId = line.SkuId,
                        SaleMode = line.SaleMode,
                        PackageId = line.PackageId,
                        Qty = decimal.Round(line.Qty, 6),
                        UomId = line.UomId,
                        ConversionFactor = line.ConversionFactor,
                        WeightKg = line.WeightKg,
                        WeightSource = line.WeightSource,
                        AmountTarget = line.AmountTarget,
                        TareKg = line.TareKg,
                        UnitPrice = line.UnitPrice,
                        PriceListId = line.PriceListId,
                        TaxTreatment = line.TaxTreatment,
                        TaxRateSnapshot = line.TaxRate,
                        LineDiscount = line.LineDiscount,
                        InvoiceDiscountShare = line.InvoiceDiscountShare,
                        TaxAmount = line.TaxAmount,
                        NetAmount = line.NetAmount,
                        CostSnapshot = line.CostSnapshot,
                        CostMethodSnapshot = line.CostMethod,
                        Notes = line.Notes,
                    };
                    _db.SalesInvoiceLines.Add(lineRow);
                    await _db.SaveChangesAsync(ct);

                    // حركة الدفتر: SALE على صنف الأثر (المحتوى عند PACK) — قيد V003/السياسة يفرض السالب
                    var movement = BuildMovement(
                        line.MovementSkuId, warehouseId, "SALE", -1, line.BaseQty,
                        line.UomId, line.ConversionFactor, "sales.invoice", invoice.InvoiceId,
                        lineRow.LineId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        negativeApprovalId, $"بيع {invoice.InvoiceNumber}",
                        plan.BranchId, plan.CashierUserId, now);
                    _db.StockMovements.Add(movement);
                    await _db.SaveChangesAsync(ct);
                    movementIds.Add(movement.MovementId);

                    // طبقة WAC: صرف من الطبقة (المتوسط لا يتغير §15.3) — الأثر مرتبط بمخزن البيع
                    await ApplyWacIssueAsync(line.MovementSkuId, warehouseId, line.BaseQty, ct);
                }

                // 5) المدفوعات + آثار الخزينة (أثر واحد لكل دفعة §15.4)
                foreach (var payment in plan.Payments)
                {
                    var paymentRow = new SalesPaymentRow
                    {
                        InvoiceId = invoice.InvoiceId,
                        PaymentMethod = payment.Method,
                        Amount = payment.Amount,
                        Status = payment.Status,
                        ProviderName = payment.ProviderName,
                        ProviderReference = payment.ProviderReference,
                        TreasuryAccountId = payment.TreasuryAccountId,
                        ReceivedAt = now,
                        CreatedBy = plan.CashierUserId,
                    };
                    _db.SalesPayments.Add(paymentRow);
                    await _db.SaveChangesAsync(ct);
                    paymentIds.Add(paymentRow.PaymentId);

                    if (payment.TreasuryAccountId is { } accountId)
                    {
                        var reason = payment.Method == PaymentMethods.Cash && plan.CashTendered is { } tendered
                            ? $"تحصيل نقدي فاتورة {invoice.InvoiceNumber} (مسلم {tendered:0.00})"
                            : $"تحصيل {payment.Method} فاتورة {invoice.InvoiceNumber}";
                        var txn = new TreasuryTxnRow
                        {
                            TreasuryAccountId = accountId,
                            Direction = 1,
                            Amount = payment.Amount,
                            SourceDocType = "sales.payment",
                            SourceDocId = paymentRow.PaymentId,
                            Reason = reason,
                            OccurredAt = now,
                            CreatedBy = plan.CashierUserId,
                            BranchId = plan.BranchId,
                            ShiftId = plan.ShiftId,   // G8/V019: سياق الوردية — مطابقة الدرج من الدفتر
                        };
                        _db.TreasuryTransactions.Add(txn);
                        await _db.SaveChangesAsync(ct);
                        treasuryIds.Add(txn.TxnId);
                    }
                }

                // 6) حدث درج للبيع النقدي — B-01 صادق: أمر موثق بلا استجابة جهاز (UNKNOWN)
                long? drawerEventId = null;
                if (plan.Payments.Any(p => p.Method is PaymentMethods.Cash or PaymentMethods.SplitCash))
                {
                    var drawerEvent = new DrawerEventRow
                    {
                        StationId = plan.StationId,
                        ShiftId = plan.ShiftId,
                        EventType = "OPENED_BY_SALE",
                        Reason = $"فتح بالبيع {invoice.InvoiceNumber}",
                        RequestedAt = now,
                        RespondedAt = null,          // لا جهاز درج مختبراً بعد (G10) — لا ندّعي استجابة
                        ResultStatus = "UNKNOWN",
                        TriggeredBy = plan.CashierUserId,
                    };
                    _db.DrawerEvents.Add(drawerEvent);
                    await _db.SaveChangesAsync(ct);
                    drawerEventId = drawerEvent.EventId;
                }

                // 7) الترحيل DRAFT→POSTED — مشغل V005 يفحص كل شيء (سطور/مجاميع/تكلفة/وزن/مدفوع)
                invoice.Status = "POSTED";
                invoice.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);

                // 8) مهمة إيصال العميل التلقائية (§29.4 AUTO_AFTER_POST) — خارج المعاملة المالية:
                //    فشلها/تعليقها لا يمس البيع (اختبار 11)، ومفتاحها فريد لكل فاتورة/نسخة
                var receipt = await CreateReceiptJobInternalAsync(
                    invoice.InvoiceId, plan.StationId, plan.CashierUserId, "CUSTOMER", null, ct);

                return new PostedSaleResult(
                    invoice.InvoiceId, invoice.InvoiceNumber,
                    [.. movementIds], [.. treasuryIds], [.. paymentIds], drawerEventId,
                    receipt, IdempotentReplay: false);
            });
        }
        catch (SalesPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Translate(pg);
        }
    }

    /// <summary>صرف من طبقة WAC بقفل الصف — المتوسط لا يتغير والكمية تنقص (§15.3).</summary>
    private async Task ApplyWacIssueAsync(long skuId, long warehouseId, decimal baseQty, CancellationToken ct)
    {
        var layer = await _db.CostLayers
            .FromSql($"SELECT * FROM finance.cost_layers WHERE sku_id = {skuId} AND warehouse_id = {warehouseId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (layer is null)
        {
            return; // لا طبقة = لا تكلفة موثقة (لقطة 0) — التقييم يعرض ما يملك فقط (نمط G6)
        }
        var updated = WacMath.ApplyIssue(new WacState(layer.QtyOnLayer, layer.AvgUnitCost), baseQty);
        layer.QtyOnLayer = updated.QtyOnLayer;
        layer.AvgUnitCost = updated.AvgUnitCost;
        layer.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>إرجاع مرتجع إلى طبقة WAC — الكمية تعود والمتوسط يبقى (سياسة D-02 أ).</summary>
    private async Task ApplyWacReturnAsync(long skuId, long warehouseId, decimal baseQty, CancellationToken ct)
    {
        var layer = await _db.CostLayers
            .FromSql($"SELECT * FROM finance.cost_layers WHERE sku_id = {skuId} AND warehouse_id = {warehouseId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (layer is null)
        {
            return;
        }
        var updated = WacMath.ApplyReturn(new WacState(layer.QtyOnLayer, layer.AvgUnitCost), baseQty);
        layer.QtyOnLayer = updated.QtyOnLayer;
        layer.AvgUnitCost = updated.AvgUnitCost;
        layer.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    internal StockMovementRow BuildMovement(
        long skuId, long warehouseId, string movementType, int direction, decimal baseQty,
        long sourceUomId, decimal conversionFactor, string sourceDocType, long sourceDocId,
        string? sourceLineRef, long? approvalId, string? reason, long branchId, long userId,
        DateTimeOffset? occurredAt = null) => new()
    {
        SkuId = skuId,
        WarehouseId = warehouseId,
        MovementType = movementType,
        Direction = (short)direction,
        BaseQty = decimal.Round(baseQty, 6),
        SourceUomId = sourceUomId,
        ConversionFactor = conversionFactor,
        SourceDocType = sourceDocType,
        SourceDocId = sourceDocId,
        SourceLineRef = sourceLineRef,
        ApprovalId = approvalId,
        Reason = reason,
        BranchId = branchId,
        UserId = userId,
        OccurredAt = occurredAt ?? _clock.GetUtcNow(),
    };

    // ═══════════════ المرتجع — اعتماد إلزامي وأثر واحد مرتبط بالأصل (§9.8/D-04) ═

    public async Task<PostedRefundResult> PostRefundAsync(RefundPostPlan plan, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                if (!string.IsNullOrWhiteSpace(plan.IdempotencyKey))
                {
                    var existing = await _db.SalesRefunds.AsNoTracking()
                        .Where(r => r.IdempotencyKey == plan.IdempotencyKey)
                        .FirstOrDefaultAsync(ct);
                    if (existing is not null)
                    {
                        return new PostedRefundResult(
                            existing.RefundId, existing.RefundNumber, [], [], "PARTIAL_RETURN", IdempotentReplay: true);
                    }
                }

                var now = _clock.GetUtcNow();

                // 1) الفاتورة الأصلية بقفل: POSTED/PARTIAL_RETURN فقط
                var invoice = await _db.SalesInvoices
                    .FromSql($"SELECT * FROM sales.invoices WHERE invoice_id = {plan.OriginalInvoiceId} FOR UPDATE")
                    .FirstAsync(ct);
                if (invoice.Status is not ("POSTED" or "PARTIAL_RETURN"))
                {
                    throw new SalesPolicyException("INVOICE_NOT_REFUNDABLE",
                        $"الفاتورة {invoice.InvoiceNumber} في حالة {invoice.Status} — لا مرتجع عليها");
                }

                // 2) الاعتماد: APPROVED وdecided_by ≠ requested_by (D-04 + قيد V001/V005)
                var approval = await _db.Approvals
                    .FromSql($"SELECT * FROM core.approvals WHERE approval_id = {plan.ApprovalId} FOR UPDATE")
                    .FirstAsync(ct);
                if (approval.Decision != "APPROVED")
                {
                    throw new SalesPolicyException("REFUND_APPROVAL_NOT_APPROVED", "اعتماد المرتجع غير موافق عليه");
                }
                // فصل الواجبات يحرسه قيد V001 approvals_no_self (المعتمد ≠ الطالب) — الحارس النهائي

                // 3) مستند المرتجع + سطوره (مشغل V005 يمنع تجاوز المباع)
                var refund = new SalesRefundRow
                {
                    RefundNumber = await NextNumberAsync("sales.refund_number_seq", "SR-", ct),
                    OriginalInvoiceId = invoice.InvoiceId,
                    BranchId = plan.BranchId,
                    ApprovalId = approval.ApprovalId,
                    Status = "DRAFT",
                    RefundMethod = plan.RefundMethod,
                    Restock = plan.Restock,
                    Total = plan.Total,
                    Reason = plan.ReasonAr,
                    PostedAt = now,
                    CreatedBy = plan.CreatedByUserId,
                    CreatedAt = now,
                    IdempotencyKey = plan.IdempotencyKey,
                };
                _db.SalesRefunds.Add(refund);
                await _db.SaveChangesAsync(ct);

                var movementIds = new List<long>();
                var treasuryIds = new List<long>();
                var warehouseId = await GetPosWarehouseIdAsync(plan.BranchId, ct)
                    ?? throw new SalesPolicyException("POS_WAREHOUSE_NOT_FOUND", "لا مخزن للفرع");

                var fullyReturned = true;
                foreach (var line in plan.Lines)
                {
                    _db.SalesRefundLines.Add(new SalesRefundLineRow
                    {
                        RefundId = refund.RefundId,
                        OriginalLineId = line.OriginalLineId,
                        Qty = decimal.Round(line.Qty, 6),
                        UnitRefund = line.UnitRefund,
                        LineTotal = line.LineTotal,
                    });
                    await _db.SaveChangesAsync(ct);

                    var originalLine = await _db.SalesInvoiceLines.AsNoTracking()
                        .FirstAsync(l => l.LineId == line.OriginalLineId, ct);

                    // المردود المُرحّل سابقاً (هذا المرتجع لا يزال DRAFT فلا يدخل المجموع)
                    var returnedBefore = await _db.SalesRefundLines.AsNoTracking()
                        .Where(rl => rl.OriginalLineId == line.OriginalLineId)
                        .Join(_db.SalesRefunds.AsNoTracking().Where(r => r.Status == "POSTED"),
                              rl => rl.RefundId, r => r.RefundId, (rl, r) => rl.Qty)
                        .SumAsync(q => q, ct);
                    if (Math.Abs(returnedBefore + line.Qty - originalLine.Qty) > 0.0000001m)
                    {
                        fullyReturned = false;
                    }

                    // 4) أثر المخزون: SALE_RETURN فقط عند إعادة التخزين (الهالك لا يعود رصيداً)
                    if (plan.Restock)
                    {
                        var movement = BuildMovement(
                            originalLine.SkuId, warehouseId, "SALE_RETURN", 1, line.Qty * originalLine.ConversionFactor,
                            originalLine.UomId, originalLine.ConversionFactor,
                            "sales.refund", refund.RefundId,
                            line.OriginalLineId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            approval.ApprovalId, $"مرتجع {refund.RefundNumber} على {invoice.InvoiceNumber}",
                            plan.BranchId, plan.CreatedByUserId, now);
                        _db.StockMovements.Add(movement);
                        await _db.SaveChangesAsync(ct);
                        movementIds.Add(movement.MovementId);
                        await ApplyWacReturnAsync(originalLine.SkuId, warehouseId, line.Qty * originalLine.ConversionFactor, ct);
                    }
                }

                // 5) أثر الخزينة: رد نقدي بفتح درج الفرع — البطاقة للبطاقة بلا نقد (D-04)
                if (plan.RefundMethod == Domain.Sales.RefundMethods.Cash)
                {
                    var drawer = await GetDrawerAccountAsync(plan.BranchId, plan.StationId, ct);
                    if (drawer is null)
                    {
                        throw new SalesPolicyException("DRAWER_ACCOUNT_NOT_FOUND", "لا حساب درج نقد للفرع — لا رد نقدي بلا خزينة موثقة");
                    }
                    var txn = new TreasuryTxnRow
                    {
                        TreasuryAccountId = drawer.TreasuryAccountId,
                        Direction = -1,
                        Amount = plan.Total,
                        SourceDocType = "sales.refund",
                        SourceDocId = refund.RefundId,
                        Reason = $"رد نقدي مرتجع {refund.RefundNumber} على {invoice.InvoiceNumber}",
                        OccurredAt = now,
                        CreatedBy = plan.CreatedByUserId,
                        BranchId = plan.BranchId,
                        ShiftId = plan.ShiftId,   // G8/V019: وردية الرد الفعلي — النقد خرج من درجها
                    };
                    _db.TreasuryTransactions.Add(txn);
                    await _db.SaveChangesAsync(ct);
                    treasuryIds.Add(txn.TxnId);
                }

                // 6) ترحيل المرتجع وأثر الفاتورة (الأرقام لا تتغير — مشغل V005)
                refund.Status = "POSTED";
                await _db.SaveChangesAsync(ct);

                invoice.ReturnedTotal += plan.Total;
                invoice.Status = fullyReturned ? "FULL_RETURN" : "PARTIAL_RETURN";
                invoice.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);
                return new PostedRefundResult(
                    refund.RefundId, refund.RefundNumber,
                    [.. movementIds], [.. treasuryIds],
                    invoice.Status, IdempotentReplay: false);
            });
        }
        catch (SalesPolicyException)
        {
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw Translate(pg);
        }
    }

    public async Task<Result<long>> VoidDraftInvoiceAsync(long invoiceId, long userId, string reasonAr, CancellationToken ct = default)
    {
        var invoice = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct);
        if (invoice is null)
        {
            return Result.Fail<long>(new("INVOICE_NOT_FOUND", "الفاتورة غير موجودة"));
        }
        if (invoice.Status == "POSTED")
        {
            return Result.Fail<long>(new("VOID_POSTED_USE_REFUND",
                "الفاتورة مرحلة — عكسها بمرتجع موثق (D-04) لا بإلغاء صامت (§9.2)"));
        }
        if (invoice.Status != "DRAFT")
        {
            return Result.Fail<long>(new("INVOICE_NOT_DRAFT", $"الفاتورة في حالة {invoice.Status}"));
        }
        invoice.Status = "VOID";
        invoice.VoidedAt = _clock.GetUtcNow();
        invoice.VoidReason = reasonAr;
        invoice.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Result.Ok(invoiceId);
    }

    // ═══════════════ السلال المعلقة (§9.5) ═════════════════════════════════════

    public async Task<long> HoldCartAsync(HoldCartPlan plan, CancellationToken ct = default)
    {
        var row = new HeldCartRow
        {
            StationId = plan.StationId,
            HeldBy = plan.HeldByUserId,
            CustomerId = plan.CustomerId,
            CartSnapshot = plan.CartJson,
            NoteAr = plan.NoteAr,
            HeldAt = _clock.GetUtcNow(),
        };
        _db.HeldCarts.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.CartId;
    }

    public async Task<IReadOnlyList<HeldCartInfo>> ListHeldCartsAsync(long stationId, bool openOnly, CancellationToken ct = default) =>
        await (from c in _db.HeldCarts.AsNoTracking()
               join u in _db.Users.AsNoTracking() on c.HeldBy equals u.UserId
               where c.StationId == stationId && (!openOnly || c.RestoredAt == null)
               orderby c.HeldAt descending
               select new HeldCartInfo(c.CartId, c.HeldAt, u.DisplayNameAr, c.NoteAr, c.CustomerId, c.RestoredAt != null))
              .ToListAsync(ct);

    public async Task<RestoredCart?> RestoreCartAsync(long cartId, long restoredByUserId, CancellationToken ct = default)
    {
        var row = await _db.HeldCarts
            .FromSql($"SELECT * FROM sales.held_carts WHERE cart_id = {cartId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (row is null || row.RestoredAt is not null)
        {
            return null;
        }
        row.RestoredAt = _clock.GetUtcNow();
        row.RestoredBy = restoredByUserId;
        await _db.SaveChangesAsync(ct);
        return new RestoredCart(row.CartId, row.CartSnapshot, row.CustomerId);
    }

    // ═══════════════ الورديات (§9.9) والدرج ═════════════════════════════════════

    public async Task<Result<ShiftInfo>> OpenShiftAsync(long stationId, long userId, decimal openingFloat, CancellationToken ct = default)
    {
        try
        {
            var open = await _db.SalesShifts.AsNoTracking()
                .AnyAsync(s => s.StationId == stationId && s.Status == "OPEN", ct);
            if (open)
            {
                return Result.Fail<ShiftInfo>(new("SHIFT_ALREADY_OPEN",
                    "وردية مفتوحة لهذه المحطة — وردية واحدة في أي لحظة (قيد V005)"));
            }
            var shiftNumber = (int)(await _db.SalesShifts.AsNoTracking()
                .Where(s => s.StationId == stationId).CountAsync(ct)) + 1;
            var row = new SalesShiftRow
            {
                StationId = stationId,
                OpenedBy = userId,
                Status = "OPEN",
                OpeningFloat = openingFloat,
                OpenedAt = _clock.GetUtcNow(),
            };
            _db.SalesShifts.Add(row);
            await _db.SaveChangesAsync(ct);
            var opener = await _db.Users.AsNoTracking().FirstAsync(u => u.UserId == userId, ct);
            return Result.Ok(new ShiftInfo(
                row.ShiftId, shiftNumber, stationId, opener.DisplayNameAr,
                row.OpenedAt, openingFloat, "OPEN"));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            return Result.Fail<ShiftInfo>(new("SHIFT_ALREADY_OPEN", "وردية مفتوحة لهذه المحطة (سباق فتح مزدوج)"));
        }
    }

    public async Task<ShiftInfo?> GetCurrentShiftAsync(long stationId, CancellationToken ct = default) =>
        await (from s in _db.SalesShifts.AsNoTracking()
               join u in _db.Users.AsNoTracking() on s.OpenedBy equals u.UserId
               where s.StationId == stationId && s.Status == "OPEN"
               orderby s.OpenedAt descending
               select new ShiftInfo(
                   s.ShiftId, 0, s.StationId, u.DisplayNameAr, s.OpenedAt, s.OpeningFloat, s.Status))
              .FirstOrDefaultAsync(ct);

    public async Task<ShiftSummaryData?> GetShiftSummaryAsync(long shiftId, CancellationToken ct = default)
    {
        var shiftInfo = await GetCurrentShiftOrClosedAsync(shiftId, ct);
        if (shiftInfo is null)
        {
            return null;
        }
        var shift = shiftInfo;
        const string sql = """
            SELECT
              (SELECT COUNT(*) FROM sales.invoices WHERE shift_id = @sid AND status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')) AS invoice_count,
              COALESCE((SELECT SUM(l.net_amount) FROM sales.invoice_lines l
                  JOIN sales.invoices i ON i.invoice_id = l.invoice_id
                 WHERE i.shift_id = @sid AND i.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')), 0) AS net_sales,
              COALESCE((SELECT SUM(p.amount) FROM sales.payments p
                  JOIN sales.invoices i2 ON i2.invoice_id = p.invoice_id
                 WHERE i2.shift_id = @sid AND i2.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
                   AND p.payment_method IN ('CASH','SPLIT_CASH') AND p.status IN ('CAPTURED','SETTLED')), 0) AS cash_collected,
              COALESCE((SELECT SUM(p.amount) FROM sales.payments p
                  JOIN sales.invoices i3 ON i3.invoice_id = p.invoice_id
                 WHERE i3.shift_id = @sid AND i3.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
                   AND p.payment_method IN ('CARD','SPLIT_CARD') AND p.status IN ('CAPTURED','SETTLED')), 0) AS card_collected,
              COALESCE((SELECT SUM(p.amount) FROM sales.payments p
                  JOIN sales.invoices i4 ON i4.invoice_id = p.invoice_id
                 WHERE i4.shift_id = @sid AND i4.status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN')
                   AND p.payment_method = 'CREDIT' AND p.status IN ('CAPTURED','SETTLED')), 0) AS credit_issued,
              COALESCE((SELECT SUM(r.total) FROM sales.refunds r
                  JOIN sales.invoices i5 ON i5.invoice_id = r.original_invoice_id
                 WHERE i5.shift_id = @sid AND r.status = 'POSTED' AND r.refund_method = 'CASH'), 0) AS cash_refunded,
              COALESCE((SELECT SUM(r.total) FROM sales.refunds r
                  JOIN sales.invoices i6 ON i6.invoice_id = r.original_invoice_id
                 WHERE i6.shift_id = @sid AND r.status = 'POSTED'), 0) AS returns_total
            """;
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sid", shiftId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        decimal cashCollected = GetDecimal(r, "cash_collected");
        decimal cashRefunded = GetDecimal(r, "cash_refunded");
        return new ShiftSummaryData(
            shift,
            r.GetInt32(r.GetOrdinal("invoice_count")),
            GetDecimal(r, "net_sales"),
            cashCollected,
            GetDecimal(r, "card_collected"),
            GetDecimal(r, "credit_issued"),
            cashRefunded,
            GetDecimal(r, "returns_total"),
            ExpectedCashDrawer: shift.OpeningFloat + cashCollected - cashRefunded);
    }

    private async Task<ShiftInfo?> GetCurrentShiftOrClosedAsync(long shiftId, CancellationToken ct) =>
        await (from s in _db.SalesShifts.AsNoTracking()
               join u in _db.Users.AsNoTracking() on s.OpenedBy equals u.UserId
               where s.ShiftId == shiftId
               select new ShiftInfo(
                   s.ShiftId, 0, s.StationId, u.DisplayNameAr, s.OpenedAt, s.OpeningFloat, s.Status))
              .FirstOrDefaultAsync(ct);

    public async Task<DrawerEventData> RecordDrawerEventAsync(
        long stationId, long? shiftId, long userId, string eventType, string? reasonAr, CancellationToken ct = default)
    {
        var row = new DrawerEventRow
        {
            StationId = stationId,
            ShiftId = shiftId,
            EventType = eventType,
            Reason = reasonAr,
            RequestedAt = _clock.GetUtcNow(),
            RespondedAt = null,          // لا جهاز درج مختبراً (G10/B-01) — لا ندّعي استجابة
            ResultStatus = "UNKNOWN",
            TriggeredBy = userId,
        };
        _db.DrawerEvents.Add(row);
        await _db.SaveChangesAsync(ct);
        return new DrawerEventData(row.EventId, row.EventType, row.RequestedAt, row.RespondedAt, row.ResultStatus);
    }

    // ═══════════════ الطباعة (§29.4/§29.9) ═════════════════════════════════════

    public async Task<PrintOutcome> CreateReceiptJobAsync(
        long invoiceId, long stationId, long userId, string copyType, long? reprintOfJobId, CancellationToken ct = default) =>
        await CreateReceiptJobInternalAsync(invoiceId, stationId, userId, copyType, reprintOfJobId, ct);

    private async Task<PrintOutcome> CreateReceiptJobInternalAsync(
        long invoiceId, long stationId, long userId, string copyType, long? reprintOfJobId, CancellationToken ct)
    {
        var printer = await GetStationReceiptPrinterAsync(stationId, ct);
        var routing = ReceiptRouting.Resolve(printer is null ? null
            : new StationReceiptPrinter(
                printer.PrinterProfileId, printer.MediaClass, printer.PrintableWidthMm,
                printer.VerifiedMediaWidthMm, printer.IsCalibrated, printer.IsAutoRoutingEnabled));

        // لا طابعة معينة أصلاً: لا مهمة — إيصال محفوظ وإشعار صادق (لا فشل صامت §9.1E)
        if (printer is null)
        {
            return new PrintOutcome(
                false, null, null, null, "HOLD_PRINTER_NOT_ASSIGNED",
                "الإيصال محفوظ مع الفاتورة — لا طابعة معتمدة لدور إيصال العميل لهذه المحطة بعد (تعيين المسؤول في G10)");
        }

        // §29.4: مهمة تنشأ بوسيط الطابعة المعلن؛ غير المعاير/المعطل توجيهه → HELD (لا تخمين ولا A4)
        var mediaClass = routing.IsSuccess ? routing.Value!.ResolvedMediaClass : printer.MediaClass;
        var template = await _db.DocumentTemplates.AsNoTracking()
            .Where(t => t.DocumentType == "CUSTOMER_RECEIPT" && t.MediaClass == mediaClass && t.IsActive)
            .OrderByDescending(t => t.Version)
            .FirstAsync(ct);

        var status = routing.IsSuccess ? "QUEUED" : "HELD_CONFIGURATION_MISMATCH";
        var job = new PrintJobRow
        {
            DocumentId = invoiceId,
            DocumentType = "CUSTOMER_RECEIPT",
            TemplateId = template.TemplateId,
            TemplateVersion = template.Version,
            PrinterProfileId = printer.PrinterProfileId,
            PrinterProfileVersion = printer.PrinterProfileVersion,
            MediaClass = mediaClass,
            PrintableWidthMm = printer.PrintableWidthMm,
            Dpi = printer.Dpi,
            CopyType = copyType,
            Status = status,
            AttemptCount = 0,
            MaxAttempts = 3,
            FailureReason = routing.IsSuccess ? null : routing.Error!.MessageAr,
            IdempotencyKey = $"receipt:{invoiceId}:{copyType}",
            RequestedBy = userId,
            RelatedReprintOf = reprintOfJobId,
            CreatedAt = _clock.GetUtcNow(),
        };
        try
        {
            _db.PrintJobs.Add(job);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            // مهمة بنفس المفتاح موجودة — إعادة Idempotency الصادقة (لا مهمة مزدوجة §29.9)
            // فك تتبع الكيان الفاشل حتى لا يعيد AuditWriter حفظه لاحقاً في نفس الطلب
            _db.Entry(job).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            var existing = await _db.PrintJobs.AsNoTracking()
                .FirstAsync(j => j.IdempotencyKey == job.IdempotencyKey, ct);
            return new PrintOutcome(
                true, existing.JobId, existing.Status, existing.MediaClass, null,
                "مهمة إيصال بهذا المفتاح موجودة — أعيدت كما هي (لا تكرار طباعي)");
        }

        return new PrintOutcome(
            true, job.JobId, job.Status, job.MediaClass,
            routing.IsSuccess ? null : routing.Error!.Code,
            routing.IsSuccess
                ? $"أُنشئت مهمة إيصال العميل على طابعة المحطة ({mediaClass}) — الرندر الفعلي في G10 (B-07)"
                : $"مهمة الإيصال معلقة HELD: {routing.Error!.MessageAr} — البيع محفوظ ولا يتأثر (§29.9)");
    }

    public async Task<PrintJobInfo?> GetPrintJobAsync(long jobId, CancellationToken ct = default) =>
        await _db.PrintJobs.AsNoTracking()
            .Where(j => j.JobId == jobId)
            .Select(j => new PrintJobInfo(
                j.JobId, j.DocumentId, j.DocumentType, j.MediaClass, j.CopyType, j.Status,
                j.AttemptCount, j.FailureReason, j.RelatedReprintOf, j.CreatedAt))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PrintJobInfo>> ListPrintJobsAsync(long invoiceId, CancellationToken ct = default) =>
        await _db.PrintJobs.AsNoTracking()
            .Where(j => j.DocumentId == invoiceId && j.DocumentType == "CUSTOMER_RECEIPT")
            .OrderBy(j => j.JobId)
            .Select(j => new PrintJobInfo(
                j.JobId, j.DocumentId, j.DocumentType, j.MediaClass, j.CopyType, j.Status,
                j.AttemptCount, j.FailureReason, j.RelatedReprintOf, j.CreatedAt))
            .ToListAsync(ct);

    // ═══════════════ عرض الفواتير ══════════════════════════════════════════════

    public async Task<RefundReplayData?> GetRefundAsync(long refundId, CancellationToken ct = default) =>
        await _db.SalesRefunds.AsNoTracking()
            .Where(r => r.RefundId == refundId)
            .Select(r => new RefundReplayData(
                r.RefundId, r.RefundNumber, r.Total, r.RefundMethod, r.Restock, "PARTIAL_RETURN"))
            .FirstOrDefaultAsync(ct);

    public async Task<SaleDetailData?> GetSaleAsync(long invoiceId, CancellationToken ct = default)
    {
        var header = await (from i in _db.SalesInvoices.AsNoTracking()
                            join u in _db.Users.AsNoTracking() on i.CashierUserId equals u.UserId
                            join c in _db.Customers.AsNoTracking() on i.CustomerId equals c.CustomerId into cust
                            from c in cust.DefaultIfEmpty()
                            where i.InvoiceId == invoiceId
                            select new
                            {
                                i.InvoiceId, i.InvoiceNumber, i.Status, i.PostedAt, i.BranchId,
                                i.CustomerId, CustomerName = c != null ? c.NameAr : null,
                                i.ShiftId, Cashier = u.DisplayNameAr,
                                i.GrossTotal, i.LineDiscountTotal, i.InvoiceDiscount,
                                i.TaxTotal, i.NetTotal, i.PaidTotal, i.ReturnedTotal,
                            }).FirstOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }

        const string linesSql = """
            SELECT l.line_id, l.line_no, l.sku_id, s.sku_code, p.name_ar, l.sale_mode,
                   l.qty, u.code AS uom_code, l.conversion_factor, l.base_qty,
                   l.weight_kg, l.weight_source, l.amount_target,
                   l.unit_price, l.line_discount, l.invoice_discount_share,
                   l.tax_amount, l.net_amount,
                   COALESCE((SELECT SUM(rl.qty) FROM sales.refund_lines rl
                              JOIN sales.refunds r ON r.refund_id = rl.refund_id
                             WHERE rl.original_line_id = l.line_id AND r.status = 'POSTED'), 0) AS refunded_qty
              FROM sales.invoice_lines l
              JOIN catalog.skus s ON s.sku_id = l.sku_id
              JOIN catalog.products p ON p.product_id = s.product_id
              JOIN catalog.units u ON u.uom_id = l.uom_id
             WHERE l.invoice_id = @inv
             ORDER BY l.line_no
            """;
        var lines = new List<SaleLineDetail>();
        await using (var conn = new NpgsqlConnection(_db.Database.GetConnectionString()))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(linesSql, conn);
            cmd.Parameters.AddWithValue("inv", invoiceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new SaleLineDetail(
                    r.GetInt64(r.GetOrdinal("line_id")), r.GetInt32(r.GetOrdinal("line_no")),
                    r.GetInt64(r.GetOrdinal("sku_id")), (string)r["sku_code"], (string)r["name_ar"],
                    (string)r["sale_mode"], GetDecimal(r, "qty"), (string)r["uom_code"],
                    GetDecimal(r, "conversion_factor"), GetDecimal(r, "base_qty"),
                    r["weight_kg"] is DBNull ? null : GetDecimal(r, "weight_kg"),
                    r["weight_source"] is DBNull ? null : (string)r["weight_source"],
                    r["amount_target"] is DBNull ? null : GetDecimal(r, "amount_target"),
                    GetDecimal(r, "unit_price"), GetDecimal(r, "line_discount"), GetDecimal(r, "invoice_discount_share"),
                    GetDecimal(r, "tax_amount"), GetDecimal(r, "net_amount"),
                    MoneyRounding.ToCent(GetDecimal(r, "net_amount") + GetDecimal(r, "tax_amount")),
                    GetDecimal(r, "refunded_qty")));
            }
        }

        var payments = await _db.SalesPayments.AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            .Select(p => new SalePaymentDetail(p.PaymentMethod, p.Amount, p.Status, p.ProviderName, p.ProviderReference))
            .ToListAsync(ct);

        return new SaleDetailData(
            header.InvoiceId, header.InvoiceNumber, header.Status, header.PostedAt,
            header.BranchId, header.CustomerId, header.CustomerName, header.ShiftId, header.Cashier,
            header.GrossTotal, header.LineDiscountTotal, header.InvoiceDiscount,
            header.TaxTotal, header.NetTotal,
            DueAmount: header.GrossTotal - header.LineDiscountTotal - header.InvoiceDiscount,
            header.PaidTotal, header.ReturnedTotal, lines, payments);
    }

    public async Task<IReadOnlyList<SaleSummaryData>> ListRecentSalesAsync(
        long stationId, long? shiftId, int limit, CancellationToken ct = default) =>
        await (from i in _db.SalesInvoices.AsNoTracking()
               join c in _db.Customers.AsNoTracking() on i.CustomerId equals c.CustomerId into cust
               from c in cust.DefaultIfEmpty()
               where i.StationId == stationId
                     && i.Status != "DRAFT"
                     && (shiftId == null || i.ShiftId == shiftId)
               orderby i.InvoiceId descending
               select new SaleSummaryData(
                   i.InvoiceId, i.InvoiceNumber, i.PostedAt ?? i.CreatedAt, i.Status,
                   i.NetTotal, i.TaxTotal, i.GrossTotal - i.LineDiscountTotal - i.InvoiceDiscount,
                   c != null ? c.NameAr : null,
                   _db.SalesInvoiceLines.AsNoTracking().Count(l => l.InvoiceId == i.InvoiceId)))
              .Take(limit)
              .ToListAsync(ct);

    // ═══════════════ مساعدات ═══════════════════════════════════════════════════

    private async Task<string> NextNumberAsync(string sequence, string prefix, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_db.Database.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT nextval('{sequence}')", conn);
        var next = await cmd.ExecuteScalarAsync(ct) is long v ? v : 0L;
        return $"{prefix}{next}";
    }

    private static decimal GetDecimal(NpgsqlDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? 0m : r.GetDecimal(ordinal);
    }

    private static SalesPolicyException Translate(PostgresException pg) => pg.SqlState switch
    {
        "23505" => new("IDEMPOTENCY_KEY_CONFLICT", "مفتاح Idempotency مستخدم — العملية موجودة سابقاً ولا تُكرر"),
        "23514" when pg.MessageText.Contains("INVOICE_") => new("INVOICE_CONSTRAINT", pg.MessageText),
        "23514" when pg.MessageText.Contains("REFUND_") => new("REFUND_CONSTRAINT", pg.MessageText),
        "23514" when pg.MessageText.Contains("NEGATIVE_STOCK") => new(
            pg.MessageText.Contains("NEEDS_APPROVAL") ? "NEGATIVE_STOCK_NEEDS_APPROVAL" : "NEGATIVE_STOCK_BLOCKED",
            "الحركة تخالف سياسة المخزون السالب (D-03) — يلزم اعتماد مشرف أو رفض"),
        "23514" => new("SALES_CONSTRAINT_VIOLATION", pg.MessageText),
        "23503" => new("SALES_FOREIGN_KEY", "مرجع غير موجود (فرع/محطة/صنف/خزينة) في عملية البيع"),
        _ => new("SALES_DB_ERROR", $"خطأ قاعدة غير متوقع: {pg.SqlState}"),
    };
}
