namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

/// <summary>
/// فحوص بوابة G6 (§12 المخزون و§12.3 المشتريات): السيناريو الكامل من السلة
/// إلى كشف الحساب ببوابة القبول حرفياً — الرصيد ناتج عن حركات، تحويل العبوة
/// والوحدة والوزن صحيح، لا تكرار استلام/دفع، استعادة المسودة لا تكرر الحركة،
/// والتقارير تطابق دفتر الحركة — على PostgreSQL حقيقي مُرحّل حتى رأس V017.
/// </summary>
public sealed class G6EndpointTests : IClassFixture<G6ApiFixture>
{
    private readonly G6ApiFixture _fx;

    public G6EndpointTests(G6ApiFixture fx) => _fx = fx;

    private async Task<HttpClient> ClientAsync(string username, string password, long? branchId = null)
    {
        var login = await _fx.LoginAsync(username, password, branchId);
        var client = _fx.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<decimal> ScalarDecimalAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? string.Empty : (string)value;
    }

    // ═══ السيناريو الكامل — بوابة القبول حرفياً ═══════════════════════════

    [Fact]
    public async Task FullGateScenario_FromBasketToStatement_AllAcceptanceCriteria()
    {
        var bagSku = await G6ApiFixture.GetSkuIdAsync("G6-SCN-01-BAG");
        var boxSku = await G6ApiFixture.GetSkuIdAsync("G6-SCN-01-BOX");
        var cheeseSku = await G6ApiFixture.GetSkuIdAsync("G6-CHEESE-KG");

        // ── 1) PAGE-28: إنشاء مخزن فرعي عبر API + تعارض الكود + نوع غير معروف ──
        using (var owner = await ClientAsync("g6.owner", "Owner#G6-Pass", _fx.BranchId))
        {
            var wh = await owner.PostAsJsonAsync("/api/v1/inventory/warehouses", new
            {
                branchId = _fx.BranchId, code = "G6WSUB", nameAr = "مخزن فرعي تجريبي", whType = "SUB",
            });
            Assert.Equal(HttpStatusCode.Created, wh.StatusCode);
            var duplicate = await owner.PostAsJsonAsync("/api/v1/inventory/warehouses", new
            {
                branchId = _fx.BranchId, code = "G6WSUB", nameAr = "مكرر", whType = "SUB",
            });
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

            var badType = await owner.PostAsJsonAsync("/api/v1/inventory/warehouses", new
            {
                branchId = _fx.BranchId, code = "G6W9", nameAr = "نوع غير معروف", whType = "GALAXY",
            });
            Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        }
        var warehouse2 = _fx.Warehouse2Id;

        // ── 2) المورد: إنشاء + كشف حساب فارغ بصدق ────────────────────────────
        long supplierId;
        using (var owner = await ClientAsync("g6.owner", "Owner#G6-Pass", _fx.BranchId))
        {
            var supplier = await owner.PostAsJsonAsync("/api/v1/purchasing/suppliers", new
            {
                code = "G6SUP1", nameAr = "مورد الألبان التجريبي", phone = "01000000001",
                paymentTermsDays = 15, creditLimit = 10000m, notes = "بذر G6",
            });
            Assert.Equal(HttpStatusCode.Created, supplier.StatusCode);
            supplierId = (await supplier.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("supplierId").GetInt64();

            var empty = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/purchasing/suppliers/{supplierId}/statement");
            Assert.Equal(0, empty.GetProperty("closingBalance").GetDecimal());
            Assert.Equal(0, empty.GetProperty("entries").GetArrayLength());
        }

        // ── 3) سلة المشتريات: مسودة قابلة للاستعادة ثم أمر ───────────────────
        long poId;
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            var basket = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/purchase-orders", new
            {
                supplierId, warehouseId = _fx.WarehouseId,
                expectedDate = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(3),
                lines = new object[]
                {
                    new { skuId = boxSku, qty = 5m, uomId = 1, unitCost = 24m },
                    new { skuId = cheeseSku, qty = 3m, uomId = 2, unitCost = 80m },
                },
            });
            Assert.Equal(HttpStatusCode.Created, basket.StatusCode);
            var body = await basket.Content.ReadFromJsonAsync<JsonElement>();
            poId = body.GetProperty("poId").GetInt64();
            Assert.Equal("DRAFT", body.GetProperty("status").GetString());
            Assert.StartsWith("PO-", body.GetProperty("poNumber").GetString());

            // استعادة المسودة (قراءة فقط — لا تكرر أي حركة)
            var restored = await storekeeper.GetFromJsonAsync<JsonElement>($"/api/v1/purchasing/purchase-orders/{poId}");
            Assert.Equal(2, restored.GetProperty("lines").GetArrayLength());

            // إصدار الأمر
            var submitted = await storekeeper.PostAsync($"/api/v1/purchasing/purchase-orders/{poId}/submit", null);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            Assert.Equal("ORDERED", (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("newStatus").GetString());
        }

        // ── 4) الاستلام: تحويل العبوة/الوحدة/الوزن صحيح + لا تكرار استلام ─────
        long receiptId;
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            // أمر بمخزن مختلف → رفض صريح
            var wrongWarehouse = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/purchase-receipts", new
            {
                supplierId, warehouseId = warehouse2, poId, notes = "مخزن مختلف عن الأمر",
                lines = new object[] { new { skuId = boxSku, qty = 5m, uomId = 1, unitCost = 24m } },
            });
            Assert.Equal(HttpStatusCode.BadRequest, wrongWarehouse.StatusCode);

