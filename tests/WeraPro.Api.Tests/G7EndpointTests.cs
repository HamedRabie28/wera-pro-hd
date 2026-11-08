namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

/// <summary>
/// اختبارات بوابة G7 (§22): نقطة البيع — المبيعات والوزن والدفع والمرتجعات.
/// السيناريوهات الإلزامية الثلاثة عشر بالأرقام المرجعية المستقلة + RBAC +
/// منع التكرار + D-03 بالوجهين + توجيه الإيصال §29.4.
/// </summary>
public sealed class G7EndpointTests : IClassFixture<G7ApiFixture>
{
    private readonly G7ApiFixture _fx;

    public G7EndpointTests(G7ApiFixture fx) => _fx = fx;

    private async Task<HttpClient> ClientAsync(string username, string password, bool withStation = true, long? branchId = null)
    {
        var login = await _fx.LoginAsync(username, password, branchId ?? _fx.BranchId, withStation ? _fx.StationId : null);
        var client = _fx.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static async Task<decimal> ScalarDecimalAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0m : Convert.ToDecimal(result);
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static object CashSaleLine(long skuId, decimal qty) => new { skuId, qty };
    /// <summary>يقرأ استجابة 201 ويفشع محتواها في رسالة الاختبار — تسريع تشخيص أي انحراف.</summary>
    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"STATUS={(int)response.StatusCode} BODY={body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static object CashPayment(decimal amount, decimal? tendered = null) =>
        tendered is null ? new { method = "CASH", amount } : new { method = "CASH", amount, cashTendered = tendered };

    // ═══ 1) بيع عددي نقدي — بالأرقام المرجعية المستقلة (§15.5) ═════════════════

    [Fact]
    public async Task T01_MandatoryNumericCashSale_IndependentNumbers()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        // 3 أكياس × 5.00 = 15.00 شامل 14% → ضريبة 1.84 · صافي 13.16 (مرجع مستقل)
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 3m) },
            payments = new[] { CashPayment(15.00m, 20.00m) },
            cashTendered = 20.00m,
            idempotencyKey = $"g7-t01-{Guid.NewGuid():N}",
        });
        Assert.True(sale.StatusCode == HttpStatusCode.Created,
            $"STATUS={sale.StatusCode} BODY={await sale.Content.ReadAsStringAsync()}");
        var body = await sale.Content.ReadFromJsonAsync<JsonElement>();
        var invoiceId = body.GetProperty("invoiceId").GetInt64();

        Assert.Equal(15.00m, body.GetProperty("totals").GetProperty("dueAmount").GetDecimal());
        Assert.Equal(1.84m, body.GetProperty("totals").GetProperty("taxTotal").GetDecimal());
        Assert.Equal(13.16m, body.GetProperty("totals").GetProperty("netTotal").GetDecimal());
        Assert.Equal(5.00m, body.GetProperty("totals").GetProperty("changeDue").GetDecimal()); // الفكة تميز عن المستحق

        // الدفتر: حركة SALE واحدة بالاتجاه −1 على صنف الكيس (القيمة موجبة والاتجاه سالب)
        Assert.Equal(-3m, await ScalarDecimalAsync(
            $"SELECT SUM(base_qty * direction) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));
        // أثر خزينة واحد: +15 على درج المحطة
        Assert.Equal(15.00m, await ScalarDecimalAsync(
            $"SELECT SUM(amount) FROM finance.treasury_transactions WHERE source_doc_type='sales.payment' AND source_doc_id IN (SELECT payment_id FROM sales.payments WHERE invoice_id={invoiceId})"));
        // حدث درج للبيع النقدي — بلا استجابة جهاز (B-01: UNKNOWN صادقة) — محصور بهذه الفاتورة
        var invoiceNumber = body.GetProperty("invoiceNumber").GetString();
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT COUNT(*) FROM sales.drawer_events WHERE event_type='OPENED_BY_SALE' AND reason = 'فتح بالبيع {invoiceNumber}'"));
    }

    // ═══ 2) البطاقة عبر Sandbox موثق (اختبار إلزامي — بتصريح B-02 الصادق) ═══════

    [Fact]
    public async Task T02_MandatoryCardSale_DocumentedSandboxSimulator()
    {
        using var owner = await ClientAsync("g7.owner", "Owner#G7-Pass");
        // (أ) الحالة الافتراضية NOT_CONFIGURED: رفض صريح بلا ادعاء مزود (B-02/D-11)
        await using (var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE core.business_settings SET value_text='NOT_CONFIGURED' WHERE setting_key='payments.card_provider'", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var blocked = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 1m) },
            payments = new[] { new { method = "CARD", amount = 5.00m } },
            idempotencyKey = $"g7-t02a-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var blockedBody = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CARD_PROVIDER_NOT_CONFIGURED", blockedBody.GetProperty("code").GetString());

        // (ب) المحاكاة الموسومة SANDBOX_SIMULATOR: مسار كامل بمرجع SBX — لا ادعاء مزود حقيقي
        await using (var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE core.business_settings SET value_text='SANDBOX_SIMULATOR' WHERE setting_key='payments.card_provider'", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var sale = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 2m) },
            payments = new[] { new { method = "CARD", amount = 10.00m } },
            idempotencyKey = $"g7-t02b-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();

        // المزود موسوم بوضوح كمحاكاة + مرجع SBX (§9.6: المحاكاة معلّمة بوضوح)
        var provider = await ScalarDecimalAsync($"SELECT COUNT(*) FROM sales.payments WHERE invoice_id={invoiceId} AND provider_name='WeraSandbox-SIMULATED' AND provider_reference LIKE 'SBX-%'");
        Assert.Equal(1m, provider);
        // البطاقة أثرها على حساب المزود لا الدرج النقدي
        Assert.Equal(0m, await ScalarDecimalAsync(
            $"SELECT COUNT(*) FROM finance.treasury_transactions t JOIN finance.treasury_accounts a ON a.treasury_account_id=t.treasury_account_id WHERE t.source_doc_type='sales.payment' AND t.source_doc_id IN (SELECT payment_id FROM sales.payments WHERE invoice_id={invoiceId}) AND a.account_kind='CASH_DRAWER'"));
    }

    // ═══ 3) الدفع المختلط Split ═════════════════════════════════════════════════

    [Fact]
    public async Task T03_MandatorySplitPayment_CashPlusCard()
    {
        using var owner = await ClientAsync("g7.owner", "Owner#G7-Pass");
        var sale = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[]
            {
                CashSaleLine(_fx.BagSkuId, 2m),   // 10.00
                CashSaleLine(_fx.BoxSkuId, 1m),   // 55.00 → المستحق 65.00
            },
            payments = new[]
            {
                new { method = "CASH", amount = 25.00m },
                new { method = "CARD", amount = 40.00m },
            },
            idempotencyKey = $"g7-t03-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();
        Assert.Equal(65.00m, body.GetProperty("totals").GetProperty("dueAmount").GetDecimal());

        // دفعتان + أثرا خزينة منفصلان (درج نقدي + حساب مزود)
        Assert.Equal(2L, await ScalarAsync($"SELECT COUNT(*) FROM sales.payments WHERE invoice_id={invoiceId}"));
        Assert.Equal(2L, await ScalarAsync(
            $"SELECT COUNT(*) FROM finance.treasury_transactions WHERE source_doc_type='sales.payment' AND source_doc_id IN (SELECT payment_id FROM sales.payments WHERE invoice_id={invoiceId})"));

        // نقص المجموع لا يعلن الفاتورة مدفوعة (§9.6)
        var shortPaid = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 1m) },
            payments = new[] { new { method = "CASH", amount = 3.00m } },
            idempotencyKey = $"g7-t03b-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, shortPaid.StatusCode);
        Assert.Equal("PAYMENT_TOTAL_MISMATCH", (await shortPaid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ═══ 4) البيع الآجل وحد الائتمان (D-05) ═══════════════════════════════════

    [Fact]
    public async Task T04_MandatoryCreditSale_WithinAndOverLimit()
    {
        using var owner = await ClientAsync("g7.owner", "Owner#G7-Pass");
        var outstandingBefore = await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(p.amount),0) FROM sales.payments p JOIN sales.invoices i ON i.invoice_id=p.invoice_id WHERE i.customer_id={_fx.CreditCustomerId} AND p.payment_method='CREDIT' AND p.status IN ('CAPTURED','SETTLED')");
        // ضمن الحد: المستحق القائم + 20 ≤ 500
        var ok = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 4m) },
            customerId = _fx.CreditCustomerId,
            payments = new[] { new { method = "CREDIT", amount = 20.00m } },
            idempotencyKey = $"g7-t04a-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(ok);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();

        // الآجل ذمة لا نقدية: لا أثر خزينة لدفعات CREDIT
        Assert.Equal(0m, await ScalarDecimalAsync(
            $"SELECT COUNT(*) FROM finance.treasury_transactions WHERE source_doc_type='sales.payment' AND source_doc_id IN (SELECT payment_id FROM sales.payments WHERE invoice_id={invoiceId})"));
        // الرصيد القائم زاد 20.00 بالضبط (من الدفتر — لا رقم مخزن)
        Assert.Equal(outstandingBefore + 20.00m, await ScalarDecimalAsync(
            $"SELECT COALESCE(SUM(p.amount),0) FROM sales.payments p JOIN sales.invoices i ON i.invoice_id=p.invoice_id WHERE i.customer_id={_fx.CreditCustomerId} AND p.payment_method='CREDIT' AND p.status IN ('CAPTURED','SETTLED')"));

        // فوق الحد (الحد 500): رفض صريح لا تخفيض صامت — نبيع 492 كيساً = 2460 > 500
        var over = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 492m) },
            customerId = _fx.CreditCustomerId,
            payments = new[] { new { method = "CREDIT", amount = 2460.00m } },
            idempotencyKey = $"g7-t04b-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // بلا عميل: رفض (الآجل مرتبط بعميل §9.4)
        var noCustomer = await owner.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 1m) },
            payments = new[] { new { method = "CREDIT", amount = 5.00m } },
            idempotencyKey = $"g7-t04c-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, noCustomer.StatusCode);
    }

    // ═══ 5) المبلغ المستهدف: 200 ج.م/كجم × 70 = 350 جم (اختبار البوابة الحرفي) ═══

    [Fact]
    public async Task T05_MandatoryAmountTarget_200PerKg70Egp_350Grams()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[]
            {
                new { skuId = _fx.CheeseSkuId, weightSource = "AMOUNT_DERIVED", amountTarget = 70.00m },
            },
            payments = new[] { CashPayment(70.00m) },
            idempotencyKey = $"g7-t05-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();

        // السطر: 0.350 كجم بمصدر AMOUNT_DERIVED والمبلغ المستهدف 70 محفوظين
        var line = await ScalarDecimalAsync(
            $"SELECT weight_kg FROM sales.invoice_lines WHERE invoice_id={invoiceId} AND sale_mode='WEIGHT'");
        Assert.Equal(0.350m, line);
        Assert.Equal(70.00m, await ScalarDecimalAsync($"SELECT amount_target FROM sales.invoice_lines WHERE invoice_id={invoiceId}"));
        Assert.Equal(70.00m, body.GetProperty("totals").GetProperty("dueAmount").GetDecimal()); // على الخطوة بلا تعديل

        // المخزون: 0.350 كجم خصمت من الوحدة الأساسية (الكيلوجرام — baseUomId=2)
        Assert.Equal(-0.350m, await ScalarDecimalAsync(
            $"SELECT SUM(base_qty * direction) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));
    }

    // ═══ 6) وزن 1.275 كجم: الحساب والمخزون ═════════════════════════════════════

    [Fact]
    public async Task T06_MandatoryWeight1275Kg_ComputeAndStock()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[]
            {
                new { skuId = _fx.CheeseSkuId, qty = 1.275m, weightKg = 1.275m, weightSource = "MANUAL_WEIGHT" },
            },
            payments = new[] { CashPayment(255.00m) },
            idempotencyKey = $"g7-t06-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();

        // 1.275 × 200 = 255.00 (مرجع مستقل)
        Assert.Equal(255.00m, body.GetProperty("totals").GetProperty("dueAmount").GetDecimal());
        Assert.Equal(1.275m, await ScalarDecimalAsync($"SELECT weight_kg FROM sales.invoice_lines WHERE invoice_id={invoiceId}"));
        Assert.Equal(-1.275m, await ScalarDecimalAsync(
            $"SELECT SUM(base_qty * direction) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));
        await using (var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT weight_source FROM sales.invoice_lines WHERE invoice_id={invoiceId}", conn);
            Assert.Equal("MANUAL_WEIGHT", (string)(await cmd.ExecuteScalarAsync() ?? string.Empty));
        }
    }

    // ═══ 7) قراءة الميزان: مستقرة/غير مستقرة/قديمة/منقطعة (الأربع خادمياً) ═════

    [Fact]
    public async Task T07_MandatoryScaleStates_FourFaces()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");

        async Task<JsonElement> Preview(object payload)
        {
            var response = await cashier.PostAsJsonAsync("/api/v1/pos/weight/preview", payload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var now = DateTimeOffset.UtcNow;
        // مستقرة + حديثة → مقبولة 1.275
        var stable = await Preview(new
        {
            skuId = _fx.CheeseSkuId,
            mode = "SCALE_READING",
            scaleReadingKg = 1.275m,
            scaleStable = true,
            scaleConnected = true,
            scaleReadAtUtc = now,
        });
        Assert.True(stable.GetProperty("accepted").GetBoolean());
        Assert.Equal(1.275m, stable.GetProperty("weightKg").GetDecimal());
        Assert.Equal(255.00m, stable.GetProperty("amount").GetDecimal());

        // غير مستقرة → SCALE_UNSTABLE
        var unstable = await Preview(new
        {
            skuId = _fx.CheeseSkuId,
            mode = "SCALE_READING",
            scaleReadingKg = 1.275m,
            scaleStable = false,
            scaleConnected = true,
            scaleReadAtUtc = now,
        });
        Assert.False(unstable.GetProperty("accepted").GetBoolean());
        Assert.Equal("SCALE_UNSTABLE", unstable.GetProperty("errorCode").GetString());

        // قديمة → SCALE_READING_STALE
        var stale = await Preview(new
        {
            skuId = _fx.CheeseSkuId,
            mode = "SCALE_READING",
            scaleReadingKg = 1.275m,
            scaleStable = true,
            scaleConnected = true,
            scaleReadAtUtc = now.AddSeconds(-60),
        });
        Assert.False(stale.GetProperty("accepted").GetBoolean());
        Assert.Equal("SCALE_READING_STALE", stale.GetProperty("errorCode").GetString());

        // منقطعة → SCALE_DISCONNECTED
        var disconnected = await Preview(new
        {
            skuId = _fx.CheeseSkuId,
            mode = "SCALE_READING",
            scaleReadingKg = 1.275m,
            scaleStable = true,
            scaleConnected = false,
            scaleReadAtUtc = now,
        });
        Assert.False(disconnected.GetProperty("accepted").GetBoolean());
        Assert.Equal("SCALE_DISCONNECTED", disconnected.GetProperty("errorCode").GetString());
    }

    // ═══ 8) باركود وزن صالح وخاطئ (§5.3 — لا تخمين) ════════════════════════════

    [Fact]
    public async Task T08_MandatoryWeightBarcode_ValidAndInvalid()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");

        // صالح: 21 + PLU 4071 + وزن 00350 (3 منازل بالكيلوجرام) = 0.350 كجم
        var valid = await cashier.PostAsJsonAsync("/api/v1/pos/lookup", new { code = _fx.ValidWeightBarcode });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var validBody = await valid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(validBody.GetProperty("found").GetBoolean(), $"LOOKUP-BODY={validBody}");
        Assert.True(validBody.GetProperty("isWeightLabel").GetBoolean());
        Assert.Equal(0.350m, validBody.GetProperty("labelWeightKg").GetDecimal());
        Assert.Equal(_fx.CheeseSkuId, validBody.GetProperty("item").GetProperty("skuId").GetInt64());

        // خاطئ: بادئة غير معروفة → رفض موثق (UNKNOWN_WEIGHT_BARCODE_FORMAT عبر البنية)
        var invalid = await cashier.PostAsJsonAsync("/api/v1/pos/lookup", new { code = "99407100350" });
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(invalidBody.GetProperty("found").GetBoolean());
        Assert.False(invalidBody.GetProperty("isWeightLabel").GetBoolean());
        Assert.Contains("لا صنف", invalidBody.GetProperty("errorAr").GetString());

        // بنية صحيحة لكن PLU غير مرتبط بصنف → رفض موثق لا تخمين
        var unknownPlu = await cashier.PostAsJsonAsync("/api/v1/pos/lookup", new { code = "219999003500" });
        var unknownPluBody = await unknownPlu.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(unknownPluBody.GetProperty("found").GetBoolean());
        Assert.Contains("PLU 9999", unknownPluBody.GetProperty("errorAr").GetString());
    }

    // ═══ 9) بيع عبوة كاملة ومفردة (§4.3 — أثر واحد على المحتوى) ════════════════

    [Fact]
    public async Task T09_MandatoryPackageAndSingleSale()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[]
            {
                new { skuId = _fx.BoxSkuId, qty = 1m },   // علبة واحدة (12 كيساً)
                new { skuId = _fx.BagSkuId, qty = 1m },   // كيس مفرد
            },
            payments = new[] { CashPayment(60.00m) },       // 55 + 5 (سعران مستقلان)
            idempotencyKey = $"g7-t09-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();
        Assert.Equal(60.00m, body.GetProperty("totals").GetProperty("dueAmount").GetDecimal());

        // حركتان فقط — كلتاهما على صنف المحتوى (الكيس): 12 + 1، وصفر حركات على SKU العلبة
        Assert.Equal(2L, await ScalarAsync(
            $"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));
        Assert.Equal(-13m, await ScalarDecimalAsync(
            $"SELECT SUM(base_qty * direction) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));
        var bagSkuId = _fx.BagSkuId;
        Assert.Equal(2L, await ScalarAsync(
            $"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId} AND sku_id={bagSkuId}"));
        Assert.Equal(0L, await ScalarAsync(
            $"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId} AND sku_id={_fx.BoxSkuId}"));
    }

    // ═══ 10) تعليق واستعادة مسودة (لا بيع ولا حركة) ════════════════════════════

    [Fact]
    public async Task T10_MandatoryHoldAndRestore_NoMovementUntilPost()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var movementsBefore = await ScalarAsync("SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type LIKE 'sales.%'");

        var hold = await cashier.PostAsJsonAsync("/api/v1/pos/carts/hold", new
        {
            noteAr = "عميل نسي محفظته",
            cartJson = """{"lines":[{"skuId":1,"qty":2}]}""",
        });
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
        var cartId = (await hold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cartId").GetInt64();

        // التعليق والاستعادة لا ينشئان حركة مخزون أو فاتورة (§9.5)
        var restore = await cashier.PostAsJsonAsync($"/api/v1/pos/carts/{cartId}/restore", (object?)null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(movementsBefore, await ScalarAsync("SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type LIKE 'sales.%'"));

        // الاستعادة الثانية مرفوضة (لا مسودتان متطابقتان لبيع مكرر)
        var restoreAgain = await cashier.PostAsJsonAsync($"/api/v1/pos/carts/{cartId}/restore", (object?)null);
        Assert.Equal(HttpStatusCode.Conflict, restoreAgain.StatusCode);

        // بعد الاستعادة: البيع يرحّل مرة واحدة
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 2m) },
            payments = new[] { CashPayment(10.00m) },
            idempotencyKey = $"g7-t10-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
        Assert.Equal(movementsBefore + 1, await ScalarAsync("SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type LIKE 'sales.%'"));
    }

    // ═══ 11) الطباعة وفشلها بعد نجاح البيع دون تكرار (§29.9) ═══════════════════

    [Fact]
    public async Task T11_MandatoryPrintFailureAfterSale_NoDuplication()
    {
        // حتمية الاختبار: أعد الطابعة لحالة «غير معايرة» أياً كانت حالة قاعدة سابقة
        await using (var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                UPDATE print.printer_profiles SET is_calibrated = false, verified_media_width_mm = NULL
                 WHERE station_id = {_fx.StationId};
                UPDATE print.printer_document_roles SET is_auto_routing_enabled = false
                 WHERE station_id = {_fx.StationId} AND document_role = 'CUSTOMER_RECEIPT';", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        // طابعة معينة لكنها غير معايرة → مهمة HELD لا فشل صامت (§29.4 HOLD)
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 1m) },
            payments = new[] { CashPayment(5.00m) },
            idempotencyKey = $"g7-t11-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();
        var receipt = body.GetProperty("receipt");
        Assert.True(receipt.GetProperty("jobCreated").GetBoolean());
        Assert.Equal("HELD_CONFIGURATION_MISMATCH", receipt.GetProperty("jobStatus").GetString());

        // البيع سليم رغم تعليق الطباعة — لا مستند مالي جديد ولا حركة إضافية
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM sales.invoices WHERE invoice_id={invoiceId}"));
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));

        // إعادة الطباعة: مهمة جديدة REPRINT مرتبطة بالأصل — بلا أثر مالي (§29.9)
        var reprint = await cashier.PostAsJsonAsync($"/api/v1/pos/sales/{invoiceId}/print", new { copyType = "REPRINT" });
        Assert.Equal(HttpStatusCode.OK, reprint.StatusCode);
        var reprintBody = await reprint.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REPRINT", reprintBody.GetProperty("copyType").GetString());
        Assert.Equal(receipt.GetProperty("jobId").GetInt64(), reprintBody.GetProperty("relatedReprintOf").GetInt64());

        // مفتاح Idempotency للمهمة يمنع تكرارها لنفس الفاتورة/النسخة
        var again = await cashier.PostAsJsonAsync($"/api/v1/pos/sales/{invoiceId}/print", new { copyType = "CUSTOMER" });
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(againBody.TryGetProperty("jobId", out var againJobId),
            $"AGAIN-BODY={againBody.GetRawText()} RECEIPT={receipt.GetRawText()}");
        Assert.Equal(receipt.GetProperty("jobId").GetInt64(), againJobId.GetInt64());
        Assert.Equal(2L, await ScalarAsync($"SELECT COUNT(*) FROM print.print_jobs WHERE document_id={invoiceId} AND document_type='CUSTOMER_RECEIPT'"));

        // ما زال مستنداً مالياً واحداً
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM sales.invoices WHERE invoice_id={invoiceId}"));
    }

    // ═══ 12) مرتجع جزئي/كامل واستبدال (D-04 — اعتماد مشرف ووسيلة أصلية) ════════

    [Fact]
    public async Task T12_MandatoryPartialFullReturnAndExchange()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        using var supervisor = await ClientAsync("g7.supervisor", "Supervisor#G7-Pass");

        // فاتورة أصلية: 3 أكياس نقداً
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 3m) },
            payments = new[] { CashPayment(15.00m) },
            idempotencyKey = $"g7-t12-{Guid.NewGuid():N}",
        });
        var saleBody = await sale.Content.ReadFromJsonAsync<JsonElement>();
        var invoiceId = saleBody.GetProperty("invoiceId").GetInt64();
        var lineId = await ScalarAsync($"SELECT line_id FROM sales.invoice_lines WHERE invoice_id={invoiceId} LIMIT 1");

        // طلب اعتماد من الكاشير (الطالب) وقرار من المشرف (≠ الطالب — فصل واجبات)
        var approval = await cashier.PostAsJsonAsync("/api/v1/approvals", new
        {
            entityType = "sales.refund",
            entityId = invoiceId,
            reasonAr = "عميل أعاد كيساً تالفاً",
        });
        Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
        var approvalId = (await approval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();
        var decision = await supervisor.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new
        {
            decision = "APPROVED",
            reasonAr = "موافقة على المرتجع الجزئي",
        });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        // مرتجع جزئي: كيس واحد نقداً (بوسيلة الدفع الأصلية — D-04)
        var partial = await supervisor.PostAsJsonAsync("/api/v1/pos/refunds", new
        {
            originalInvoiceId = invoiceId,
            lines = new[] { new { originalLineId = lineId, qty = 1m } },
            refundMethod = "CASH",
            restock = true,
            reasonAr = "كيس تالف — إعادة للمخزون",
            approvalId,
            idempotencyKey = $"g7-t12r-{Guid.NewGuid():N}",
        });
        var partialBody = await CreatedAsync(partial);
        Assert.Equal(5.00m, partialBody.GetProperty("total").GetDecimal());
        Assert.Equal("PARTIAL_RETURN", partialBody.GetProperty("invoiceStatusAfter").GetString());

        // أثر واحد: حركة SALE_RETURN +1 ورد نقدي −5 من الدرج
        Assert.Equal(1m, await ScalarDecimalAsync(
            $"SELECT SUM(base_qty * direction) FROM inv.stock_movements WHERE source_doc_type='sales.refund' AND source_doc_id={partialBody.GetProperty("refundId").GetInt64()}"));
        Assert.Equal(-5.00m, await ScalarDecimalAsync(
            $"SELECT SUM(amount * direction) FROM finance.treasury_transactions WHERE source_doc_type='sales.refund' AND source_doc_id={partialBody.GetProperty("refundId").GetInt64()}"));

        // رد أكثر من المتبقي مرفوض (REFUND_EXCEEDS_SOLD — قيد V005)
        var over = await supervisor.PostAsJsonAsync("/api/v1/pos/refunds", new
        {
            originalInvoiceId = invoiceId,
            lines = new[] { new { originalLineId = lineId, qty = 3m } },
            refundMethod = "CASH",
            restock = true,
            reasonAr = "محاولة تجاوز",
            approvalId,
            idempotencyKey = $"g7-t12o-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);

        // بطاقة على فاتورة نقدية: رفض (البطاقة للبطاقة — D-04)
        var cardMismatch = await supervisor.PostAsJsonAsync("/api/v1/pos/refunds", new
        {
            originalInvoiceId = invoiceId,
            lines = new[] { new { originalLineId = lineId, qty = 1m } },
            refundMethod = "CARD",
            restock = true,
            reasonAr = "محاولة رد بطاقة",
            approvalId,
            idempotencyKey = $"g7-t12c-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, cardMismatch.StatusCode);
        Assert.Equal("REFUND_METHOD_MISMATCH", (await cardMismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // مرتجع كامل للباقي → FULL_RETURN
        var approval2 = await cashier.PostAsJsonAsync("/api/v1/approvals", new
        {
            entityType = "sales.refund",
            entityId = invoiceId,
            reasonAr = "استرجاع كامل",
        });
        var approval2Id = (await approval2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();
        await supervisor.PostAsJsonAsync($"/api/v1/approvals/{approval2Id}/decision", new
        {
            decision = "APPROVED",
            reasonAr = "موافقة الاسترجاع الكامل",
        });
        var full = await supervisor.PostAsJsonAsync("/api/v1/pos/refunds", new
        {
            originalInvoiceId = invoiceId,
            lines = new[] { new { originalLineId = lineId, qty = 2m } },
            refundMethod = "CASH",
            restock = true,
            reasonAr = "استرجاع الكمية المتبقية",
            approvalId = approval2Id,
            idempotencyKey = $"g7-t12f-{Guid.NewGuid():N}",
        });
        var fullBody = await full.Content.ReadAsStringAsync();
        Assert.True(full.StatusCode == HttpStatusCode.Created, $"FULL-BODY={fullBody}");
        Assert.Equal("FULL_RETURN", JsonDocument.Parse(fullBody).RootElement.GetProperty("invoiceStatusAfter").GetString());

        // الاستبدال = مرتجع + بيع جديد بالفرق: بيع جديد بعد الاسترجاع
        var exchange = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BoxSkuId, 1m) },
            payments = new[] { CashPayment(55.00m) },
            idempotencyKey = $"g7-t12e-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Created, exchange.StatusCode);
    }

    // ═══ 13) الضغط: 1/100/1000 سطر ومسح متتابع ═════════════════════════════════

    [Fact]
    public async Task T13_MandatoryStress_1_100_1000LinesAndSequentialScans()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");

        foreach (var count in new[] { 1, 100, 1000 })
        {
            var lines = Enumerable.Range(0, count)
                .Select(i => CashSaleLine(_fx.BagSkuId, 1m))
                .ToArray();
            var due = count * 5.00m;
            var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
            {
                lines,
                payments = new[] { CashPayment(due) },
                idempotencyKey = $"g7-t13-{count}-{Guid.NewGuid():N}",
            });
            var body = await CreatedAsync(sale);
            var invoiceId = body.GetProperty("invoiceId").GetInt64();
            Assert.Equal(count, await ScalarAsync($"SELECT COUNT(*) FROM sales.invoice_lines WHERE invoice_id={invoiceId}"));
            Assert.Equal(due, body.GetProperty("totals").GetProperty("dueAmount").GetDecimal());
        }

        // المسح المتتابع: 50 التقاطاً متتابعاً بالباركود Exact Match
        for (var i = 0; i < 50; i++)
        {
            var lookup = await cashier.PostAsJsonAsync("/api/v1/pos/lookup", new { code = _fx.BagBarcode });
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            var lookupBody = await lookup.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(lookupBody.GetProperty("found").GetBoolean());
        }
    }

    // ═══ منع التكرار: إعادة الإرسال بمفتاح Idempotency تعيد الأصل (§15.4/§11.3) ═

    [Fact]
    public async Task T14_IdempotencyRetry_ReturnsOriginal_NoDuplicateSalePaymentOrStock()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var key = $"g7-t14-{Guid.NewGuid():N}";
        var payload = new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 2m) },
            payments = new[] { CashPayment(10.00m) },
            idempotencyKey = key,
        };
        var first = await cashier.PostAsJsonAsync("/api/v1/pos/sales", payload);
        var firstBody = await CreatedAsync(first);
        var invoiceId = firstBody.GetProperty("invoiceId").GetInt64();
        Assert.False(firstBody.GetProperty("idempotentReplay").GetBoolean());

        // إعادة الإرسال بعد timeout: 200 بالفاتورة الأصلية — لا بيع/دفع/خصم مزدوج
        var retry = await cashier.PostAsJsonAsync("/api/v1/pos/sales", payload);
        var retryBody = await CreatedAsync(retry);
        Assert.Equal(invoiceId, retryBody.GetProperty("invoiceId").GetInt64());
        Assert.True(retryBody.GetProperty("idempotentReplay").GetBoolean());

        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM sales.invoices WHERE idempotency_key='{key}'"));
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId}"));
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM sales.payments WHERE invoice_id={invoiceId}"));
        Assert.Equal(10.00m, await ScalarDecimalAsync(
            $"SELECT SUM(amount) FROM finance.treasury_transactions WHERE source_doc_type='sales.payment' AND source_doc_id IN (SELECT payment_id FROM sales.payments WHERE invoice_id={invoiceId})"));
    }

    // ═══ RBAC خادمي: مصفوفة V009 كما هي ═════════════════════════════════════════

    [Fact]
    public async Task T15_Rbac_ServerEnforcedMatrix()
    {
        // الكاشير: يبيع ويعلق ويفتح الدرج — ولا يعتمد مرتجعاً ولا يعدل سعراً ولا يبيع آجلاً
        using (var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass"))
        {
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/pos/context")).StatusCode);

            var refund = await cashier.PostAsJsonAsync("/api/v1/pos/refunds", new
            {
                originalInvoiceId = 1,
                lines = new[] { new { originalLineId = 1, qty = 1m } },
                refundMethod = "CASH",
                restock = true,
                reasonAr = "بدون صلاحية",
                approvalId = 1,
            });
            Assert.Equal(HttpStatusCode.Forbidden, refund.StatusCode);

            var overrideSale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
            {
                lines = new[] { new { skuId = _fx.BagSkuId, qty = 1m, priceOverride = 3.00m, priceOverrideReason = "تخفيض" } },
                payments = new[] { CashPayment(3.00m) },
                idempotencyKey = $"g7-t15a-{Guid.NewGuid():N}",
            });
            Assert.Equal(HttpStatusCode.Conflict, overrideSale.StatusCode);
            Assert.Equal("PRICE_OVERRIDE_DENIED", (await overrideSale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            // خصم 8% يتجاوز حد الكاشير 5% (D-06)
            var discountSale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
            {
                lines = new[] { new { skuId = _fx.BagSkuId, qty = 10m, lineDiscount = 0.80m } }, // 0.80/50 = 1.6% داخل حد الكاشير
                payments = new[] { CashPayment(49.20m) },
                idempotencyKey = $"g7-t15b-{Guid.NewGuid():N}",
            });
            // 0.80 على 50.00 = 1.6% داخل الحد — ثم نختبر 8% فعلياً
            Assert.Equal(HttpStatusCode.Created, discountSale.StatusCode);
            var heavyDiscount = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
            {
                lines = new[] { new { skuId = _fx.BagSkuId, qty = 10m, lineDiscount = 4.20m } }, // 4.20/50 = 8.4% > 5%
                payments = new[] { CashPayment(208.00m) },
                idempotencyKey = $"g7-t15c-{Guid.NewGuid():N}",
            });
            Assert.Equal(HttpStatusCode.Conflict, heavyDiscount.StatusCode);
            Assert.Equal("DISCOUNT_PERMISSION_DENIED", (await heavyDiscount.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // المراجع وأمين المخزن: لا pos.use إطلاقاً (لا قائمة إدارية للكاشير ولا العكس)
        using (var auditor = await ClientAsync("g7.auditor", "Auditor#G7-Pass", withStation: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/v1/pos/context")).StatusCode);
        }
        using (var storekeeper = await ClientAsync("g7.storekeeper", "Storekeeper#G7-Pass"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.GetAsync("/api/v1/pos/context")).StatusCode);
        }

        // المجهول: 401 على كل شيء
        using var anonymous = _fx.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/pos/context")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/pos/sales", new { })).StatusCode);
    }

    // ═══ D-03 بالوجهين على البيع: بلا اعتماد يرفض وباعتماد يمر مرتبطاً ══════════

    [Fact]
    public async Task T16_NegativeStockSale_D03BothFaces()
    {
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        using var supervisor = await ClientAsync("g7.supervisor", "Supervisor#G7-Pass");

        // صنف محدود المخزون (5 وحدات فقط) — نبيع 50: بلا اعتماد يرفض وباعتماد يمر
        var withoutApproval = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.LowSkuId, 50m) },
            payments = new[] { CashPayment(250.00m) },
            idempotencyKey = $"g7-t16a-{Guid.NewGuid():N}",
        });
        Assert.Equal(HttpStatusCode.Conflict, withoutApproval.StatusCode);
        Assert.Equal("NEGATIVE_STOCK_NEEDS_APPROVAL", (await withoutApproval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // اعتماد مشرف (الطالب كاشير والمعتمد مشرف — فصل واجبات)
        var approval = await cashier.PostAsJsonAsync("/api/v1/approvals", new
        {
            entityType = "sales.invoice",
            entityId = 0,
            reasonAr = "بيع بمخزون سالب بموافقة مشرف (D-03)",
        });
        var approvalId = (await approval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetInt64();
        await supervisor.PostAsJsonAsync($"/api/v1/approvals/{approvalId}/decision", new
        {
            decision = "APPROVED",
            reasonAr = "موافق على البيع السالب",
        });

        var withApproval = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.LowSkuId, 50m) },
            payments = new[] { CashPayment(250.00m) },
            approvalId,
            idempotencyKey = $"g7-t16b-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(withApproval);
        var invoiceId = body.GetProperty("invoiceId").GetInt64();

        // الرصيد سالب والحركة تحمل الاعتماد — موثقة لا مخفية
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id={invoiceId} AND approval_id={approvalId}"));
    }

    // ═══ توجيه الإيصال §29.4: معاير 80 → قالب 80 تلقائياً — ولا A4 أبداً ════════

    [Fact]
    public async Task T17_ReceiptRouting_VerifiedPrinter_RoutesRollTemplateNeverA4()
    {
        // معايرة طابعة المحطة (G10 يقوم بالمعايرة الفعلية — هنا نثبت سلوك التوجيه)
        await using (var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                UPDATE print.printer_profiles
                   SET is_calibrated = true, verified_media_width_mm = 72.0, last_test_at = now(), last_test_result = 'PASS'
                 WHERE printer_profile_id = {_fx.PrinterProfileId};
                UPDATE print.printer_document_roles SET is_auto_routing_enabled = true
                 WHERE station_id = {_fx.StationId} AND document_role = 'CUSTOMER_RECEIPT';", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 1m) },
            payments = new[] { CashPayment(5.00m) },
            idempotencyKey = $"g7-t17-{Guid.NewGuid():N}",
        });
        var body = await CreatedAsync(sale);
        var receipt = body.GetProperty("receipt");

        // قالب 80 المستقل تلقائياً — لا Print Dialog ولا تحويل لـ58/A4 (§29.4)
        Assert.True(receipt.GetProperty("jobCreated").GetBoolean());
        Assert.Equal("QUEUED", receipt.GetProperty("jobStatus").GetString());
        Assert.Equal("ROLL_80", receipt.GetProperty("mediaClass").GetString());
        var jobId = receipt.GetProperty("jobId").GetInt64();

        // القالب المختار هو قالب G2 المعتمد للوسيط (ROLL_MEDIUM_80 نسخة 1)
        Assert.Equal("ROLL_MEDIUM_80", await GetTemplateCodeAsync(jobId));
    }

    private static async Task<string> GetTemplateCodeAsync(long jobId)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            SELECT t.template_code FROM print.print_jobs j
              JOIN print.document_templates t ON t.template_id = j.template_id
             WHERE j.job_id = {jobId}", conn);
        return (string)(await cmd.ExecuteScalarAsync() ?? string.Empty);
    }

    // ═══ الوردية (§9.9): فتح وربط الفواتير وملخص المتوقع ═══════════════════════

    [Fact]
    public async Task T18_ShiftOpen_BindingAndSummary()
    {
        // حتمية: أغلق أي وردية مفتوحة سابقة للمحطة — بإغلاق كامل الشروط (V019/G8 يشترط العدّ
        // والمتوقع والمغلق والزمن على الإغلاق المباشر كما تفعل الواجهة دائماً)
        await using (var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                UPDATE sales.shifts s SET status='CLOSED', closed_at=now(),
                       closed_by=(SELECT user_id FROM core.users WHERE username='g7.supervisor'),
                       expected_cash=s.opening_float, counted_cash=s.opening_float
                 WHERE s.station_id={_fx.StationId} AND s.status='OPEN'
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        using var cashier = await ClientAsync("g7.cashier", "Cashier#G7-Pass");
        var open = await cashier.PostAsJsonAsync("/api/v1/pos/shifts", new { openingFloat = 200.00m });
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        var shiftId = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shiftId").GetInt64();

        // وردية ثانية للمحطة نفسها مرفوضة (واحدة مفتوحة دائماً)
        var second = await cashier.PostAsJsonAsync("/api/v1/pos/shifts", new { openingFloat = 50m });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // فاتورة داخل الوردية
        var sale = await cashier.PostAsJsonAsync("/api/v1/pos/sales", new
        {
            lines = new[] { CashSaleLine(_fx.BagSkuId, 4m) },
            payments = new[] { CashPayment(20.00m) },
            idempotencyKey = $"g7-t18-{Guid.NewGuid():N}",
        });
        var invoiceId = (await sale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invoiceId").GetInt64();
        Assert.Equal(shiftId, await ScalarAsync($"SELECT shift_id FROM sales.invoices WHERE invoice_id={invoiceId}"));

        // الملخص: المتوقع في الدرج = افتتاحي 200 + نقدي 20
        var summary = await cashier.GetAsync($"/api/v1/pos/shifts/{shiftId}/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var summaryBody = await summary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(220.00m, summaryBody.GetProperty("expectedCashDrawer").GetDecimal());
        Assert.True(summaryBody.GetProperty("invoiceCount").GetInt32() >= 1);
    }
}

// ═══════════════════════ عدة البذر G7 ═════════════════════════════════════════

public sealed class G7ApiFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seededOnce;

    public WeraApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public long BranchId { get; private set; }
    public long StationId { get; private set; }
    public long WarehouseId { get; private set; }
    public long BagSkuId { get; private set; }
    public long BoxSkuId { get; private set; }
    public long CheeseSkuId { get; private set; }
    public long LowSkuId { get; private set; }
    public long CreditCustomerId { get; private set; }
    public long PrinterProfileId { get; private set; }
    public string BagBarcode { get; private set; } = "6221031497773";
    /// <summary>باركود ملصق موزون صالح: 21 + PLU 4071 + 00350 (0.350 كجم بثلاث منازل).</summary>
    public string ValidWeightBarcode { get; private set; } = "214071003500";  // 21+4071+00350+ خانة تحقق غير مفحوصة (NONE)

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient(); // يقلع ويرحّل حتى رأس V018
        await SeedOnceAsync();
    }

    private async Task SeedOnceAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seededOnce)
            {
                return;
            }
            await SeedCoreAsync();
            _seededOnce = true;
        }
        finally
        {
            SeedLock.Release();
        }
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task<Contracts.V1.LoginResponse> LoginAsync(string username, string password, long? branchId = null, long? stationId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password, branchId, stationId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contracts.V1.LoginResponse>())!;
    }

    private async Task SeedCoreAsync()
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

        UpsertUser("g7.owner", "مالك G7", "Owner#G7-Pass", "OWNER");
        UpsertUser("g7.manager", "مدير G7", "Manager#G7-Pass", "MANAGER");
        UpsertUser("g7.supervisor", "مشرف G7", "Supervisor#G7-Pass", "SUPERVISOR");
        UpsertUser("g7.cashier", "كاشير G7", "Cashier#G7-Pass", "CASHIER");
        UpsertUser("g7.storekeeper", "أمين مخزن G7", "Storekeeper#G7-Pass", "STOREKEEPER");
        UpsertUser("g7.auditor", "مراجع G7", "Auditor#G7-Pass", "AUDITOR");

        BranchId = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G7B1', 'فرع G7 الرئيسي', 'Africa/Cairo') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING branch_id");
        StationId = Scalar($"INSERT INTO core.stations (branch_id, code, name_ar) VALUES ({BranchId}, 'G7ST1', 'محطة كاشير G7') ON CONFLICT (branch_id, code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING station_id");
        WarehouseId = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({BranchId}, 'G7W1', 'مخزن البيع G7', 'MAIN') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");
        Scalar($"INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, is_active) VALUES ('G7DRAWER', 'درج محطة G7', 'CASH_DRAWER', {BranchId}, true) ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id");
        Scalar("INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, is_active) VALUES ('G7PROVIDER', 'حساب مزود الدفع (محاكاة)', 'PAYMENT_PROVIDER', true) ON CONFLICT (code) DO NOTHING");
        CreditCustomerId = Scalar("INSERT INTO sales.customers (code, name_ar, credit_enabled, credit_limit) VALUES ('G7CRED', 'عميل آجل G7', true, 500) ON CONFLICT (code) DO UPDATE SET credit_enabled = EXCLUDED.credit_enabled, credit_limit = EXCLUDED.credit_limit RETURNING customer_id");

        var categoryId = Scalar("INSERT INTO catalog.categories (code, name_ar) VALUES ('G7CAT', 'فئة G7') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING category_id");
        var priceListId = Scalar("SELECT price_list_id FROM catalog.price_lists WHERE code='DEFAULT_EGP'");
        var ownerId = Scalar("SELECT user_id FROM core.users WHERE username='g7.owner'");

        // منتج الأكياس/العلب (سعران مستقلان: كيس 5 وعلبة 55 من 12 كيساً)
        var alreadySeeded = Scalar("SELECT COUNT(*) FROM catalog.products WHERE code = 'G7-SCN'") > 0;

        var login = await LoginAsync("g7.owner", "Owner#G7-Pass", BranchId, StationId);
        using var authed = CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        if (!alreadySeeded)
        {
            var milk = CatalogEndpointTests.BagAndBoxPayload(
                "G7-SCN", "G7-SCN-BAG", "G7-SCN-BOX",
                BagBarcode, "6221031497780", categoryId, priceListId);
            var milkCreated = await authed.PostAsJsonAsync("/api/v1/catalog/products", milk);
            Assert.True(milkCreated.IsSuccessStatusCode, "بذر منتج الأكياس فشل: " + await milkCreated.Content.ReadAsStringAsync());

            var cheese = new
            {
                code = "G7-CHEESE",
                nameAr = "جبنة G7 بالكيلو",
                categoryId,
                trackLots = false,
                trackSerials = false,
                skus = new object[]
                {
                new
                {
                    skuCode = "G7-CHEESE-KG", saleMode = "WEIGHT", baseUomId = 2, saleUomId = 2, nameAr = "بالكيلو", reorderLevel = 0,
                    weight = new { weightPrecisionG = 1, weightStepG = 5, allowManualWeight = true, allowAmountTarget = true, minWeightKg = (decimal?)0.05m, maxWeightKg = (decimal?)50m, defaultTareKg = 0m },
                },
                },
                barcodes = new object[] { new { barcodeValue = "6291041500770", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
                plus = new object[] { new { pluCode = "4071", skuOrdinal = 0 } },
                prices = new object[] { new { skuOrdinal = 0, priceListId, unitId = 2, unitPrice = 200m, taxTreatment = "TAXABLE" } },  // 200 ج.م/كجم — المثال الملزم
                packages = Array.Empty<object>(),
            };
            var cheeseCreated = await authed.PostAsJsonAsync("/api/v1/catalog/products", cheese);
            Assert.True(cheeseCreated.IsSuccessStatusCode, "بذر الجبن فشل: " + await cheeseCreated.Content.ReadAsStringAsync());

            var low = new
            {
                code = "G7-LOW",
                nameAr = "صنف محدود المخزون G7",
                categoryId,
                trackLots = false,
                trackSerials = false,
                skus = new object[] { new { skuCode = "G7-LOW-1", saleMode = "COUNT", baseUomId = 1, saleUomId = 1, nameAr = "وحدة", reorderLevel = 0, weight = (object?)null } },
                barcodes = new object[] { new { barcodeValue = "6221031497797", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
                plus = Array.Empty<object>(),
                prices = new object[] { new { skuOrdinal = 0, priceListId, unitId = 1, unitPrice = 5.00m, taxTreatment = "TAXABLE" } },
                packages = Array.Empty<object>(),
            };
            var lowCreated = await authed.PostAsJsonAsync("/api/v1/catalog/products", low);
            Assert.True(lowCreated.IsSuccessStatusCode, "بذر الصنف المحدود فشل: " + await lowCreated.Content.ReadAsStringAsync());

        } // نهاية بذر المنتجات لمرة واحدة

        // المعرفات تُجلب دائماً (البذرة قد تكون موجودة من تشغيل سابق)
        BagSkuId = Scalar("SELECT sku_id FROM catalog.skus WHERE sku_code='G7-SCN-BAG'");
        BoxSkuId = Scalar("SELECT sku_id FROM catalog.skus WHERE sku_code='G7-SCN-BOX'");
        CheeseSkuId = Scalar("SELECT sku_id FROM catalog.skus WHERE sku_code='G7-CHEESE-KG'");
        LowSkuId = Scalar("SELECT sku_id FROM catalog.skus WHERE sku_code='G7-LOW-1'");

        // ملف باركود الوزن المتغير: بادئة 21 · طول 11 · PLU 4 خانات · وزن 5 خانات بثلاث منازل (كجم)
        var kgUom = Scalar("SELECT uom_id FROM catalog.units WHERE code='KG'");
        // البنية 12 خانة: [21][PLU×4][وزن×5][خانة تحقق NONE] — مواضع المقاطع بعد البادئة (قيد vw_segments_valid)
        Scalar($@"INSERT INTO catalog.vw_barcode_profiles
                    (name, prefix, total_length, plu_start_pos, plu_length, value_type, value_start_pos, value_length, value_decimals, value_unit_id, check_digit_rule, is_active)
                  SELECT 'G7-SCALE-A', '21', 12, 3, 4, 'WEIGHT', 7, 5, 3, {kgUom}, 'NONE', true
                   WHERE NOT EXISTS (SELECT 1 FROM catalog.vw_barcode_profiles WHERE name = 'G7-SCALE-A')");
        Scalar(@"UPDATE catalog.vw_barcode_profiles
                   SET total_length = 12, plu_start_pos = 3, value_start_pos = 7, is_active = true
                 WHERE name = 'G7-SCALE-A'");

        // بذر المخزون مرة واحدة فقط (لا تراكم عند إعادة تشغيل البذرة على قاعدة قائمة)
        var stockSeeded = Scalar("SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='g7.seed'") > 0;
        if (!stockSeeded)
        {
            // مخزون افتتاحي + طبقات WAC: 100000 كيس (ميزانية ضغط 1000 سطر) + صنف محدود 5 + 10 كجم جبن
            Scalar($@"INSERT INTO finance.cost_layers (sku_id, warehouse_id, costing_method, avg_unit_cost, qty_on_layer)
                  VALUES ({BagSkuId}, {WarehouseId}, 'WAC', 2.0000, 100000)
                  ON CONFLICT (sku_id, warehouse_id) DO UPDATE SET avg_unit_cost = 2.0000, qty_on_layer = 100000");
            Scalar($@"INSERT INTO finance.cost_layers (sku_id, warehouse_id, costing_method, avg_unit_cost, qty_on_layer)
                  VALUES ({LowSkuId}, {WarehouseId}, 'WAC', 2.0000, 5)
                  ON CONFLICT (sku_id, warehouse_id) DO UPDATE SET avg_unit_cost = 2.0000, qty_on_layer = 5");
            Scalar($@"INSERT INTO finance.cost_layers (sku_id, warehouse_id, costing_method, avg_unit_cost, qty_on_layer)
                  VALUES ({CheeseSkuId}, {WarehouseId}, 'WAC', 120.0000, 10)
                  ON CONFLICT (sku_id, warehouse_id) DO UPDATE SET avg_unit_cost = 120.0000, qty_on_layer = 10");
            Scalar($@"INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor, source_doc_type, source_doc_id, reason, branch_id, user_id)
                  VALUES ({BagSkuId}, {WarehouseId}, 'OPENING', 1, 100000, 1, 1, 'g7.seed', 1, 'بذر مخزون G7', {BranchId}, {ownerId})");
            Scalar($@"INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor, source_doc_type, source_doc_id, reason, branch_id, user_id)
                  VALUES ({LowSkuId}, {WarehouseId}, 'OPENING', 1, 5, 1, 1, 'g7.seed', 3, 'بذر الصنف المحدود G7', {BranchId}, {ownerId})");
            Scalar($@"INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor, source_doc_type, source_doc_id, reason, branch_id, user_id)
                  VALUES ({CheeseSkuId}, {WarehouseId}, 'OPENING', 1, 10000, 2, 1, 'g7.seed', 2, 'بذر جبن G7 (جرام)', {BranchId}, {ownerId})");
        } // نهاية بذر المخزون لمرة واحدة

        // طابعة محطة غير معايرة بعد (فحص HOLD والمعايرة في T17) + تعيين دور إيصال العميل
        PrinterProfileId = Scalar($@"INSERT INTO print.printer_profiles
                    (printer_id, windows_queue_name, device_model, driver_name, station_id, media_class, media_width_mm, printable_width_mm, dpi_x, dpi_y, adapter_kind)
                  VALUES ('G7-PRN-80', 'G7Queue80', 'Epson TM-T88', 'ESC_POS', {StationId}, 'ROLL_80', 80, 72, 180, 180, 'ESC_POS')
                  ON CONFLICT (printer_id) DO UPDATE SET station_id = EXCLUDED.station_id RETURNING printer_profile_id");
        Scalar($@"INSERT INTO print.printer_document_roles (printer_profile_id, station_id, document_role, is_auto_routing_enabled)
                  VALUES ({PrinterProfileId}, {StationId}, 'CUSTOMER_RECEIPT', false)
                  ON CONFLICT (station_id, document_role) DO UPDATE SET printer_profile_id = EXCLUDED.printer_profile_id");

        // إعداد البطاقة: محاكاة موسومة (اختبار 2 يعيد NOT_CONFIGURED مؤقتاً ثم يعيدها)
        Scalar("UPDATE core.business_settings SET value_text='SANDBOX_SIMULATOR' WHERE setting_key='payments.card_provider'");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
