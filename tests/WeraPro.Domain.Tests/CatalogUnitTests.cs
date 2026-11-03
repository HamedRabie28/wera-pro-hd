namespace WeraPro.Domain.Tests;

using WeraPro.Domain.Catalog;
using WeraPro.Domain.SharedKernel;
using Xunit;

/// <summary>وحدات بذر V009 — معرفات ثابتة للاختبارات.</summary>
internal static class SeedUnits
{
    public const long Piece = 1;
    public const long Kg = 2;
    public const long G = 3;
    public const long L = 4;
    public const long Ml = 5;
    public const long M = 6;
    public const long Cm = 7;

    public static UnitConversionGraph BuildGraph(params ConversionDefinition[] extra)
    {
        var units = new List<UnitDefinition>
        {
            new(Piece, "PIECE", UnitDimension.Count, IsBase: true, Decimals: 0),
            new(Kg, "KG", UnitDimension.Mass, IsBase: false, Decimals: 3),
            new(G, "G", UnitDimension.Mass, IsBase: true, Decimals: 0),
            new(L, "L", UnitDimension.Volume, IsBase: false, Decimals: 3),
            new(Ml, "ML", UnitDimension.Volume, IsBase: true, Decimals: 0),
            new(M, "M", UnitDimension.Length, IsBase: false, Decimals: 2),
            new(Cm, "CM", UnitDimension.Length, IsBase: true, Decimals: 0),
        };
        var conversions = new List<ConversionDefinition>
        {
            new(Kg, G, 1000m),
            new(L, Ml, 1000m),
            new(M, Cm, 100m),
        };
        conversions.AddRange(extra);
        var result = UnitConversionGraph.TryCreate([.. units], [.. conversions]);
        Assert.True(result.IsSuccess, "مخطط بذر V009 يجب أن يبني بنجاح: " + result.Error?.MessageAr);
        return result.Value!;
    }
}

/// <summary>مخطط تحويل الوحدات (§4.1): أبعاد · دورات · مسارات · عوامل.</summary>
public class UnitConversionGraphTests
{
    [Fact]
    public void SeedGraph_BuildsCleanly()
    {
        var graph = SeedUnits.BuildGraph();
        Assert.Equal(7, graph.Units.Count);
        Assert.Equal(3, graph.Conversions.Count);
    }

    [Fact]
    public void SameDimension_ConversionAllowed()
    {
        var graph = SeedUnits.BuildGraph();
        var factor = graph.Factor(SeedUnits.Kg, SeedUnits.G);
        Assert.True(factor.IsSuccess);
        Assert.Equal(1000m, factor.Value);
    }

    [Fact]
    public void ReverseConversion_DividesFactor()
    {
        var graph = SeedUnits.BuildGraph();
        var factor = graph.Factor(SeedUnits.G, SeedUnits.Kg);
        Assert.True(factor.IsSuccess);
        Assert.Equal(0.001m, factor.Value);
    }

    [Fact]
    public void CrossDimension_Rejected()
    {
        var graph = SeedUnits.BuildGraph();
        var factor = graph.Factor(SeedUnits.Kg, SeedUnits.L);          // MASS → VOLUME
        Assert.False(factor.IsSuccess);
        Assert.Equal("UNIT_DIMENSION_MISMATCH", factor.Error!.Code);

        var pieceToKg = graph.Factor(SeedUnits.Piece, SeedUnits.Kg);   // COUNT → MASS
        Assert.False(pieceToKg.IsSuccess);
        Assert.Equal("UNIT_DIMENSION_MISMATCH", pieceToKg.Error!.Code);
    }

    [Fact]
    public void MissingPath_RejectedExplicitly()
    {
        var graph = SeedUnits.BuildGraph();
        var factor = graph.Factor(SeedUnits.Piece, SeedUnits.Cm);      // بُعدان مختلفان بلا مسار
        Assert.False(factor.IsSuccess);
    }

    [Fact]
    public void DirectCycle_Rejected()
    {
        var units = new List<UnitDefinition>
        {
            new(1, "KG", UnitDimension.Mass, false, 3),
            new(2, "G", UnitDimension.Mass, true, 0),
        };
        var result = UnitConversionGraph.TryCreate(units,
            [new ConversionDefinition(1, 2, 1000m), new ConversionDefinition(2, 1, 0.001m)]);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNIT_CONVERSION_CYCLE", result.Error!.Code);
    }

