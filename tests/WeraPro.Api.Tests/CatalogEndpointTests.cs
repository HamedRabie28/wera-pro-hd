namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

/// <summary>
/// فحوص بوابة G4 (§4 الكتالوج): إنشاء متعدد الأقسام ذري · تفرد الأكواد ·
/// لا سعر صفر · حقول الوزن للوزني حصراً · بحث Exact Match بالباركود/SKU/PLU ·
/// بحث نصي محدود · RBAC خادمي · السيناريو الإلزامي (كيس مفرد + علبة 12 كيساً
/// بسعرين مستقلين وتحويل مخزون صحيح) واختبارات البيع/الشراء/المرتجع/الجرد
/// لكل صيغة بيع على PostgreSQL حقيقي مُرحّل حتى رأس V015.
/// الترتيب داخل الصف حتمي عبر بذر التجهيز (منتجا السيناريو يُنشآن مرة واحدة
/// في InitializeAsync) ومخزن مخصص لتأكيد الأرصدة يعزله عن ضجيج فحوص RBAC.
/// </summary>
public sealed class CatalogEndpointTests : IClassFixture<CatalogApiFixture>
{
    private readonly CatalogApiFixture _fx;

    private static readonly string[] ReceiveSerials = ["SN-A1", "SN-B2"];
    private static readonly string[] SaleSerial = ["SN-A1"];

    public CatalogEndpointTests(CatalogApiFixture fx) => _fx = fx;