            var draft = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/purchase-receipts", new
            {
                supplierId, warehouseId = _fx.WarehouseId, poId, notes = "استلام السيناريو",
                lines = new object[]
                {
                    new { skuId = boxSku, qty = 5m, uomId = 1, unitCost = 24m, lotCode = "MILK-LOT-1", lotExpiry = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(30) },
                    new { skuId = cheeseSku, qty = 3m, uomId = 2, unitCost = 80m },
                },
            });
            Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
            receiptId = (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receiptId").GetInt64();

            var posted = await storekeeper.PostAsJsonAsync($"/api/v1/purchasing/purchase-receipts/{receiptId}/post",
                new { idempotencyKey = "g6-scenario-receipt-1" });
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            var postedBody = await posted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, postedBody.GetProperty("movementIds").GetArrayLength());
            Assert.Equal(63m, postedBody.GetProperty("totalBaseQty").GetDecimal());   // 60 كيس + 3 كجم

            // ترحيل ثانٍ لنفس الاستلام → 409 (لا تكرار استلام)
            var again = await storekeeper.PostAsJsonAsync($"/api/v1/purchasing/purchase-receipts/{receiptId}/post", new { });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            // مفتاح Idempotency مستهلك: استلام آخر بنفس المفتاح → 409 (فهرس V017 الفريد الجزئي)
            var otherDraft = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/purchase-receipts", new
            {
                supplierId, warehouseId = _fx.WarehouseId, notes = "استلام بمفتاح مستهلك",
                lines = new object[] { new { skuId = boxSku, qty = 1m, uomId = 1, unitCost = 24m } },
            });
            var otherId = (await otherDraft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("receiptId").GetInt64();
            var sameKey = await storekeeper.PostAsJsonAsync($"/api/v1/purchasing/purchase-receipts/{otherId}/post",
                new { idempotencyKey = "g6-scenario-receipt-1" });
            Assert.Equal(HttpStatusCode.Conflict, sameKey.StatusCode);
            await storekeeper.PostAsync($"/api/v1/purchasing/purchase-receipts/{otherId}/cancel", null);

            // صنف وزني فوق حده الأقصى → رفض من قواعد الوزن (§5.1)
            var tooHeavy = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/purchase-receipts", new
            {
                supplierId, warehouseId = _fx.WarehouseId,
                lines = new object[] { new { skuId = cheeseSku, qty = 9m, uomId = 2, unitCost = 80m } },
            });
            Assert.Equal(HttpStatusCode.BadRequest, tooHeavy.StatusCode);
        }

        // الأثر وقع على صنف المحتوى (الكيس) بلقطة عامل العبوة — لا حركة على SKU العلبة
        Assert.Equal(60m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_movements WHERE source_doc_type='purch.receipt' AND source_doc_id={receiptId} AND sku_id={bagSku}"));
        Assert.Equal(12m, await ScalarDecimalAsync($"SELECT conversion_factor FROM inv.stock_movements WHERE source_doc_type='purch.receipt' AND source_doc_id={receiptId} AND sku_id={bagSku}"));
        Assert.Equal(3m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_movements WHERE source_doc_type='purch.receipt' AND source_doc_id={receiptId} AND sku_id={cheeseSku}"));
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_movements WHERE sku_id={boxSku}"));

        // ── المعيار 1: الرصيد ناتج عن حركات — الدفتر = الرصيد المخزن حرفياً ──
        Assert.Equal(60m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));
        Assert.Equal(3m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={cheeseSku} AND warehouse_id={_fx.WarehouseId}"));
        Assert.Equal(60m, await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(direction * base_qty),0) FROM inv.stock_movements WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));

        // WAC (D-02 أ) بعد الاستلام — تكلفة الوحدة الأساسية للكيس = ثمن العلبة ÷ 12
        Assert.Equal(2.0000m, await ScalarDecimalAsync($"SELECT avg_unit_cost FROM finance.cost_layers WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));
        Assert.Equal(80.0000m, await ScalarDecimalAsync($"SELECT avg_unit_cost FROM finance.cost_layers WHERE sku_id={cheeseSku} AND warehouse_id={_fx.WarehouseId}"));

        // تغطية الأمر بنفس وحداته → RECEIVED
        Assert.Equal("RECEIVED", await ScalarTextAsync($"SELECT status FROM purch.purchase_orders WHERE po_id={poId}"));

        // ── 5) الحجز والمتاح (§12.1): المتاح = الرصيد − المحجوز ─────────────
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            var reserved = await storekeeper.PostAsJsonAsync("/api/v1/inventory/reservations", new
            {
                skuId = bagSku, warehouseId = _fx.WarehouseId, qtyBase = 10m,
                sourceDocType = "purch.purchase-orders", sourceDocId = poId,
            });
            Assert.Equal(HttpStatusCode.Created, reserved.StatusCode);
            var reservationId = (await reserved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservationId").GetInt64();

            var over = await storekeeper.PostAsJsonAsync("/api/v1/inventory/reservations", new
            {
                skuId = bagSku, warehouseId = _fx.WarehouseId, qtyBase = 100m,
                sourceDocType = "purch.purchase-orders", sourceDocId = poId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);

            var balances = await storekeeper.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/balances?warehouseId={_fx.WarehouseId}");
            var bagRow = balances.GetProperty("items").EnumerateArray().First(b => b.GetProperty("skuCode").GetString() == "G6-SCN-01-BAG");
            Assert.Equal(60m, bagRow.GetProperty("baseQty").GetDecimal());
            Assert.Equal(10m, bagRow.GetProperty("reservedQty").GetDecimal());
            Assert.Equal(50m, bagRow.GetProperty("availableQty").GetDecimal());

            var released = await storekeeper.PostAsJsonAsync($"/api/v1/inventory/reservations/{reservationId}/release", new { reasonAr = "انتهت الحاجة" });
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
            balances = await storekeeper.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/balances?warehouseId={_fx.WarehouseId}");
            bagRow = balances.GetProperty("items").EnumerateArray().First(b => b.GetProperty("skuCode").GetString() == "G6-SCN-01-BAG");
            Assert.Equal(60m, bagRow.GetProperty("availableQty").GetDecimal());
        }

        // ── 6) فاتورة المورد: التزام مالي — لا حركة مخزون (§12.3 ق1) ─────────
        long invoiceId;
        using (var accountant = await ClientAsync("g6.accountant", "Accountant#G6-Pass", _fx.BranchId))
        {
            var badReceipt = await accountant.PostAsJsonAsync("/api/v1/purchasing/supplier-invoices", new
            {
                supplierId, receiptId = 999999, invoiceNumber = "INV-BAD-1",
                invoiceDate = DateOnly.FromDateTime(DateTime.UtcNow.Date), subtotal = 360m, taxTotal = 40m,
                lines = Array.Empty<object>(),
            });
            Assert.Equal(HttpStatusCode.NotFound, badReceipt.StatusCode);

            var invoice = await accountant.PostAsJsonAsync("/api/v1/purchasing/supplier-invoices", new
            {
                supplierId, receiptId, invoiceNumber = "SUP-INV-1001",
                invoiceDate = DateOnly.FromDateTime(DateTime.UtcNow.Date), dueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(15),
                subtotal = 360m, taxTotal = 40m,      // 5 علب×24 + 3كجم×80 = 360 + ضريبة 40 = إجمالي 400
                lines = new object[]
                {
                    new { skuId = boxSku, qty = 5m, uomId = 1, unitCost = 24m },
                    new { skuId = cheeseSku, qty = 3m, uomId = 2, unitCost = 80m },
                },
            });
            Assert.Equal(HttpStatusCode.Created, invoice.StatusCode);
            var invoiceBody = await invoice.Content.ReadFromJsonAsync<JsonElement>();
            invoiceId = invoiceBody.GetProperty("invoiceId").GetInt64();
            Assert.Equal(400m, invoiceBody.GetProperty("grandTotal").GetDecimal());

            var posted = await accountant.PostAsync($"/api/v1/purchasing/supplier-invoices/{invoiceId}/post", null);
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            var again = await accountant.PostAsync($"/api/v1/purchasing/supplier-invoices/{invoiceId}/post", null);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            // الفاتورة وحدها لم تزد المخزون
            Assert.Equal(60m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));
        }

        // ── 7) السداد: أثر خزينة واحد — لا تكرار دفع ولا مصروف (§12.3 ق3) ──
        long payment1Id;
        using (var owner = await ClientAsync("g6.owner", "Owner#G6-Pass", _fx.BranchId))
        {
            var overInvoice = await owner.PostAsJsonAsync("/api/v1/purchasing/supplier-payments", new
            {
                supplierId, treasuryAccountId = _fx.TreasuryId, amount = 500m,
                paymentMethod = "CASH", paymentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                allocations = new object[] { new { invoiceId, amount = 500m } },
            });
            Assert.Equal(HttpStatusCode.BadRequest, overInvoice.StatusCode);
            Assert.Contains("أكثر من المتبقي", (await overInvoice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

            var payment1 = await owner.PostAsJsonAsync("/api/v1/purchasing/supplier-payments", new
            {
                supplierId, treasuryAccountId = _fx.TreasuryId, amount = 250m,
                paymentMethod = "BANK_TRANSFER", paymentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                notes = "دفعة أولى", allocations = new object[] { new { invoiceId, amount = 250m } },
            });
            Assert.Equal(HttpStatusCode.Created, payment1.StatusCode);
            payment1Id = (await payment1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentId").GetInt64();

            var posted1 = await owner.PostAsJsonAsync($"/api/v1/purchasing/supplier-payments/{payment1Id}/post", new { idempotencyKey = "g6-pay-1" });
            Assert.Equal(HttpStatusCode.OK, posted1.StatusCode);
            Assert.True((await posted1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("treasuryTxnId").GetInt64() > 0);

            var again = await owner.PostAsJsonAsync($"/api/v1/purchasing/supplier-payments/{payment1Id}/post", new { });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            // أثر واحد على الخزينة مهما أعيد الإرسال — وصفر مصروفات لهذا السيناريو (§12.3 ق3):
            // السداد ليس مصروفاً — النطاق بمستخدمي G6 (اختبارات G8 اللاحقة تنشئ مصروفاتها المشروعة)
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM finance.treasury_transactions WHERE source_doc_type='purch.payment' AND source_doc_id={payment1Id}"));
            Assert.Equal(0, await ScalarAsync("""
                SELECT COUNT(*) FROM finance.expenses e
                  JOIN core.users u ON u.user_id = e.created_by
                 WHERE u.username LIKE 'g6.%'
                """));

            var invoiceState = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/purchasing/supplier-invoices/{invoiceId}");
            Assert.Equal("PARTIALLY_PAID", invoiceState.GetProperty("status").GetString());
            Assert.Equal(150m, invoiceState.GetProperty("remainingAmount").GetDecimal());
        }

        // ── 8) مرتجع المورد: مرتبط بالأصل بحد ما استُلم (§12.3 ق4) ──────────
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            var over = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/supplier-returns", new
            {
                supplierId, receiptId, warehouseId = _fx.WarehouseId, refundMethod = "CREDIT_NOTE",
                lines = new object[] { new { skuId = boxSku, qty = 50m, uomId = 1, unitCost = 24m } },   // 600 كيس > 60
            });
            Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
            Assert.Contains("يتجاوز ما استُلم", (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

            var draft = await storekeeper.PostAsJsonAsync("/api/v1/purchasing/supplier-returns", new
            {
                supplierId, receiptId, invoiceId, warehouseId = _fx.WarehouseId, refundMethod = "CREDIT_NOTE",
                lines = new object[] { new { skuId = boxSku, qty = 1m, uomId = 1, unitCost = 24m } },   // علبة = 12 كيس
            });
            Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
            var returnId = (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnId").GetInt64();

            var posted = await storekeeper.PostAsJsonAsync($"/api/v1/purchasing/supplier-returns/{returnId}/post", new { idempotencyKey = "g6-ret-1" });
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            var postedBody = await posted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, postedBody.GetProperty("movementIds").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, postedBody.GetProperty("treasuryTxnId").ValueKind);   // إشعار دائن — لا أثر خزينة
            Assert.True(postedBody.GetProperty("reducesPayable").GetBoolean());           // بل تخفيض للذمم

            var again = await storekeeper.PostAsJsonAsync($"/api/v1/purchasing/supplier-returns/{returnId}/post", new { });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
        // الرصيد بعد المرتجع: 60 − 12 = 48 (حركة SUPPLIER_RETURN على صنف المحتوى)
        Assert.Equal(48m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_movements WHERE movement_type='SUPPLIER_RETURN' AND sku_id={bagSku}"));

        // ── 9) السداد الثاني حتى الإقفال + كشف الحساب (§13) ─────────────────
        using (var owner = await ClientAsync("g6.owner", "Owner#G6-Pass", _fx.BranchId))
        {
            // تخصيص يتجاوز المتبقي (150) بعد تخفيض الإشعار الدائن (المتبقي 126)
            var overRemaining = await owner.PostAsJsonAsync("/api/v1/purchasing/supplier-payments", new
            {
                supplierId, treasuryAccountId = _fx.TreasuryId, amount = 200m,
                paymentMethod = "CASH", paymentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                allocations = new object[] { new { invoiceId, amount = 200m } },
            });
            Assert.Equal(HttpStatusCode.BadRequest, overRemaining.StatusCode);

            var payment2 = await owner.PostAsJsonAsync("/api/v1/purchasing/supplier-payments", new
            {
                supplierId, treasuryAccountId = _fx.TreasuryId, amount = 126m,
                paymentMethod = "CASH", paymentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                notes = "دفعة ختامية", allocations = new object[] { new { invoiceId, amount = 126m } },
            });
            var payment2Id = (await payment2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentId").GetInt64();
            var posted2 = await owner.PostAsync($"/api/v1/purchasing/supplier-payments/{payment2Id}/post", null);
            Assert.Equal(HttpStatusCode.OK, posted2.StatusCode);

            var invoiceState = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/purchasing/supplier-invoices/{invoiceId}");
            Assert.Equal("PAID", invoiceState.GetProperty("status").GetString());

            // ── المعيار 5: التقارير تطابق الدفتر/المستندات ─────────────────
            var statement = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/purchasing/suppliers/{supplierId}/statement?from={DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-7):yyyy-MM-dd}&to={DateOnly.FromDateTime(DateTime.UtcNow.Date):yyyy-MM-dd}");
            Assert.Equal(400m, statement.GetProperty("totalCharges").GetDecimal());       // الفاتورة
            Assert.Equal(400m, statement.GetProperty("totalCredits").GetDecimal());       // 250 + 126 + إشعار دائن 24
            Assert.Equal(0m, statement.GetProperty("closingBalance").GetDecimal());
            Assert.Equal(4, statement.GetProperty("entries").GetArrayLength());      // فاتورة + سدادان + إشعار دائن
        }
        // كشف مستقل من الجداول الخام: افتتاحي 0، ختامي 0 (لا حسابات مختلفة بين الصفحات §16.5)
        // كشف مستقل من الجداول الخام: نفس أرقام كشف الحساب (لا حسابات مختلفة بين الصفحات §16.5)
        var rawCharges = await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(grand_total),0) FROM purch.supplier_invoices WHERE supplier_id={G6ApiFixture.SupplierDbId()} AND status NOT IN ('DRAFT','CANCELLED')");
        var rawCredits = await ScalarDecimalAsync(
            $"SELECT COALESCE((SELECT SUM(amount) FROM purch.supplier_payments p WHERE p.supplier_id={G6ApiFixture.SupplierDbId()} AND p.status='POSTED'),0) + COALESCE((SELECT SUM(total) FROM purch.supplier_returns r WHERE r.supplier_id={G6ApiFixture.SupplierDbId()} AND r.status='POSTED' AND r.refund_method='CREDIT_NOTE'),0)");
        Assert.Equal(400m, rawCharges);
        Assert.Equal(400m, rawCredits);

        // ── 10) الجرد والتسويات (PAGE-20): فصل واجبات المعتمد ≠ الجارّ ──────
        using (var manager = await ClientAsync("g6.manager", "Manager#G6-Pass", _fx.BranchId))
        using (var owner = await ClientAsync("g6.owner", "Owner#G6-Pass", _fx.BranchId))
        {
            // المدير يجرد (رصد PAGE-20: م، مش، خ) — ثم يُختبر فصل الواجبات عليه صراحة
            var opened = await manager.PostAsJsonAsync("/api/v1/inventory/stocktakes", new
            {
                warehouseId = _fx.WarehouseId, notes = "جرد نهاية الأسبوع",
            });
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            var countId = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("countId").GetInt64();

            // جرد ثانٍ على نفس المخزن قبل إقفال الأول → رفض
            var second = await manager.PostAsJsonAsync("/api/v1/inventory/stocktakes", new { warehouseId = _fx.WarehouseId });
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

            // تسجيل العد: الكيس 45 (دفترياً 48) والجبنة 3 (مطابق — فرق صفر)
            var recorded = await manager.PostAsJsonAsync($"/api/v1/inventory/stocktakes/{countId}/lines", new
            {
                lines = new object[]
                {
                    new { skuId = bagSku, countedQty = 45m },
                    new { skuId = cheeseSku, countedQty = 3m },
                },
            });
            Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
            var lines = (await recorded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines");
            // البحث بالكود — لا بترتيب المصفوفة (الترتيب أبجدي: الجبنة قبل الكيس)
            var bagLine = lines.EnumerateArray().First(l => l.GetProperty("skuCode").GetString() == "G6-SCN-01-BAG");
            var cheeseLine = lines.EnumerateArray().First(l => l.GetProperty("skuCode").GetString() == "G6-CHEESE-KG");
            Assert.Equal(-3m, bagLine.GetProperty("varianceQty").GetDecimal());
            Assert.Equal(48m, bagLine.GetProperty("systemQty").GetDecimal());       // لقطة الدفتر وقت العد
            Assert.Equal(0m, cheeseLine.GetProperty("varianceQty").GetDecimal());   // فرق صفر — لا حركة عند الاعتماد

            var closed = await manager.PostAsync($"/api/v1/inventory/stocktakes/{countId}/close", null);
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            var reconciled = await manager.PostAsync($"/api/v1/inventory/stocktakes/{countId}/reconcile", null);
            Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);

            // الجارّ نفسه لا يعتمد جرده — له صلاحية الاعتماد لكن فصل الواجبات يمنعه (قيد V003)
            var selfApprove = await manager.PostAsync($"/api/v1/inventory/stocktakes/{countId}/approve", null);
            Assert.Equal(HttpStatusCode.Conflict, selfApprove.StatusCode);
            Assert.Contains("غير من قام بالجرد", (await selfApprove.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

            var approved = await owner.PostAsync($"/api/v1/inventory/stocktakes/{countId}/approve", null);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var approvedBody = await approved.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, approvedBody.GetProperty("adjustedLines").GetInt32());    // الكيس فقط
            Assert.Equal(1, approvedBody.GetProperty("zeroVarianceLines").GetInt32()); // الجبنة فرق صفر — بلا حركة
            Assert.Equal(1, approvedBody.GetProperty("movementIds").GetArrayLength());

            // الرصيد بعد التسوية المعتمدة = المعدود حرفياً
            Assert.Equal(45m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_movements WHERE movement_type='ADJUSTMENT_OUT' AND sku_id={bagSku}"));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_adjustments WHERE count_id={countId} AND adjustment_kind='STOCKTAKE'"));
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM core.approvals WHERE entity_type='inv.count_session' AND decision='APPROVED'"));
        }

        // ── 11) الهالك (PAGE-23): اعتماد مشرف إلزامي (D-03 ب + V003) ─────────
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        using (var supervisor = await ClientAsync("g6.supervisor", "Supervisor#G6-Pass", _fx.BranchId))
        using (var manager = await ClientAsync("g6.manager", "Manager#G6-Pass", _fx.BranchId))
        {
            var cheeseNow = await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={cheeseSku} AND warehouse_id={_fx.WarehouseId}");   // 3

            // بلا اعتماد → رفض
            var noApproval = await storekeeper.PostAsJsonAsync("/api/v1/inventory/adjustments", new
            {
                skuId = cheeseSku, warehouseId = _fx.WarehouseId, newQty = 1m,
                adjustmentKind = "DAMAGE", reasonAr = "تلف نقل", approvalId = 0,
            });
            Assert.Equal(HttpStatusCode.Conflict, noApproval.StatusCode);

            // المدير (له صلاحية البت) يطلب الاعتماد — ثم يُختبر منعه من البت في طلبه (قيد V003)
            var request = await manager.PostAsJsonAsync("/api/v1/approvals", new
            {
                entityType = "inv.adjustment", entityId = 0, reasonAr = "هالك جبنة تالفة أثناء العرض",
            });
            Assert.Equal(HttpStatusCode.Created, request.StatusCode);
            var approvalId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();

            // طالب العملية لا يبت في طلبه — لديه الصلاحية لكن فصل الواجبات يمنعه (قيد V003 approvals_no_self)
            var selfDecide = await manager.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "APPROVED", reasonAr = "موافقة ذاتية" });
            Assert.Equal(HttpStatusCode.Conflict, selfDecide.StatusCode);

            var decided = await supervisor.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "APPROVED", reasonAr = "معاينة التلف" });
            Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

            var damage = await storekeeper.PostAsJsonAsync("/api/v1/inventory/adjustments", new
            {
                skuId = cheeseSku, warehouseId = _fx.WarehouseId, newQty = 1m,   // الباقي 1 — الهالك = 2
                adjustmentKind = "DAMAGE", reasonAr = "تلف نقل", approvalId,
            });
            Assert.Equal(HttpStatusCode.Created, damage.StatusCode);
            var damageBody = await damage.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("DAMAGE", damageBody.GetProperty("movementType").GetString());
            Assert.Equal(2m, damageBody.GetProperty("baseQty").GetDecimal());

            Assert.Equal(1m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={cheeseSku} AND warehouse_id={_fx.WarehouseId}"));
            Assert.Equal(1m, cheeseNow - 2m);   // 3 كجم − هالك 2 = 1
        }

        // ── 12) التحويل بين المخازن + D-03 (نقص مؤقت باعتماد مشرف) ───────────
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        using (var supervisor = await ClientAsync("g6.supervisor", "Supervisor#G6-Pass", _fx.BranchId))
        {
            // تحويل 200 كيس من رصيد 45 → نقص يتطلب اعتماداً
            var transfer = await storekeeper.PostAsJsonAsync("/api/v1/inventory/transfers", new
            {
                fromWarehouseId = _fx.WarehouseId, toWarehouseId = warehouse2,
                lines = new object[] { new { skuId = bagSku, qtyBase = 200m } },
            });
            Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
            var transferId = (await transfer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transferId").GetInt64();

            var blocked = await storekeeper.PostAsJsonAsync($"/api/v1/inventory/transfers/{transferId}/send", new { });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains("اعتماد مشرف", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

            var request = await storekeeper.PostAsJsonAsync("/api/v1/approvals", new
            {
                entityType = "inv.transfer", entityId = transferId, reasonAr = "نقل طارئ لفرع ثانٍ",
            });
            var approvalId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();
            await supervisor.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "APPROVED", reasonAr = "مقبول" });

            var sent = await storekeeper.PostAsJsonAsync($"/api/v1/inventory/transfers/{transferId}/send", new { approvalId });
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

            // المصدر سالب بإذن (D-03 ب) والوجهة لم تستلم بعد
            Assert.Equal(-155m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}"));
            var approvalOnMovement = await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_movements WHERE movement_type='TRANSFER_OUT' AND approval_id={approvalId}");
            Assert.Equal(1, approvalOnMovement);

            // الاستلام في مخزن الوجهة يرحّل TRANSFER_IN — الرصيد النهائي = ما قبل الإرسال + 200
            var w2Before = await ScalarDecimalAsync($"SELECT COALESCE(base_qty,0) FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={warehouse2}");
            var received = await storekeeper.PostAsync($"/api/v1/inventory/transfers/{transferId}/receive", null);
            Assert.Equal(HttpStatusCode.OK, received.StatusCode);
            Assert.Equal(w2Before + 200m, await ScalarDecimalAsync($"SELECT base_qty FROM inv.stock_balances WHERE sku_id={bagSku} AND warehouse_id={warehouse2}"));

            // الإلغاء بعد الإرسال مرفوض — الخروج وقع في الدفتر
            var cancel = await storekeeper.PostAsync($"/api/v1/inventory/transfers/{transferId}/cancel", null);
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        }

        // ── 13) كرت الحركة (PAGE-19): الرصيد الجاري يطابق الدفتر ─────────────
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            // نقطة النهاية تُعيد مصفوفة الحركات مباشرة
            var movements = await storekeeper.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/skus/{bagSku}/movements?warehouseId={_fx.WarehouseId}");
            Assert.True(movements.GetArrayLength() >= 4);
            var last = movements[movements.GetArrayLength() - 1];
            Assert.Equal(-155m, last.GetProperty("runningBalance").GetDecimal());
            var ledger = await ScalarDecimalAsync(
                $"SELECT COALESCE(SUM(direction*base_qty),0) FROM inv.stock_movements WHERE sku_id={bagSku} AND warehouse_id={_fx.WarehouseId}");
            Assert.Equal(ledger, last.GetProperty("runningBalance").GetDecimal());   // كرت الحركة = الدفتر حرفياً

            // تقييم المخزون (D-02 WAC) — PAGE-18: الجبنة (رصيد 1 × WAC 80)
            // والكيس رصيده سالب بعد النقل المعتمد (D-03) فلا يُقيَّم — لا تقييم لمخزون غير مملوك
            var valuation = await storekeeper.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/valuation?warehouseId={_fx.WarehouseId}");
            var cheeseValuation = valuation.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("skuCode").GetString() == "G6-CHEESE-KG");
            Assert.Equal(80.0000m, cheeseValuation.GetProperty("avgUnitCost").GetDecimal());
            Assert.Equal(80.0000m, cheeseValuation.GetProperty("valuation").GetDecimal());
            Assert.DoesNotContain(valuation.GetProperty("lines").EnumerateArray(),
                l => l.GetProperty("skuCode").GetString() == "G6-SCN-01-BAG");

            // حتمية عبر ترتيب التنفيذ (اكتُشف بدورة G8): فحص النواقص أدناه يشترط رصيد W3 = +5
            // الذي ينشئه اختبار CrossBranchScope — وترتيب xUnit داخل الفئة غير مضمون، فيُستكمل
            // الشرط هنا إن لم يكن قائماً (نقل 5 عبر الفروع باعتماد D-03 ثم استلام بفرع الوجهة)
            var w3Now = await ScalarDecimalAsync($"SELECT COALESCE((SELECT SUM(direction*base_qty) FROM inv.stock_movements WHERE sku_id={bagSku} AND warehouse_id={_fx.Warehouse3Id}),0)");
            if (w3Now < 5m)
            {
                var setup = await storekeeper.PostAsJsonAsync("/api/v1/inventory/transfers", new
                {
                    fromWarehouseId = _fx.Warehouse2Id, toWarehouseId = _fx.Warehouse3Id,
                    lines = new object[] { new { skuId = bagSku, qtyBase = 5m } },
                });
                Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
                var setupId = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transferId").GetInt64();
                var setupApproval = await storekeeper.PostAsJsonAsync("/api/v1/approvals", new
                {
                    entityType = "inv.transfer", entityId = setupId, reasonAr = "استكمال حالة W3 لفحص النواقص",
                });
                var setupApprovalId = (await setupApproval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();
                using (var g6Supervisor = await ClientAsync("g6.supervisor", "Supervisor#G6-Pass", _fx.BranchId))
                {
                    await g6Supervisor.PostAsJsonAsync($"/api/v1/approvals/{setupApprovalId}/decision", new { decision = "APPROVED", reasonAr = "مقبول" });
                }
                Assert.Equal(HttpStatusCode.OK, (await storekeeper.PostAsJsonAsync($"/api/v1/inventory/transfers/{setupId}/send", new { approvalId = setupApprovalId })).StatusCode);
                using (var branch2Receiver = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.Branch2Id))
                {
                    Assert.Equal(HttpStatusCode.OK, (await branch2Receiver.PostAsync($"/api/v1/inventory/transfers/{setupId}/receive", null)).StatusCode);
                }
            }

            // النواقص وإعادة الطلب (PAGE-22): الكيس تحت مستوى إعادة الطلب (10) في كل مخازنه الثلاثة
            var reorder = await storekeeper.GetFromJsonAsync<JsonElement>("/api/v1/inventory/reorder");
            var items = reorder.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("skuCode").GetString() == "G6-SCN-01-BAG").ToList();
            Assert.Equal(2, items.Count);   // W1 (-155) وW3 (+5) تحت المستوى 10 — وW2 فوقه (195 بعد النقل المستلم)
            var w1Row = items.First(i => i.GetProperty("warehouseCode").GetString() == "G6W1");
            Assert.Equal(165m, w1Row.GetProperty("suggestedQty").GetDecimal());   // يرفع المتاح (-155) إلى المستوى 10
            var w3Row = items.First(i => i.GetProperty("warehouseCode").GetString() == "G6W3");
            Assert.Equal(5m, w3Row.GetProperty("suggestedQty").GetDecimal());

            // الدفعات (PAGE-23): دفعة الاستلام بلot
            var lots = await storekeeper.GetFromJsonAsync<JsonElement>("/api/v1/inventory/lots?expiringInDays=45");
            var lot = lots.GetProperty("items").EnumerateArray().FirstOrDefault(l => l.GetProperty("lotCode").GetString() == "MILK-LOT-1", default);
            Assert.NotEqual(default, lot);
            Assert.Equal(60m, lot.GetProperty("onHandQty").GetDecimal());
            Assert.True(lot.GetProperty("isExpiringSoon").GetBoolean());
        }

        // ── 14) الأطراف: تعطيل لا حذف (PRT-002) + كشف العميل الصادق ─────────
        using (var owner = await ClientAsync("g6.owner", "Owner#G6-Pass", _fx.BranchId))
        {
            var customer = await owner.PostAsJsonAsync("/api/v1/parties/customers", new
            {
                code = "G6CUST1", nameAr = "عميل تجريبي", phone = "01234567890",
                creditLimit = 0m, creditEnabled = false, paymentTermsDays = 0,
            });
            Assert.Equal(HttpStatusCode.Created, customer.StatusCode);
            var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerId").GetInt64();

            // كشف حساب العميل: لا حركات بعد (فواتير آجلة G7 وسندات G8 تغذيها)
            var statement = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/parties/customers/{customerId}/statement");
            Assert.Equal(0, statement.GetProperty("closingBalance").GetDecimal());
            Assert.Equal(0, statement.GetProperty("entries").GetArrayLength());

            var suspended = await owner.PostAsJsonAsync($"/api/v1/parties/customers/{customerId}/status", new { accountStatus = "SUSPENDED", reasonAr = "شيك مرتجع" });
            Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
            var archived = await owner.PostAsJsonAsync($"/api/v1/parties/customers/{customerId}/status", new { accountStatus = "ARCHIVED", reasonAr = "إقفال نهائي" });
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            var resurrect = await owner.PostAsJsonAsync($"/api/v1/parties/customers/{customerId}/status", new { accountStatus = "ACTIVE", reasonAr = "محاولة تنشيط من الأرشيف" });
            Assert.Equal(HttpStatusCode.Conflict, resurrect.StatusCode);   // الأرشيف نهائي — تعطيل لا حذف (§13)

            // الطرف باقٍ في السجل بعد التعطيل (لا حذف لطرف له مستندات)
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM sales.customers WHERE customer_id={customerId}"));
        }

        // ── 15) استعادة المسودة لا تكرر الحركة (بوابة القبول حرفياً) ──────────
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            var before = await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='purch.receipt' AND source_doc_id={receiptId}");
            var restored = await storekeeper.GetFromJsonAsync<JsonElement>($"/api/v1/purchasing/purchase-receipts/{receiptId}");
            Assert.Equal("POSTED", restored.GetProperty("status").GetString());   // الاستعادة قراءة فقط
            var retry = await storekeeper.PostAsJsonAsync($"/api/v1/purchasing/purchase-receipts/{receiptId}/post", new { });
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            var after = await ScalarAsync($"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='purch.receipt' AND source_doc_id={receiptId}");
            Assert.Equal(before, after);   // أثر واحد مهما استُعيدت المسودة وأعيد الترحيل
        }
    }

    // ═══ RBAC G6 (§18 · V017) ═════════════════════════════════════════════

    [Fact]
    public async Task Rbac_Matrix_G6()
    {
        // الكاشير يقرأ المخزون (تكوين عمل) ولا يرحّل حركات
        using (var cashier = await ClientAsync("g6.cashier", "Cashier#G6-Pass", _fx.BranchId))
        {
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/inventory/balances")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/inventory/lots")).StatusCode);

            var adjust = await cashier.PostAsJsonAsync("/api/v1/inventory/adjustments", new
            {
                skuId = 1, warehouseId = _fx.WarehouseId, newQty = 1m,
                adjustmentKind = "QUICK", reasonAr = "محاولة كاشير", approvalId = 1,
            });
            Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);

            var receive = await cashier.PostAsJsonAsync("/api/v1/purchasing/purchase-receipts", new
            {
                supplierId = 1, warehouseId = _fx.WarehouseId,
                lines = new object[] { new { skuId = 1, qty = 1m, uomId = 1, unitCost = 1m } },
            });
            Assert.Equal(HttpStatusCode.Forbidden, receive.StatusCode);
        }

        // أمين المخزن يستلم ولا يعتمد الجرد ولا يسدد
        using (var storekeeper = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        {
            Assert.Equal(HttpStatusCode.OK, (await storekeeper.GetAsync("/api/v1/purchasing/purchase-orders")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.PostAsync("/api/v1/inventory/stocktakes/1/approve", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.PostAsJsonAsync("/api/v1/purchasing/supplier-payments", new
            {
                supplierId = 1, treasuryAccountId = _fx.TreasuryId, amount = 1m,
                paymentMethod = "CASH", paymentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            })).StatusCode);
        }

        // المحاسب يفوتر ويسدد ولا يستلم
        using (var accountant = await ClientAsync("g6.accountant", "Accountant#G6-Pass", _fx.BranchId))
        {
            Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/v1/purchasing/treasury-accounts")).StatusCode);
            var receive = await accountant.PostAsJsonAsync("/api/v1/purchasing/purchase-receipts", new
            {
                supplierId = 1, warehouseId = _fx.WarehouseId,
                lines = new object[] { new { skuId = 1, qty = 1m, uomId = 1, unitCost = 1m } },
            });
            Assert.Equal(HttpStatusCode.Forbidden, receive.StatusCode);
        }

        // المراجع يقرأ الأطراف ولا يديرها
        using (var auditor = await ClientAsync("g6.auditor", "Auditor#G6-Pass", _fx.BranchId))
        {
            Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/parties/customers")).StatusCode);
            var create = await auditor.PostAsJsonAsync("/api/v1/parties/customers", new
            {
                code = "G6X", nameAr = "محاولة مراجع", creditLimit = 0m, creditEnabled = false, paymentTermsDays = 0,
            });
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        // المجهول 401 على كل شيء
        using (var anonymous = _fx.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/inventory/balances")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/purchasing/suppliers")).StatusCode);
        }
    }

    // ═══ حواجز الصلاحيات العابرة للنطاقات ═════════════════════════════════

    [Fact]
    public async Task CrossBranchScope_TransferReceiveRequiresDestinationBranch()
    {
        var bagSku = await G6ApiFixture.GetSkuIdAsync("G6-SCN-01-BAG");
        using (var sender = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.BranchId))
        using (var supervisor = await ClientAsync("g6.supervisor", "Supervisor#G6-Pass", _fx.BranchId))
        {
            var transfer = await sender.PostAsJsonAsync("/api/v1/inventory/transfers", new
            {
                fromWarehouseId = _fx.Warehouse2Id, toWarehouseId = _fx.Warehouse3Id,   // W3 في فرع G6B2
                lines = new object[] { new { skuId = bagSku, qtyBase = 5m } },
            });
            Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
            var transferId = (await transfer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transferId").GetInt64();

            // الرصيد قد لا يكفي — اعتماد D-03 يجعل الإرسال ممكناً من أي حالة
            var request = await sender.PostAsJsonAsync("/api/v1/approvals", new
            {
                entityType = "inv.transfer", entityId = transferId, reasonAr = "نقل عبر الفروع",
            });
            var approvalId = (await request.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();
            await supervisor.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new { decision = "APPROVED", reasonAr = "مقبول" });
            var sent = await sender.PostAsJsonAsync($"/api/v1/inventory/transfers/{transferId}/send", new { approvalId });
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

            // مستخدم نطاقه فرع المصدر لا يستلم في مخزن الوجهة — الاستلام لفرع الوجهة (§11.1)
            var wrongScope = await sender.PostAsync($"/api/v1/inventory/transfers/{transferId}/receive", null);
            Assert.Equal(HttpStatusCode.Conflict, wrongScope.StatusCode);

            using (var receiver = await ClientAsync("g6.storekeeper", "Storekeeper#G6-Pass", _fx.Branch2Id))
            {
                var received = await receiver.PostAsync($"/api/v1/inventory/transfers/{transferId}/receive", null);
                Assert.Equal(HttpStatusCode.OK, received.StatusCode);
                // حركة الدخول توثق بفرع مخزن الوجهة
                var movementBranch = await ScalarAsync(
                    $"SELECT branch_id FROM inv.stock_movements WHERE movement_type='TRANSFER_IN' AND source_doc_id={transferId}");
                Assert.Equal(_fx.Branch2Id, movementBranch);
            }
        }
    }
}