    /// <summary>T-CAT-006: الدورة الثلاثية (أ→ب→ج→أ) تمنعها خدمة المجال لا القاعدة فقط.</summary>
    [Fact]
    public void ThreeNodeCycle_Rejected_DomainService()
    {
        var units = new List<UnitDefinition>
        {
            new(1, "U1", UnitDimension.Mass, false, 0),
            new(2, "U2", UnitDimension.Mass, false, 0),
            new(3, "U3", UnitDimension.Mass, true, 0),
        };
        var result = UnitConversionGraph.TryCreate(units,
        [
            new ConversionDefinition(1, 2, 10m),
            new ConversionDefinition(2, 3, 10m),
            new ConversionDefinition(3, 1, 0.01m),   // يغلق الدورة
        ]);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNIT_CONVERSION_CYCLE", result.Error!.Code);
    }

    [Fact]
    public void NonPositiveFactor_Rejected()
    {
        var units = new List<UnitDefinition>
        {
            new(1, "U1", UnitDimension.Mass, false, 0),
            new(2, "U2", UnitDimension.Mass, true, 0),
        };
        var result = UnitConversionGraph.TryCreate(units, [new ConversionDefinition(1, 2, 0m)]);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNIT_CONVERSION_FACTOR_INVALID", result.Error!.Code);
    }

    [Fact]
    public void UnknownUnitReference_Rejected()
    {
        var units = new List<UnitDefinition> { new(1, "U1", UnitDimension.Mass, true, 0) };
        var result = UnitConversionGraph.TryCreate(units, [new ConversionDefinition(1, 99, 2m)]);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNIT_CONVERSION_UNKNOWN_UNIT", result.Error!.Code);
    }

    [Fact]
    public void AmbiguousTwoPaths_Rejected()
    {
        // مساران من 1 إلى 4 بعاملين مختلفين — تعريف غير متسق يرفض لا يخمن
        var units = new List<UnitDefinition>
        {
            new(1, "U1", UnitDimension.Mass, false, 0),
            new(2, "U2", UnitDimension.Mass, false, 0),
            new(3, "U3", UnitDimension.Mass, false, 0),
            new(4, "U4", UnitDimension.Mass, true, 0),
        };
        var result = UnitConversionGraph.TryCreate(units,
        [
            new ConversionDefinition(1, 2, 10m),
            new ConversionDefinition(2, 4, 10m),      // 1→2→4 = 100
            new ConversionDefinition(1, 3, 5m),
            new ConversionDefinition(3, 4, 10m),      // 1→3→4 = 50 ≠ 100
        ]);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNIT_CONVERSION_AMBIGUOUS", result.Error!.Code);
    }

    [Fact]
    public void SameUnit_FactorIsOne()
    {
        var graph = SeedUnits.BuildGraph();
        var factor = graph.Factor(SeedUnits.Kg, SeedUnits.Kg);
        Assert.True(factor.IsSuccess);
        Assert.Equal(1m, factor.Value);
    }
}

/// <summary>تنميط الباركود وأرقام التحقق (§3.1 Exact Match).</summary>
public class BarcodeNormalizerTests
{
    [Fact]
    public void Ean13_ValidCheckDigit_Passes()
    {
        // 4006381333931 — EAN-13 صحيح
        var result = BarcodeNormalizer.Validate("4006381333931", BarcodeTypes.Ean13);
        Assert.True(result.IsSuccess);
        Assert.Equal("4006381333931", result.Value);
    }

    [Fact]
    public void Ean13_BrokenCheckDigit_Rejected()
    {
        var result = BarcodeNormalizer.Validate("4006381333932", BarcodeTypes.Ean13);
        Assert.False(result.IsSuccess);
        Assert.Equal("BARCODE_CHECK_DIGIT_INVALID", result.Error!.Code);
    }

    [Fact]
    public void Ean13_TrimAndSpacesNormalizeToDigits()
    {
        var result = BarcodeNormalizer.Validate("  4006381333931 ", BarcodeTypes.Ean13);
        Assert.True(result.IsSuccess);
        Assert.Equal("4006381333931", result.Value);
    }