    private async Task<HttpClient> ClientAsync(string username, string password, long? branchId = null)
    {
        var login = await _fx.LoginAsync(username, password, branchId);
        var client = _fx.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    /// <summary>طلب كيس + علبة 12 كيساً (بنية المنتج المرجعي §4.3).</summary>
    public static object BagAndBoxPayload(
        string code, string bagSku, string boxSku, string barcodeBag, string barcodeBox,
        long categoryId, long priceListId, decimal bagPrice = 5.00m, decimal boxPrice = 55.00m) => new
        {
            code,
            nameAr = "لبن كامل الدسم 500 مل",
            nameEn = "Full Cream Milk 500ml",
            categoryId,
            description = "منتج اختبار السيناريو الإلزامي",
            trackLots = false,
            trackSerials = false,
            skus = new object[]
        {
            new { skuCode = bagSku, saleMode = "COUNT", baseUomId = 1, saleUomId = 1, nameAr = "كيس مفرد", reorderLevel = 10, weight = (object?)null },
            new { skuCode = boxSku, saleMode = "PACK", baseUomId = 1, saleUomId = 1, nameAr = "علبة 12 كيساً", reorderLevel = 2, weight = (object?)null },
        },
            barcodes = new object[]
        {
            new { barcodeValue = barcodeBag, barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 },
            new { barcodeValue = barcodeBox, barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 1 },
        },
            plus = Array.Empty<object>(),
            prices = new object[]
        {
            new { skuOrdinal = 0, priceListId, unitId = 1, unitPrice = bagPrice, taxTreatment = "TAXABLE" },
            new { skuOrdinal = 1, priceListId, unitId = 1, unitPrice = boxPrice, taxTreatment = "TAXABLE" },
        },
            packages = new object[]
        {
            new { packageSkuOrdinal = 1, contentSkuOrdinal = 0, unitsPerPackage = 12, openPackageAllowed = true },
        },
        };

    public static object CheeseWeightPayload(long categoryId, long priceListId) => new
    {
        code = "G4-CHEESE-01",
        nameAr = "جبنة رومي بلدي طازجة",
        categoryId,
        trackLots = false,
        trackSerials = false,
        skus = new object[]
        {
            new
            {
                skuCode = "G4-CHEESE-01-KG", saleMode = "WEIGHT", baseUomId = 2, saleUomId = 2, nameAr = "بالكيلو", reorderLevel = 0,
                weight = new { weightPrecisionG = 1, weightStepG = 5, allowManualWeight = true, allowAmountTarget = true, minWeightKg = (decimal?)0.1m, maxWeightKg = (decimal?)5m, defaultTareKg = 0m },
            },
        },
        barcodes = new object[] { new { barcodeValue = "6291041500213", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
        plus = new object[] { new { pluCode = "3021", skuOrdinal = 0 } },
        prices = new object[] { new { skuOrdinal = 0, priceListId, unitId = 2, unitPrice = 189m, taxTreatment = "TAXABLE" } },
        packages = Array.Empty<object>(),
    };

    // ── الإنشاء الذري متعدد الأقسام ──────────────────────────────────────

    [Fact]
    public async Task CreateProduct_BagAndBox_AtomicWithIndependentPrices()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = BagAndBoxPayload("G4-CREATE-01", "G4-CREATE-01-BAG", "G4-CREATE-01-BOX",
            "6221031492174", "6221031492181", _fx.CategoryId, _fx.PriceListId);

        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("G4-CREATE-01", body.GetProperty("code").GetString());
        var skus = body.GetProperty("skus");
        Assert.Equal(2, skus.GetArrayLength());

        // سعران مستقلان — لا يُستنتج أحدهما من الآخر (§4.3 ق4)
        var bagPrice = skus[0].GetProperty("prices")[0].GetProperty("unitPrice").GetDecimal();
        var boxPrice = skus[1].GetProperty("prices")[0].GetProperty("unitPrice").GetDecimal();
        Assert.Equal(5.00m, bagPrice);
        Assert.Equal(55.00m, boxPrice);
        Assert.NotEqual(bagPrice, boxPrice);

        // الصيغ كما أرسلت حرفياً — لا استنتاج (§4.2)
        Assert.Equal("COUNT", skus[0].GetProperty("saleMode").GetString());
        Assert.Equal("PACK", skus[1].GetProperty("saleMode").GetString());
        Assert.Equal("كيس مفرد", skus[0].GetProperty("nameAr").GetString());

        // العبوة: علبة = 12 كيساً
        var packages = body.GetProperty("packages");
        Assert.Equal(1, packages.GetArrayLength());
        Assert.Equal(12m, packages[0].GetProperty("unitsPerPackage").GetDecimal());

        // سطر التدقيق مكتوب
        var productId = body.GetProperty("productId").GetInt64();
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.audit_logs WHERE entity_schema='catalog.products' AND entity_id=@p AND action='PRODUCT_CREATE'",
            conn);
        cmd.Parameters.AddWithValue("p", productId);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) >= 1, "لا يوجد صف PRODUCT_CREATE في التدقيق");
    }

    [Fact]
    public async Task CreateProduct_InvalidBarcode_NothingPersisted_NoPartialSave()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = BagAndBoxPayload("G4-MILK-02", "G4-MILK-02-BAG", "G4-MILK-02-BOX",
            "4006381333932", "4006381333948", _fx.CategoryId, _fx.PriceListId);   // رقم تحقق مكسور

        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // لا حفظ جزئي: لا منتج ولا صيغة ولا أي قسم
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM catalog.products WHERE code = 'G4-MILK-02'", conn);
        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        await using var cmdSku = new NpgsqlCommand("SELECT COUNT(*) FROM catalog.skus WHERE sku_code LIKE 'G4-MILK-02%'", conn);
        Assert.Equal(0L, Convert.ToInt64(await cmdSku.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CreateProduct_DuplicateProductCode_409()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var first = BagAndBoxPayload("G4-DUP-01", "G4-DUP-01-BAG", "G4-DUP-01-BOX",
            "6291041500220", "6291041500237", _fx.CategoryId, _fx.PriceListId);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/catalog/products", first)).StatusCode);

        var second = BagAndBoxPayload("G4-DUP-01", "G4-DUP-01-BAG-2", "G4-DUP-01-BOX-2",
            "6291041500220", "6291041500237", _fx.CategoryId, _fx.PriceListId);
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", second);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("مستخدم", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CreateProduct_DuplicateSkuCode_409()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = BagAndBoxPayload("G4-DUP-02", "G4-DUP-01-BAG", "G4-DUP-02-BOX",
            "6221031492174", "6221031492181", _fx.CategoryId, _fx.PriceListId);
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateProduct_DuplicateActiveBarcode_409_CrossProduct()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        // 4006381333931 نشط لمنتج السيناريو المبذور — نطاق التفرد على مستوى النشاط
        var payload = BagAndBoxPayload("G4-DUP-03", "G4-DUP-03-BAG", "G4-DUP-03-BOX",
            "4006381333931", "6221031492181", _fx.CategoryId, _fx.PriceListId);
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task CreateProduct_ZeroOrNegativePrice_400(decimal price)
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = BagAndBoxPayload("G4-PRICE-01", "G4-PRICE-01-BAG", "G4-PRICE-01-BOX",
            "6221031492174", "6221031492181", _fx.CategoryId, _fx.PriceListId, bagPrice: price);
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateProduct_WeightFieldsOnCountSku_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-WF-01",
            nameAr = "منتج عددي بحقول وزن",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new
                {
                    skuCode = "G4-WF-01-BAG", saleMode = "COUNT", baseUomId = 1, saleUomId = 1, reorderLevel = 0,
                    weight = new { weightPrecisionG = 1, weightStepG = 5, allowManualWeight = true, allowAmountTarget = true, minWeightKg = (decimal?)0.1m, maxWeightKg = (decimal?)5m, defaultTareKg = 0m },
                },
            },
            barcodes = new object[] { new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 10m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("weight_fields_forbidden", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CreateProduct_WeightSkuWithoutFields_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-WF-02",
            nameAr = "جبنة وزنية بلا إعدادات",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new { skuCode = "G4-WF-02-KG", saleMode = "WEIGHT", baseUomId = 2, saleUomId = 2, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[] { new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 2, unitPrice = 200m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("weight_fields_required", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CreateProduct_PackWithoutPackageDefinition_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-PACK-01",
            nameAr = "علبة بلا تعريف",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new { skuCode = "G4-PACK-01-BAG", saleMode = "COUNT", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
                new { skuCode = "G4-PACK-01-BOX", saleMode = "PACK", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[]
            {
                new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 },
                new { barcodeValue = "6221031492181", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 1 },
            },
            plus = Array.Empty<object>(),
            prices = new object[]
            {
                new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 5m, taxTreatment = "TAXABLE" },
                new { skuOrdinal = 1, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 55m, taxTreatment = "TAXABLE" },
            },
            packages = Array.Empty<object>(),
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("package_definition_required", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CreateProduct_PackageSelfOrNestedOrWrongMode_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");

        // عبوة تشير لنفسها
        var self = new
        {
            code = "G4-PACK-02",
            nameAr = "عبوة ذاتية",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new { skuCode = "G4-PACK-02-BOX", saleMode = "PACK", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[] { new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 55m, taxTreatment = "TAXABLE" } },
            packages = new object[] { new { packageSkuOrdinal = 0, contentSkuOrdinal = 0, unitsPerPackage = 12, openPackageAllowed = true } },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/catalog/products", self)).StatusCode);

        // عبوة محتواها صنف وزني (موزونة مسبقاً) — تُرحَّل بوزنها في G6 لا عدداً هنا
        var weightBox = new
        {
            code = "G4-PACK-03",
            nameAr = "علبة من صنف وزني",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new
                {
                    skuCode = "G4-PACK-03-KG", saleMode = "WEIGHT", baseUomId = 2, saleUomId = 2, reorderLevel = 0,
                    weight = new { weightPrecisionG = 1, weightStepG = 5, allowManualWeight = true, allowAmountTarget = true, minWeightKg = (decimal?)null, maxWeightKg = (decimal?)null, defaultTareKg = 0m },
                },
                new { skuCode = "G4-PACK-03-BOX", saleMode = "PACK", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[]
            {
                new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 },
                new { barcodeValue = "6221031492181", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 1 },
            },
            plus = Array.Empty<object>(),
            prices = new object[]
            {
                new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 2, unitPrice = 200m, taxTreatment = "TAXABLE" },
                new { skuOrdinal = 1, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 55m, taxTreatment = "TAXABLE" },
            },
            packages = new object[] { new { packageSkuOrdinal = 1, contentSkuOrdinal = 0, unitsPerPackage = 12, openPackageAllowed = true } },
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", weightBox);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("package_content_weight_not_allowed", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CreateProduct_BaseAndSaleUomDifferentDimensions_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-DIM-01",
            nameAr = "خلط أبعاد",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new { skuCode = "G4-DIM-01-X", saleMode = "COUNT", baseUomId = 2, saleUomId = 1, reorderLevel = 0, weight = (object?)null },   // KG أساس + PIECE بيع
            },
            barcodes = new object[] { new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 3m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unit_dimension_mismatch", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CreateProduct_SkuWithoutPrice_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-NOPRICE-01",
            nameAr = "صيغة بلا سعر",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new { skuCode = "G4-NOPRICE-01-A", saleMode = "COUNT", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
                new { skuCode = "G4-NOPRICE-01-B", saleMode = "COUNT", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[]
            {
                new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 },
                new { barcodeValue = "6221031492181", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 1 },
            },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 5m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("price_required_per_sku", (await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task CreateProduct_SerializedWithoutTrackSerials_400()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-SN-01",
            nameAr = "سيريال بلا تتبع",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = false,
            skus = new object[]
            {
                new { skuCode = "G4-SN-01-X", saleMode = "SERIALIZED", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[] { new { barcodeValue = "6221031492174", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 999m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        var response = await client.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("serialized_requires_track_serials", (await response.Content.ReadAsStringAsync()));
    }

    // ── المطابقة الدقيقة (Exact Match §3.1) ───────────────────────────────

    [Fact]
    public async Task Lookup_ByBarcode_SkuCode_NotFound()
    {
        using var client = await ClientAsync("g4.cashier", "Cashier#G4-Pass");   // القراءة لكل الأدوار (V015)

        // بالباركود (مع فراغات — التنميط يوحّد)
        var byBarcode = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/lookup?code=" + Uri.EscapeDataString(" 4006381333931 "));
        Assert.Equal("BARCODE", byBarcode.GetProperty("matchKind").GetString());
        Assert.Equal("G4-MILK-01-BAG", byBarcode.GetProperty("sku").GetProperty("skuCode").GetString());

        // بكود الصيغة (غير حساس لحالة الأحرف)
        var bySku = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/lookup?code=g4-milk-01-box");
        Assert.Equal("SKU", bySku.GetProperty("matchKind").GetString());
        Assert.Equal("PACK", bySku.GetProperty("sku").GetProperty("saleMode").GetString());

        // بلا مطابقة — 404 صريح
        var notFound = await client.GetAsync("/api/v1/catalog/lookup?code=0000000000000");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Lookup_ByPlu_FindsWeightSkuWithD07Settings()
    {
        using var client = await ClientAsync("g4.cashier", "Cashier#G4-Pass");
        var byPlu = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/lookup?code=3021");
        Assert.Equal("PLU", byPlu.GetProperty("matchKind").GetString());
        Assert.Equal("G4-CHEESE-01-KG", byPlu.GetProperty("sku").GetProperty("skuCode").GetString());
        Assert.Equal("WEIGHT", byPlu.GetProperty("sku").GetProperty("saleMode").GetString());
        var weight = byPlu.GetProperty("sku").GetProperty("weight");
        Assert.Equal(1, weight.GetProperty("weightPrecisionG").GetInt32());
        Assert.Equal(5, weight.GetProperty("weightStepG").GetInt32());
        Assert.Equal(0.1m, weight.GetProperty("minWeightKg").GetDecimal());
    }

    // ── البحث النصي المحدود (ترقيم خادمي) ────────────────────────────────

    [Fact]
    public async Task Search_TextLimited_Paged_Capped()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");

        var search = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?query=" + Uri.EscapeDataString("لبن كامل"));
        Assert.True(search.GetProperty("totalCount").GetInt64() >= 1);
        Assert.True(search.GetProperty("items").GetArrayLength() >= 1);
        Assert.Equal(1, search.GetProperty("page").GetInt32());

        // الحد الأقصى لحجم الصفحة 50 — لا مسح غير محدود (§7.2.1)
        var capped = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?pageSize=500");
        Assert.Equal(50, capped.GetProperty("pageSize").GetInt32());

        // بحث أقصر من حرفين يرفض — للمفرد المطابقة الدقيقة
        var tooShort = await client.GetAsync("/api/v1/catalog/products?query=ل");
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // فلتر صيغة البيع: كل نتيجة تملك صيغة PACK نشطة
        var packOnly = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?saleMode=PACK&query=" + Uri.EscapeDataString("لبن كامل"));
        foreach (var item in packOnly.GetProperty("items").EnumerateArray())
        {
            var modes = item.GetProperty("skus").EnumerateArray().Select(s => s.GetProperty("saleMode").GetString());
            Assert.Contains("PACK", modes);
        }
    }

    [Fact]
    public async Task Units_ReturnSeededUnitsAndConversions()
    {
        using var client = await ClientAsync("g4.cashier", "Cashier#G4-Pass");
        var units = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/units");
        Assert.Equal(7, units.GetProperty("units").GetArrayLength());
        Assert.Equal(3, units.GetProperty("conversions").GetArrayLength());
        var kg = units.GetProperty("units").EnumerateArray().First(u => u.GetProperty("code").GetString() == "KG");
        Assert.Equal("MASS", kg.GetProperty("dimension").GetString());
    }

    [Fact]
    public async Task Reference_ReturnsCategoriesBrandsPriceLists()
    {
        using var client = await ClientAsync("g4.manager", "Manager#G4-Pass");
        var reference = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/reference");
        Assert.True(reference.GetProperty("categories").GetArrayLength() >= 1);   // فئة التجهيز
        var priceLists = reference.GetProperty("priceLists");
        Assert.True(priceLists.GetArrayLength() >= 1);
        var hasDefault = priceLists.EnumerateArray().Any(p => p.GetProperty("isDefault").GetBoolean());
        Assert.True(hasDefault, "قائمة سعرية افتراضية يجب أن تكون موجودة (بذر V009)");
    }

    [Fact]
    public async Task GetProduct_FullComposition()
    {
        using var client = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?query=G4-MILK-01");
        var productId = list.GetProperty("items")[0].GetProperty("productId").GetInt64();

        var product = await client.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/products/{productId}");
        Assert.Equal("G4-MILK-01", product.GetProperty("code").GetString());
        Assert.Equal(2, product.GetProperty("skus").GetArrayLength());
        Assert.Equal(1, product.GetProperty("packages").GetArrayLength());
        Assert.Equal(2, product.GetProperty("skus")[0].GetProperty("barcodes").GetArrayLength()
            + product.GetProperty("skus")[1].GetProperty("barcodes").GetArrayLength());
    }

    // ── RBAC خادمي (§18) ─────────────────────────────────────────────────

    [Fact]
    public async Task RBAC_CashierAndStorekeeperCannotCreate_CashierCanRead()
    {
        using var cashier = await ClientAsync("g4.cashier", "Cashier#G4-Pass");
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);

        var payload = BagAndBoxPayload("G4-RBAC-01", "G4-RBAC-01-BAG", "G4-RBAC-01-BOX",
            "6221031492174", "6221031492181", _fx.CategoryId, _fx.PriceListId);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/v1/catalog/products", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await storekeeper.PostAsJsonAsync("/api/v1/catalog/products", payload)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/catalog/products?query=G4-MILK")).StatusCode);
    }

    [Fact]
    public async Task RBAC_MovementPosting_CashierForbidden_StorekeeperAllowed()
    {
        using var cashier = await ClientAsync("g4.cashier", "Cashier#G4-Pass");
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);

        var bagSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BAG");
        var payload = new
        {
            movementType = "RECEIVE",
            warehouseId = _fx.WarehouseId,
            quantity = 1m,
            sourceUomId = 1,
            sourceDocType = "g4.rbac",
            sourceDocId = 990001,
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync($"/api/v1/catalog/skus/{bagSkuId}/stock-movements", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{bagSkuId}/stock-movements", payload)).StatusCode);
    }

    [Fact]
    public async Task Movement_WithoutBranchScope_400_Explicit()
    {
        // دخول بلا نطاق فرع → رفض صريح (الدفتر يوثق كل حركة بفرعها)
        var login = await _fx.LoginAsync("g4.storekeeper", "Storekeeper#G4-Pass");
        using var storekeeperNoBranch = _fx.CreateClient();
        storekeeperNoBranch.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var bagSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BAG");
        var response = await storekeeperNoBranch.PostAsJsonAsync($"/api/v1/catalog/skus/{bagSkuId}/stock-movements", new
        {
            movementType = "RECEIVE",
            warehouseId = _fx.WarehouseId,
            quantity = 1m,
            sourceUomId = 1,
            sourceDocType = "g4.scope",
            sourceDocId = 994300,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("branch_scope_required", (await response.Content.ReadAsStringAsync()));
    }

    // ── السيناريو الإلزامي §4.3: كيس مفرد + علبة 12 كيساً بسعرين مستقلين ──

    [Fact]
    public async Task MandatoryScenario_BagAndBox_StockConversion_AllDocumentKinds()
    {
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);
        var bagSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BAG");
        var boxSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BOX");
        var wh = _fx.ScenarioWarehouseId;
        long docId = 991000;

        async Task<JsonElement> Post(long skuId, string type, decimal qty, long uom, string? reason = null, string? lineRef = null)
        {
            var payload = new
            {
                movementType = type,
                warehouseId = wh,
                quantity = qty,
                sourceUomId = uom,
                sourceDocType = "g4.scenario",
                sourceDocId = docId,
                sourceLineRef = lineRef,
                reason,
            };
            var response = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{skuId}/stock-movements", payload);
            response.EnsureSuccessStatusCode();
            docId++;
            return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        }

        // 1) شراء/استلام: علبتان = 24 كيساً — صف واحد على المحتوى بعامل 12 (أثر واحد §4.3 ق5)
        var receive = await Post(boxSkuId, "RECEIVE", 2m, 1, lineRef: "receive-boxes");
        Assert.Equal(bagSkuId, receive.GetProperty("skuId").GetInt64());
        Assert.Equal(24m, receive.GetProperty("baseQty").GetDecimal());
        Assert.Equal(12m, receive.GetProperty("conversionFactor").GetDecimal());
        Assert.Equal(1, receive.GetProperty("direction").GetInt32());

        // 2) بيع علبة واحدة = 12 كيساً — صف واحد بعامل 12 لا 12 حركة
        var sale = await Post(boxSkuId, "SALE", 1m, 1, lineRef: "sale-one-box");
        Assert.Equal(bagSkuId, sale.GetProperty("skuId").GetInt64());
        Assert.Equal(12m, sale.GetProperty("baseQty").GetDecimal());
        Assert.Equal(12m, sale.GetProperty("conversionFactor").GetDecimal());
        Assert.Equal(-1, sale.GetProperty("direction").GetInt32());

        // 3) بيع 5 أكياس مفردة — عامل 1
        var saleBags = await Post(bagSkuId, "SALE", 5m, 1, lineRef: "sale-bags");
        Assert.Equal(bagSkuId, saleBags.GetProperty("skuId").GetInt64());
        Assert.Equal(5m, saleBags.GetProperty("baseQty").GetDecimal());
        Assert.Equal(1m, saleBags.GetProperty("conversionFactor").GetDecimal());

        // 4) مرتجع بيع علبة = +12
        var ret = await Post(boxSkuId, "SALE_RETURN", 1m, 1, lineRef: "return-box");
        Assert.Equal(12m, ret.GetProperty("baseQty").GetDecimal());
        Assert.Equal(1, ret.GetProperty("direction").GetInt32());

        // 5) جرد: عُدّت 3 علب = تسوية +36 (العبوة تُحسب بعاملها الكامل)
        var count = await Post(boxSkuId, "ADJUSTMENT_IN", 3m, 1, reason: "جرد فعلي - فرق عد");
        Assert.Equal(36m, count.GetProperty("baseQty").GetDecimal());
        Assert.Equal(12m, count.GetProperty("conversionFactor").GetDecimal());

        // التحقق من الدفتر والرصيد مباشرة من القاعدة
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();

        // خمس حركات على SKU الكيس فقط (استلام علبتين + بيع علبة + بيع أكياس + مرتجع + جرد) — لا ازدواج علبة/كيس
        await using (var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='g4.scenario' AND sku_id=@bag AND movement_type IN ('RECEIVE','SALE','SALE_RETURN','ADJUSTMENT_IN')",
            conn))
        {
            cmd.Parameters.AddWithValue("bag", bagSkuId);
            Assert.Equal(5L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }

        // الرصيد = 24 - 12 - 5 + 12 + 36 = 55 كيساً (المشغّل يصونه تلقائياً)
        await using (var cmd = new NpgsqlCommand(
            "SELECT base_qty FROM inv.stock_balances WHERE sku_id=@bag AND warehouse_id=@wh", conn))
        {
            cmd.Parameters.AddWithValue("bag", bagSkuId);
            cmd.Parameters.AddWithValue("wh", wh);
            Assert.Equal(55m, Convert.ToDecimal(await cmd.ExecuteScalarAsync()));
        }

        // الأسعار مستقلة بعد كل الحركات — لا يُشتق سعر العلبة من الكيس
        var lookupBag = await storekeeper.GetFromJsonAsync<JsonElement>("/api/v1/catalog/lookup?code=G4-MILK-01-BAG");
        var lookupBox = await storekeeper.GetFromJsonAsync<JsonElement>("/api/v1/catalog/lookup?code=G4-MILK-01-BOX");
        Assert.Equal(5.00m, lookupBag.GetProperty("sku").GetProperty("prices")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(55.00m, lookupBox.GetProperty("sku").GetProperty("prices")[0].GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task MandatoryScenario_WeightSku_Sale350g_D07Reference()
    {
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);
        var cheeseSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-CHEESE-01-KG");
        var wh = _fx.ScenarioWarehouseId;

        var receive = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{cheeseSkuId}/stock-movements", new
        {
            movementType = "RECEIVE",
            warehouseId = wh,
            quantity = 3m,
            sourceUomId = 2,
            sourceDocType = "g4.weight",
            sourceDocId = 992001,
        });
        Assert.Equal(HttpStatusCode.Created, receive.StatusCode);
        Assert.Equal(3m, (await receive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("baseQty").GetDecimal());

        // بيع 350 جرام (المثال المرجعي D-07: 200 EGP/kg × 70 EGP) — يُرحّل بالوحدة الأساسية كجم
        var sale = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{cheeseSkuId}/stock-movements", new
        {
            movementType = "SALE",
            warehouseId = wh,
            quantity = 350m,
            sourceUomId = 3,
            sourceDocType = "g4.weight",
            sourceDocId = 992002,
        });
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
        var saleBody = await sale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.350m, saleBody.GetProperty("baseQty").GetDecimal());
        Assert.Equal(0.001m, saleBody.GetProperty("conversionFactor").GetDecimal());

        var ret = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{cheeseSkuId}/stock-movements", new
        {
            movementType = "SALE_RETURN",
            warehouseId = wh,
            quantity = 350m,
            sourceUomId = 3,
            sourceDocType = "g4.weight",
            sourceDocId = 992003,
        });
        Assert.Equal(HttpStatusCode.Created, ret.StatusCode);

        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT base_qty FROM inv.stock_balances WHERE sku_id=@s AND warehouse_id=@wh", conn);
        cmd.Parameters.AddWithValue("s", cheeseSkuId);
        cmd.Parameters.AddWithValue("wh", wh);
        Assert.Equal(3m, Convert.ToDecimal(await cmd.ExecuteScalarAsync()));   // 3 - 0.350 + 0.350
    }

    [Fact]
    public async Task MandatoryScenario_SerializedSku_SerialsRequired()
    {
        using var owner = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var payload = new
        {
            code = "G4-DEVICE-01",
            nameAr = "ميزان مكتبي رقمي",
            categoryId = _fx.CategoryId,
            trackLots = false,
            trackSerials = true,
            skus = new object[]
            {
                new { skuCode = "G4-DEVICE-01-SN", saleMode = "SERIALIZED", baseUomId = 1, saleUomId = 1, reorderLevel = 0, weight = (object?)null },
            },
            barcodes = new object[] { new { barcodeValue = "6291041500244", barcodeType = "EAN13", isPrintedLabel = false, skuOrdinal = 0 } },
            plus = Array.Empty<object>(),
            prices = new object[] { new { skuOrdinal = 0, priceListId = _fx.PriceListId, unitId = 1, unitPrice = 1500m, taxTreatment = "TAXABLE" } },
            packages = Array.Empty<object>(),
        };
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/catalog/products", payload)).StatusCode);

        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);
        var skuId = await CatalogApiFixture.GetSkuIdAsync("G4-DEVICE-01-SN");
        var wh = _fx.ScenarioWarehouseId;

        var receive = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{skuId}/stock-movements", new
        {
            movementType = "RECEIVE",
            warehouseId = wh,
            quantity = 2,
            sourceUomId = 1,
            sourceDocType = "g4.serial",
            sourceDocId = 993001,
            serials = ReceiveSerials,
        });
        Assert.Equal(HttpStatusCode.Created, receive.StatusCode);
        Assert.Equal(2m, (await receive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("baseQty").GetDecimal());

        var sale = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{skuId}/stock-movements", new
        {
            movementType = "SALE",
            warehouseId = wh,
            quantity = 1,
            sourceUomId = 1,
            sourceDocType = "g4.serial",
            sourceDocId = 993002,
            serials = SaleSerial,
        });
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
        Assert.Equal(1m, (await sale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("baseQty").GetDecimal());

        // سيريال بلا قائمة → رفض صريح (وحدة لكل سيريال)
        var noSerials = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{skuId}/stock-movements", new
        {
            movementType = "SALE",
            warehouseId = wh,
            quantity = 1,
            sourceUomId = 1,
            sourceDocType = "g4.serial",
            sourceDocId = 993003,
        });
        Assert.Equal(HttpStatusCode.BadRequest, noSerials.StatusCode);
    }

    // ── سياسات الدفتر (D-03 والتوثيق) ───────────────────────────────────

    [Fact]
    public async Task Movement_MakingNegativeWithoutApproval_409_D03()
    {
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);
        var bagSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BAG");
        var response = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{bagSkuId}/stock-movements", new
        {
            movementType = "SALE",
            warehouseId = _fx.WarehouseId,
            quantity = 99999m,
            sourceUomId = 1,
            sourceDocType = "g4.negative",
            sourceDocId = 994001,
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);   // D-03: SUPERVISOR_APPROVAL
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("اعتماد مشرف", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Movement_StocktakeWithoutReason_400()
    {
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);
        var bagSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BAG");
        var response = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{bagSkuId}/stock-movements", new
        {
            movementType = "ADJUSTMENT_OUT",
            warehouseId = _fx.WarehouseId,
            quantity = 1m,
            sourceUomId = 1,
            sourceDocType = "g4.stocktake",
            sourceDocId = 994100,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Movement_UnknownWarehouse_400_Friendly()
    {
        using var storekeeper = await ClientAsync("g4.storekeeper", "Storekeeper#G4-Pass", _fx.BranchId);
        var bagSkuId = await CatalogApiFixture.GetSkuIdAsync("G4-MILK-01-BAG");
        var response = await storekeeper.PostAsJsonAsync($"/api/v1/catalog/skus/{bagSkuId}/stock-movements", new
        {
            movementType = "RECEIVE",
            warehouseId = 999999,
            quantity = 1m,
            sourceUomId = 1,
            sourceDocType = "g4.wh",
            sourceDocId = 994200,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("المخزن غير موجود", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Ledger_AppendOnly_UpdateRejectedByTrigger()
    {
        // الدفتر للقراءة فقط بعد الإدخال (§0.1 ق6) — محاولة تعديل ترفضها القاعدة
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE inv.stock_movements SET base_qty = 999 WHERE movement_id = (SELECT movement_id FROM inv.stock_movements ORDER BY movement_id LIMIT 1)", conn);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    // ── حالة المنتج (لا حذف تاريخي) ─────────────────────────────────────

    [Fact]
    public async Task SetProductStatus_Deactivate_Then_Reactivate_WithAudit()
    {
        using var owner = await ClientAsync("g4.owner", "Owner#G4-Pass");
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?query=G4-MILK-01");
        var productId = list.GetProperty("items")[0].GetProperty("productId").GetInt64();

        var off = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{productId}/status",
            new { status = "INACTIVE", reasonAr = "إيقاف مؤقت للاختبار" });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        // فلتر الحالة يعمل خادمياً
        var activeOnly = await owner.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?query=G4-MILK-01&status=ACTIVE");
        Assert.Equal(0, activeOnly.GetProperty("items").GetArrayLength());

        var on = await owner.PostAsJsonAsync($"/api/v1/catalog/products/{productId}/status",
            new { status = "ACTIVE", reasonAr = "إعادة التفعيل" });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.audit_logs WHERE entity_schema='catalog.products' AND entity_id=@p AND action='PRODUCT_STATUS_CHANGE'", conn);
        cmd.Parameters.AddWithValue("p", productId);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) >= 2, "يجب أن يُسجل تغيرا الحالة في التدقيق");
    }
}

/// <summary>
/// تجهيز G4: مستخدمون بأدوار أربعة + فئة وقائمة ومخزنان (عام + سيناريو) +
/// منتجا السيناريو المرجعيان (كيس/علبة 12 وجبنة وزنية بـPLU) — وتنظيف كامل
/// بعدهم بترتيب يحترم الفواصل. البذر هنا يجعل كل فحص مستقلاً عن ترتيب التشغيل.
/// </summary>
public sealed class CatalogApiFixture : IAsyncLifetime
{
    public long CategoryId { get; private set; }
    public long PriceListId { get; private set; }
    public long WarehouseId { get; private set; }          // عام (RBAC/ضجيج)
    public long ScenarioWarehouseId { get; private set; }  // مخصص لتأكيد الأرصدة
    public long BranchId { get; private set; }

    public WeraApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient();   // يقلع ويرحّل حتى رأس V015
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

        BranchId = Scalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G4B1', 'فرع اختبار G4', 'Africa/Cairo') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING branch_id");
        WarehouseId = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({BranchId}, 'G4W1', 'مخزن اختبار G4', 'BRANCH') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");
        ScenarioWarehouseId = Scalar($"INSERT INTO inv.warehouses (branch_id, code, name_ar, wh_type) VALUES ({BranchId}, 'G4W2', 'مخزن سيناريو G4', 'BRANCH') ON CONFLICT (code) DO UPDATE SET branch_id = EXCLUDED.branch_id RETURNING warehouse_id");
        CategoryId = Scalar("INSERT INTO catalog.categories (code, name_ar) VALUES ('G4CAT', 'فئة اختبار G4') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING category_id");
        PriceListId = Scalar("SELECT price_list_id FROM catalog.price_lists WHERE code='DEFAULT_EGP'");

        void UpsertUser(string username, string display, string password, string roleCode)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
            Scalar($"INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('{username}', '{display}', '{hash}', true) ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash");
            var userId = Scalar($"SELECT user_id FROM core.users WHERE username = '{username}'");
            Scalar($"DELETE FROM core.user_roles WHERE user_id = {userId}");
            Scalar($"INSERT INTO core.user_roles (user_id, role_id, branch_id) VALUES ({userId}, (SELECT role_id FROM core.roles WHERE code='{roleCode}'), NULL)");
        }

        UpsertUser("g4.owner", "مالك G4", "Owner#G4-Pass", "OWNER");
        UpsertUser("g4.manager", "مدير G4", "Manager#G4-Pass", "MANAGER");
        UpsertUser("g4.storekeeper", "أمين مخزن G4", "Storekeeper#G4-Pass", "STOREKEEPER");
        UpsertUser("g4.cashier", "كاشير G4", "Cashier#G4-Pass", "CASHIER");

        // منتجا السيناريو المرجعيان — عبر الـAPI نفسه (نفس مسار الإنتاج)
        var owner = await LoginAsync("g4.owner", "Owner#G4-Pass");
        using var authed = CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", owner.AccessToken);

        var milk = CatalogEndpointTests.BagAndBoxPayload("G4-MILK-01", "G4-MILK-01-BAG", "G4-MILK-01-BOX",
            "4006381333931", "4006381333948", CategoryId, PriceListId);
        var milkResponse = await authed.PostAsJsonAsync("/api/v1/catalog/products", milk);
        Assert.True(milkResponse.IsSuccessStatusCode,
            "بذر منتج الكيس/العلبة فشل: " + await milkResponse.Content.ReadAsStringAsync());

        var cheese = CatalogEndpointTests.CheeseWeightPayload(CategoryId, PriceListId);
        var cheeseResponse = await authed.PostAsJsonAsync("/api/v1/catalog/products", cheese);
        Assert.True(cheeseResponse.IsSuccessStatusCode,
            "بذر منتج الجبنة الوزنية فشل: " + await cheeseResponse.Content.ReadAsStringAsync());
    }

    public static async Task<long> GetSkuIdAsync(string skuCode)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT sku_id FROM catalog.skus WHERE sku_code = @c", conn);
        cmd.Parameters.AddWithValue("c", skuCode);
        var result = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        Assert.NotEqual(0L, result);
        return result;
    }

    public async Task DisposeAsync()
    {
        // الدفتر Append-only: مشغّل القاعدة يمنع DELETE صفّاً صفّاً (§0.1 ق6) —
        // تنظيف قاعدة الاختبار (لا الإنتاج) يفرغ الدفتر والأرصدة بـTRUNCATE
        // التي لا تطلق مشغّلات DELETE، ثم يحذف ملحقات المنتجات والمنتجات
        // والتجهيزات وأخيراً تدقيق/جلسات/مستخدمو G4 — بترتيب يحترم الفواصل.
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cleanup = new NpgsqlCommand(
            "TRUNCATE inv.stock_movements, inv.stock_balances; " +
            "DELETE FROM catalog.barcodes WHERE sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G4-%'); " +
            "DELETE FROM catalog.plus WHERE sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G4-%'); " +
            "DELETE FROM catalog.product_prices WHERE sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G4-%'); " +
            "DELETE FROM catalog.packages WHERE package_sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G4-%') OR content_sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G4-%'); " +
            "DELETE FROM catalog.skus WHERE sku_code LIKE 'G4-%'; " +
            "DELETE FROM catalog.products WHERE code LIKE 'G4-%'; " +
            "DELETE FROM core.refresh_tokens WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g4.%'); " +
            "DELETE FROM audit.user_sessions WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g4.%'); " +
            "DELETE FROM audit.audit_logs WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g4.%'); " +
            "DELETE FROM audit.audit_logs WHERE entity_schema IN ('catalog.products','inv.stock_movements'); " +
            "DELETE FROM audit.audit_logs WHERE branch_id IN (SELECT branch_id FROM core.branches WHERE code='G4B1'); " +
            "DELETE FROM inv.warehouses WHERE code IN ('G4W1','G4W2'); " +
            "DELETE FROM catalog.categories WHERE code = 'G4CAT'; " +
            "DELETE FROM core.user_roles WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g4.%'); " +
            "DELETE FROM core.users WHERE username LIKE 'g4.%'; " +
            "DELETE FROM core.branches WHERE code = 'G4B1'; ", conn);
        await cleanup.ExecuteNonQueryAsync();
        Factory.Dispose();
    }
}