// ═══ البذر الثابت للبوابة ═════════════════════════════════════════════════

public sealed class G6ApiFixture : IAsyncLifetime
{
    public WeraApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public long BranchId { get; private set; }
    public long Branch2Id { get; private set; }
    public long WarehouseId { get; private set; }         // G6W1 — فرع G6B1
    public long Warehouse2Id { get; private set; }        // G6W2 — فرع G6B1 (نفس الفرع)
    public long Warehouse3Id { get; private set; }        // G6W3 — فرع G6B2
    public long TreasuryId { get; private set; }

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient(); // يقلع ويرحّل حتى رأس V017
        await SeedAsync();
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task<Contracts.V1.LoginResponse> LoginAsync(string username, string password, long? branchId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password, branchId, stationId = (long?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contracts.V1.LoginResponse>())!;
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        void UpsertUser(string username, string display, string password, string roleCode)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
            Scalar($"INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('{username}', '{display}', '{hash}', true) ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash");
            var userId = Scalar($"SELECT user_id FROM core.users WHERE username = '{username}'");
            Scalar($"DELETE FROM core.user_roles WHERE user_id = {userId}");
            Scalar($"INSERT INTO core.user_roles (user_id, role_id, branch_id) VALUES ({userId}, (SELECT role_id FROM core.roles WHERE code='{roleCode}'), NULL)");
        }