    [Fact]
    public void Ean13_WrongLength_Rejected()
    {
        var result = BarcodeNormalizer.Validate("400638133393", BarcodeTypes.Ean13);   // 12 خانة
        Assert.False(result.IsSuccess);
        Assert.Equal("BARCODE_INVALID_LENGTH", result.Error!.Code);
    }

    [Fact]
    public void Ean13_NonDigits_Rejected()
    {
        var result = BarcodeNormalizer.Validate("40063A1333931", BarcodeTypes.Ean13);
        Assert.False(result.IsSuccess);
        Assert.Equal("BARCODE_INVALID_CHARACTERS", result.Error!.Code);
    }

    [Fact]
    public void Code128_UppercaseTrim_NoCheckDigit()
    {
        var result = BarcodeNormalizer.Validate("  ab-123 ", BarcodeTypes.Code128);
        Assert.True(result.IsSuccess);
        Assert.Equal("AB-123", result.Value);
    }

    [Fact]
    public void ComposeEan13_RoundTrips()
    {
        var composed = BarcodeNormalizer.ComposeEan13("400638133393");
        Assert.True(composed.IsSuccess);
        var validated = BarcodeNormalizer.Validate(composed.Value!, BarcodeTypes.Ean13);
        Assert.True(validated.IsSuccess);
        Assert.Equal("4006381333931", validated.Value);
    }

    [Fact]
    public void NormalizeForLookup_DigitsStayDigits_OtherUppercased()
    {
        Assert.Equal("6221031492173", BarcodeNormalizer.NormalizeForLookup(" 6221031492173 "));
        Assert.Equal("SKU-9", BarcodeNormalizer.NormalizeForLookup("sku-9"));
    }
}

/// <summary>باركود الوزن/السعر المتغير (§5.3 ق6): من الملفات فقط — لا تخمين.</summary>
public class VariableWeightBarcodeParserTests
{
    private static VariableWeightProfile StdProfile() => new(
        ProfileId: 1,
        Name: "PREFIX20-13D-WEIGHT-G",
        Prefix: "20",
        TotalLength: 13,
        PluStartPos: 3, PluLength: 5,        // 2 بادئة + 5 PLU + 5 وزن + 1 تحقق = 13
        ValueType: "WEIGHT",
        ValueStartPos: 8, ValueLength: 5,    // 5 خانات × 3 كسور = 999.999 كجم
        ValueDecimals: 3,
        ValueUnitId: SeedUnits.G,
        CheckDigitRule: "MOD10_WEIGHTED");

    [Fact]
    public void ValidWeightBarcode_ResolvesPluAndWeight()
    {
        // 20 + PLU=1234 + وزن 350g (00350 × 10^-3 = 0.350) + رقم تحقق
        // نولّد رقم تحقق MOD10_WEIGHTED صحيحاً على الجسم 200123400350
        var body = "200123400350";
        var weight = 0;
        var factor = 3;
        for (var i = body.Length - 1; i >= 0; i--)
        {
            weight += (body[i] - '0') * factor;
            factor = factor == 3 ? 1 : 3;
        }
        var check = (10 - weight % 10) % 10;
        var barcode = body + check.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var result = VariableWeightBarcodeParser.Parse(barcode, [StdProfile()]);
        Assert.True(result.IsSuccess);
        Assert.Equal("1234", result.Value!.PluCode);
        Assert.Equal(0.350m, result.Value.Value);
        Assert.Equal("WEIGHT", result.Value.ValueType);
    }

    [Fact]
    public void UnknownPrefix_RejectedExplicitly()
    {
        var result = VariableWeightBarcodeParser.Parse("9901234003501", [StdProfile()]);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNKNOWN_WEIGHT_BARCODE_FORMAT", result.Error!.Code);
    }

    [Fact]
    public void WrongLength_Rejected()
    {
        var result = VariableWeightBarcodeParser.Parse("201234003501", [StdProfile()]);   // 12 خانة
        Assert.False(result.IsSuccess);
        Assert.Equal("UNKNOWN_WEIGHT_BARCODE_FORMAT", result.Error!.Code);
    }

    [Fact]
    public void BrokenCheckDigit_Rejected()
    {
        var result = VariableWeightBarcodeParser.Parse("2001234003509", [StdProfile()]);
        Assert.False(result.IsSuccess);
        Assert.Equal("WEIGHT_BARCODE_CHECK_DIGIT_INVALID", result.Error!.Code);
    }

