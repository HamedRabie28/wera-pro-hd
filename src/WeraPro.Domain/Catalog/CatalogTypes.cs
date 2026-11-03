namespace WeraPro.Domain.Catalog;

/// <summary>
/// صيغة البيع الصريحة (§4.2) — لا تُستنتج أبداً من الوحدة أو السعر؛
/// كل SKU تعلن صيغتها، وبها تتحدد حقول الوزن وطريقة الخصم من المخزون.
/// </summary>
public enum SaleMode
{
    /// <summary>عددي بالقطعة — المخزون يخصم qty × عامل تحويل الوحدة.</summary>
    Count,
    /// <summary>وزني متغير — الوزن إلزامي بمصدر موثق (§9.3).</summary>
    Weight,
    /// <summary>عبوة بعامل معروف — بيع العلبة يخصم units_per_package من رصيد المحتوى (أثر واحد).</summary>
    Pack,
    /// <summary>سيريال لكل وحدة — وحدة لكل serial_id.</summary>
    Serialized,
}

/// <summary>أنماط أبعاد الوحدات (§4.1) — التحويل داخل البعد فقط.</summary>
public enum UnitDimension
{
    Count,
    Mass,
    Volume,
    Length,
}

/// <summary>رموز صيغ البيع كما في قاعدة البيانات (V002 catalog.skus.sale_mode).</summary>
public static class SaleModeCodes
{
    public const string Count = "COUNT";
    public const string Weight = "WEIGHT";
    public const string Pack = "PACK";
    public const string Serialized = "SERIALIZED";

    public static string ToCode(SaleMode mode) => mode switch
    {
        SaleMode.Count => Count,
        SaleMode.Weight => Weight,
        SaleMode.Pack => Pack,
        SaleMode.Serialized => Serialized,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static SaleMode FromCode(string code) => code switch
    {
        Count => SaleMode.Count,
        Weight => SaleMode.Weight,
        Pack => SaleMode.Pack,
        Serialized => SaleMode.Serialized,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "صيغة بيع غير معروفة"),
    };
}

/// <summary>رموز الأبعاد كما في قاعدة البيانات (V002 catalog.units.dimension).</summary>
public static class UnitDimensionCodes
{
    public const string Count = "COUNT";
    public const string Mass = "MASS";
    public const string Volume = "VOLUME";
    public const string Length = "LENGTH";

    public static string ToCode(UnitDimension dimension) => dimension switch
    {
        UnitDimension.Count => Count,
        UnitDimension.Mass => Mass,
        UnitDimension.Volume => Volume,
        UnitDimension.Length => Length,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    public static UnitDimension FromCode(string code) => code switch
    {
        Count => UnitDimension.Count,
        Mass => UnitDimension.Mass,
        Volume => UnitDimension.Volume,
        Length => UnitDimension.Length,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "بُعد وحدة غير معروف"),
    };
}

/// <summary>أنواع الباركود المعتمدة (V002 catalog.barcodes.barcode_type).</summary>
public static class BarcodeTypes
{
    public const string Ean13 = "EAN13";
    public const string Ean8 = "EAN8";
    public const string UpcA = "UPC_A";
    public const string UpcE = "UPC_E";
    public const string Code128 = "CODE128";
    public const string Code39 = "CODE39";
    /// <summary>GS1-128 — اسم بلا شرطة سفلية التزاماً بـCA1707؛ القيمة كما في القاعدة.</summary>
    public const string Gs1 = "GS1_128";
    public const string Internal = "INTERNAL";

    /// <summary>الأنواع الرقمية القياسية — تنمّط أرقاماً فقط ولها رقم تحقق MOD10 موزون.</summary>
    public static readonly IReadOnlySet<string> NumericRetail =
        new HashSet<string>(StringComparer.Ordinal) { Ean13, Ean8, UpcA, UpcE };
}

/// <summary>أنواع حركة المخزون (V003 inv.stock_movements.movement_type) واتجاهها المشتق.</summary>
public static class StockMovementTypes
{
    public const string Opening = "OPENING";
    public const string Receive = "RECEIVE";
    public const string Sale = "SALE";
    public const string SaleReturn = "SALE_RETURN";
    public const string SupplierReturn = "SUPPLIER_RETURN";
    public const string TransferOut = "TRANSFER_OUT";
    public const string TransferIn = "TRANSFER_IN";
    public const string AdjustmentIn = "ADJUSTMENT_IN";
    public const string AdjustmentOut = "ADJUSTMENT_OUT";
    public const string Damage = "DAMAGE";
    public const string Expiry = "EXPIRY";

    /// <summary>الاتجاه مشتق من النوع (قيد mov_direction_type) — 1 دخول / -1 خروج.</summary>
    public static int DirectionOf(string movementType) => movementType switch
    {
        Opening or Receive or SaleReturn or TransferIn or AdjustmentIn => 1,
        Sale or SupplierReturn or TransferOut or AdjustmentOut or Damage or Expiry => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(movementType), movementType, "نوع حركة مخزون غير معروف"),
    };

    /// <summary>الحركات التي يقودها مستند شراء/استلام (إثبات تحويل المخزون في G4).</summary>
    public static readonly IReadOnlySet<string> PurchaseKinds =
        new HashSet<string>(StringComparer.Ordinal) { Opening, Receive };

    /// <summary>حركات البيع والمرتجع (إثبات G4؛ مستنداتها الكاملة في G7).</summary>
    public static readonly IReadOnlySet<string> SaleKinds =
        new HashSet<string>(StringComparer.Ordinal) { Sale, SaleReturn };

    /// <summary>حركات الجرد والتسويات (إثبات G4؛ الجرد الكامل في G6).</summary>
    public static readonly IReadOnlySet<string> StocktakeKinds =
        new HashSet<string>(StringComparer.Ordinal) { AdjustmentIn, AdjustmentOut };
}