        UpsertUser("g6.owner", "مالك G6", "Owner#G6-Pass", "OWNER");
        UpsertUser("g6.manager", "مدير G6", "Manager#G6-Pass", "MANAGER");
        UpsertUser("g6.storekeeper", "أمين مخزن G6", "Storekeeper#G6-Pass", "STOREKEEPER");
        UpsertUser("g6.supervisor", "مشرف G6", "Supervisor#G6-Pass", "SUPERVISOR");
        UpsertUser("g6.accountant", "محاسب G6", "Accountant#G6-Pass", "ACCOUNTANT");
        UpsertUser("g6.cashier", "كاشير G6", "Cashier#G6-Pass", "CASHIER");
        UpsertUser("g6.auditor", "مراجع G6", "Auditor#G6-Pass", "AUDITOR");

        BranchId = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G6B1', 'فرع اختبار G6 الرئيسي', 'Africa/Cairo') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING branch_id");
        Branch2Id = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G6B2', 'فرع اختبار G6 الثاني', 'Africa/Cairo') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING branch_id");
        WarehouseId = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({BranchId}, 'G6W1', 'مخزن G6 الرئيسي', 'BRANCH') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");
        Warehouse3Id = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({Branch2Id}, 'G6W3', 'مخزن G6 بفرع ثانٍ', 'SUB') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");
        Warehouse2Id = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({BranchId}, 'G6W2', 'مخزن G6 الفرعي', 'SUB') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");
        TreasuryId = Scalar("INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, currency, is_active) VALUES ('G6TREAS1', 'خزينة اختبار G6', 'SAFE', 'EGP', true) ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING treasury_account_id");