    [Fact]
    public void NoProfilesAtAll_Rejected()
    {
        var result = VariableWeightBarcodeParser.Parse("2001234003501", []);
        Assert.False(result.IsSuccess);
        Assert.Equal("UNKNOWN_WEIGHT_BARCODE_FORMAT", result.Error!.Code);
    }

    [Fact]
    public void NonNumeric_Rejected()
    {
        var result = VariableWeightBarcodeParser.Parse("20A1234003501", [StdProfile()]);
        Assert.False(result.IsSuccess);
        Assert.Equal("WEIGHT_BARCODE_NOT_NUMERIC", result.Error!.Code);
    }
}

/// <summary>
/// حاسبة كمية المخزون لكل صيغة بيع (§4.2–4.3) — سيناريو الكيس والعلبة الإلزامي
/// واختبارات البيع/الشراء/المرتجع/الجرد لكل صيغة على مستوى التحويل.
/// </summary>
public class StockQuantityCalculatorTests
{
    private static SkuSaleInfo BagSku() => new(
        SkuId: 101, ProductId: 1, SkuCode: "MILK-BAG", SaleMode: SaleMode.Count,
        BaseUomId: SeedUnits.Piece, SaleUomId: SeedUnits.Piece, MinWeightKg: null, MaxWeightKg: null);

    private static SkuSaleInfo BoxSku() => new(
        SkuId: 102, ProductId: 1, SkuCode: "MILK-BOX", SaleMode: SaleMode.Pack,
        BaseUomId: SeedUnits.Piece, SaleUomId: SeedUnits.Piece, MinWeightKg: null, MaxWeightKg: null);

    private static SkuSaleInfo CheeseSku() => new(
        SkuId: 103, ProductId: 2, SkuCode: "CH-KG", SaleMode: SaleMode.Weight,
        BaseUomId: SeedUnits.Kg, SaleUomId: SeedUnits.Kg, MinWeightKg: 0.100m, MaxWeightKg: 5m);

    private static SkuSaleInfo DeviceSku() => new(
        SkuId: 104, ProductId: 3, SkuCode: "DEV-SN", SaleMode: SaleMode.Serialized,
        BaseUomId: SeedUnits.Piece, SaleUomId: SeedUnits.Piece, MinWeightKg: null, MaxWeightKg: null);

    // ── COUNT ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_FiveBags_BaseQtyFive()
    {
        var snap = StockQuantityCalculator.ResolveCount(BagSku(), 5m, SeedUnits.Piece, SeedUnits.BuildGraph());
        Assert.True(snap.IsSuccess);
        Assert.Equal(5m, snap.Value!.BaseQty);
        Assert.Equal(1m, snap.Value.ConversionFactor);
        Assert.Equal(101, snap.Value.SkuId);
    }

    [Fact]
    public void Count_ZeroOrNegative_Rejected()
    {
        var graph = SeedUnits.BuildGraph();
        Assert.False(StockQuantityCalculator.ResolveCount(BagSku(), 0m, SeedUnits.Piece, graph).IsSuccess);
        Assert.False(StockQuantityCalculator.ResolveCount(BagSku(), -3m, SeedUnits.Piece, graph).IsSuccess);
    }