        var categoryId = Scalar("INSERT INTO catalog.categories (code, name_ar) VALUES ('G6CAT', 'فئة اختبار G6') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING category_id");
        var priceListId = Scalar("SELECT price_list_id FROM catalog.price_lists WHERE code='DEFAULT_EGP'");

        // منتج الأكياس والعلب (كيس COUNT + علبة PACK من 12) + منتج وزني بالكيلو
        var login = await LoginAsync("g6.owner", "Owner#G6-Pass");
        using var authed = CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var milk = CatalogEndpointTests.BagAndBoxPayload(
            "G6-SCN-01", "G6-SCN-01-BAG", "G6-SCN-01-BOX",
            "6221031492303", "6221031492310", categoryId, priceListId);
        var milkCreated = await authed.PostAsJsonAsync("/api/v1/catalog/products", milk);
        Assert.True(milkCreated.IsSuccessStatusCode, "بذر منتج الألبان فشل: " + await milkCreated.Content.ReadAsStringAsync());

        var cheese = new
        {
            code = "G6-CHEESE",
            nameAr = "جبنة رومي G6",
            categoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new
                {
                    skuCode = "G6-CHEESE-KG", saleMode = "WEIGHT", baseUomId = 2, saleUomId = 2, nameAr = "بالكيلو", reorderLevel = 0,
                    weight = new { weightPrecisionG = 1, weightStepG = 5, allowManualWeight = true, allowAmountTarget = true, minWeightKg = (decimal?)0.1m, maxWeightKg = (decimal?)5m, defaultTareKg = 0m },
                },
            },
            barcodes = new object[] { new { barcodeValue = "6291041500992", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId, unitId = 2, unitPrice = 189m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        var cheeseCreated = await authed.PostAsJsonAsync("/api/v1/catalog/products", cheese);
        Assert.True(cheeseCreated.IsSuccessStatusCode, "بذر منتج الجبنة فشل: " + await cheeseCreated.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        // بذور G6 تبقى للفحوص اللاحقة — لا تنظيف يقتل بذور بوابات أخرى (نمط G4/G5)
    }

    public static async Task<long> GetSkuIdAsync(string skuCode)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT sku_id FROM catalog.skus WHERE sku_code = @c", conn);
        cmd.Parameters.AddWithValue("c", skuCode);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task<long> GetWarehouseIdAsync(string code)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT warehouse_id FROM inv.warehouses WHERE code = @c", conn);
        cmd.Parameters.AddWithValue("c", code);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static long SupplierDbId()
    {
        using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        conn.Open();
        using var cmd = new NpgsqlCommand("SELECT supplier_id FROM purch.suppliers WHERE code='G6SUP1'", conn);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