    [Fact]
    public void Count_WeightModeSku_Rejected()
    {
        var snap = StockQuantityCalculator.ResolveCount(CheeseSku(), 2m, SeedUnits.Kg, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("SALE_MODE_MISMATCH", snap.Error!.Code);
    }

    // ── PACK (كيس مفرد وعلبة 12 كيساً — §4.3) ─────────────────────────────

    [Fact]
    public void Pack_OneBoxOf12Bags_SingleMovementOnContent()
    {
        var package = new PackageInfo(PackageSkuId: 102, ContentSkuId: 101, UnitsPerPackage: 12m, OpenPackageAllowed: true);
        var snap = StockQuantityCalculator.ResolvePack(package, BagSku(), 1m, SeedUnits.Piece);
        Assert.True(snap.IsSuccess);
        Assert.Equal(101, snap.Value!.SkuId);            // الأثر على الكيس (المحتوى)
        Assert.Equal(12m, snap.Value.BaseQty);           // 12 كيساً
        Assert.Equal(12m, snap.Value.ConversionFactor);  // لقطة العامل
    }

    [Fact]
    public void Pack_ThreeBoxes_BaseQtyThirtySix()
    {
        var package = new PackageInfo(102, 101, 12m, true);
        var snap = StockQuantityCalculator.ResolvePack(package, BagSku(), 3m, SeedUnits.Piece);
        Assert.True(snap.IsSuccess);
        Assert.Equal(36m, snap.Value!.BaseQty);
    }

    [Fact]
    public void Pack_NestedPackage_RejectedByUseCaseRule()
    {
        // عبوة داخل عبوة — القاعدة تُفرض في حالة الاستخدام (PACKAGE_CONTENT_NESTED)
        // وهنا نفحص عامل غير موجب فقط على مستوى الحاسبة:
        var badPackage = new PackageInfo(102, 101, 0m, true);
        var snap = StockQuantityCalculator.ResolvePack(badPackage, BagSku(), 1m, SeedUnits.Piece);
        Assert.False(snap.IsSuccess);
        Assert.Equal("PACKAGE_UNITS_INVALID", snap.Error!.Code);
    }

    [Fact]
    public void Pack_ContentMismatch_Rejected()
    {
        var package = new PackageInfo(102, 999, 12m, true);   // المحتوى 999 ≠ الصنف 101
        var snap = StockQuantityCalculator.ResolvePack(package, BagSku(), 1m, SeedUnits.Piece);
        Assert.False(snap.IsSuccess);
        Assert.Equal("PACKAGE_CONTENT_MISMATCH", snap.Error!.Code);
    }

    [Fact]
    public void Pack_ZeroBoxes_Rejected()
    {
        var package = new PackageInfo(102, 101, 12m, true);
        var snap = StockQuantityCalculator.ResolvePack(package, BagSku(), 0m, SeedUnits.Piece);
        Assert.False(snap.IsSuccess);
        Assert.Equal("QTY_NOT_POSITIVE", snap.Error!.Code);
    }

    // ── WEIGHT ────────────────────────────────────────────────────────────

    [Fact]
    public void Weight_350Grams_InBaseKg()
    {
        // المثال المرجعي D-07: 350g — بالوحدة الأساسية كجم = 0.350
        var snap = StockQuantityCalculator.ResolveWeight(CheeseSku(), 350m, SeedUnits.G, SeedUnits.BuildGraph());
        Assert.True(snap.IsSuccess);
        Assert.Equal(0.350m, snap.Value!.BaseQty);
        Assert.Equal(0.001m, snap.Value.ConversionFactor);
        Assert.Equal(SeedUnits.G, snap.Value.SourceUomId);
    }

    [Fact]
    public void Weight_KgDirect_NoConversion()
    {
        var snap = StockQuantityCalculator.ResolveWeight(CheeseSku(), 1.275m, SeedUnits.Kg, SeedUnits.BuildGraph());
        Assert.True(snap.IsSuccess);
        Assert.Equal(1.275m, snap.Value!.BaseQty);
        Assert.Equal(1m, snap.Value.ConversionFactor);
    }

    [Fact]
    public void Weight_ZeroOrNegative_Rejected()
    {
        var graph = SeedUnits.BuildGraph();
        Assert.False(StockQuantityCalculator.ResolveWeight(CheeseSku(), 0m, SeedUnits.Kg, graph).IsSuccess);
        Assert.False(StockQuantityCalculator.ResolveWeight(CheeseSku(), -0.5m, SeedUnits.Kg, graph).IsSuccess);
    }

    [Fact]
    public void Weight_BelowMin_Rejected()
    {
        // 50g < الحد الأدنى 100g
        var snap = StockQuantityCalculator.ResolveWeight(CheeseSku(), 50m, SeedUnits.G, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("WEIGHT_BELOW_MIN", snap.Error!.Code);
    }

    [Fact]
    public void Weight_AboveMax_Rejected()
    {
        // 6 كجم > الحد الأقصى 5 كجم
        var snap = StockQuantityCalculator.ResolveWeight(CheeseSku(), 6m, SeedUnits.Kg, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("WEIGHT_ABOVE_MAX", snap.Error!.Code);
    }

    [Fact]
    public void Weight_NonMassUnit_Rejected()
    {
        var snap = StockQuantityCalculator.ResolveWeight(CheeseSku(), 2m, SeedUnits.L, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("UNIT_DIMENSION_MISMATCH", snap.Error!.Code);
    }

    [Fact]
    public void Weight_CountModeSku_Rejected()
    {
        var snap = StockQuantityCalculator.ResolveWeight(BagSku(), 1m, SeedUnits.Kg, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("SALE_MODE_MISMATCH", snap.Error!.Code);
    }

    // ── SERIALIZED ────────────────────────────────────────────────────────

    [Fact]
    public void Serialized_TwoSerials_BaseQtyTwo()
    {
        var snap = StockQuantityCalculator.ResolveSerialized(DeviceSku(), 2);
        Assert.True(snap.IsSuccess);
        Assert.Equal(2m, snap.Value!.BaseQty);
        Assert.Equal(1m, snap.Value.ConversionFactor);
    }

    [Fact]
    public void Serialized_ZeroSerials_Rejected()
    {
        Assert.False(StockQuantityCalculator.ResolveSerialized(DeviceSku(), 0).IsSuccess);
    }

    // ── الموزّع الموحد ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_DispatchesBySaleMode()
    {
        var graph = SeedUnits.BuildGraph();
        var bag = StockQuantityCalculator.Resolve(BagSku(), 4m, SeedUnits.Piece, graph);
        Assert.True(bag.IsSuccess);
        Assert.Equal(4m, bag.Value!.BaseQty);

        var device = StockQuantityCalculator.Resolve(DeviceSku(), 1m, SeedUnits.Piece, graph,
            serials: ["SN-A"]);
        Assert.True(device.IsSuccess);
        Assert.Equal(1m, device.Value!.BaseQty);

        var box = StockQuantityCalculator.Resolve(BoxSku(), 2m, SeedUnits.Piece, graph,
            package: new PackageInfo(102, 101, 12m, true), contentSku: BagSku());
        Assert.True(box.IsSuccess);
        Assert.Equal(24m, box.Value!.BaseQty);       // علبتان = 24 كيساً

        var cheese = StockQuantityCalculator.Resolve(CheeseSku(), 0.350m, SeedUnits.Kg, graph);
        Assert.True(cheese.IsSuccess);
        Assert.Equal(0.350m, cheese.Value!.BaseQty);
    }

    [Fact]
    public void Resolve_SerializedWithoutSerials_Rejected()
    {
        var snap = StockQuantityCalculator.Resolve(DeviceSku(), 1m, SeedUnits.Piece, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("SERIALS_REQUIRED", snap.Error!.Code);
    }

    [Fact]
    public void Resolve_PackWithoutPackage_Rejected()
    {
        var snap = StockQuantityCalculator.Resolve(BoxSku(), 1m, SeedUnits.Piece, SeedUnits.BuildGraph());
        Assert.False(snap.IsSuccess);
        Assert.Equal("PACKAGE_DEFINITION_REQUIRED", snap.Error!.Code);
    }
}

/// <summary>رموز صيغ البيع والحركة (V002/V003) — الاتجاه مشتق من النوع.</summary>
public class CatalogTypesTests
{
    [Theory]
    [InlineData("COUNT", SaleMode.Count)]
    [InlineData("WEIGHT", SaleMode.Weight)]
    [InlineData("PACK", SaleMode.Pack)]
    [InlineData("SERIALIZED", SaleMode.Serialized)]
    public void SaleModeCodes_RoundTrip(string code, SaleMode mode)
    {
        Assert.Equal(mode, SaleModeCodes.FromCode(code));
        Assert.Equal(code, SaleModeCodes.ToCode(mode));
    }

    [Fact]
    public void SaleModeCodes_Unknown_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleModeCodes.FromCode("PIECE"));
    }

    [Theory]
    [InlineData(StockMovementTypes.Sale, -1)]
    [InlineData(StockMovementTypes.SaleReturn, 1)]
    [InlineData(StockMovementTypes.Receive, 1)]
    [InlineData(StockMovementTypes.Opening, 1)]
    [InlineData(StockMovementTypes.AdjustmentIn, 1)]
    [InlineData(StockMovementTypes.AdjustmentOut, -1)]
    [InlineData(StockMovementTypes.Damage, -1)]
    public void StockMovementTypes_DirectionDerived(string type, int expected)
    {
        Assert.Equal(expected, StockMovementTypes.DirectionOf(type));
    }

    [Fact]
    public void StockMovementTypes_Unknown_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StockMovementTypes.DirectionOf("INVENT"));
    }
}
