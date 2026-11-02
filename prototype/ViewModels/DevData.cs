// DevData.cs — بيانات عرض النموذج فقط · كل صف موسوم DEV DATA
// القاعدة: لا تدخل هذه البيانات أي بيئة إنتاج، ولا تُحسب أدلة قبول وظيفية.
using Microsoft.UI.Xaml.Media;

namespace WeraPrototype.ViewModels;

public class AlertItem
{
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Severity { get; init; } = "";
    public Brush SeverityBrush { get; init; } = Microsoft.UI.Xaml.Application.Current.Resources["WeraStatusWarningBrush"] as Brush!;
}

public class ProductRow
{
    public string Name { get; init; } = "";
    public string Sku { get; init; } = "";
    public string Barcode { get; init; } = "";
    public string Category { get; init; } = "";
    public string SellForm { get; init; } = "";
    public string Unit { get; init; } = "";
    public string PriceInclVat { get; init; } = "";   // D-01: السعر المعروض شامل الضريبة
    public string Stock { get; init; } = "";
    public string Status { get; init; } = "";
}

public class CartLine
{
    public string Name { get; init; } = "";
    public string Sku { get; init; } = "";
    public string QtyOrWeight { get; init; } = "";
    public string UnitPrice { get; init; } = "";
    public string Discount { get; init; } = "";
    public string Total { get; init; } = "";
}

public static class DevData
{
    public const string Tag = "DEV DATA — G2 PROTOTYPE";

    public static readonly AlertItem[] Alerts =
    {
        new() { Title = "فارق وردية ليلية #46 تجاوز الحد (-62.00 ج.م)", Source = "الورديات", Severity = "بانتظار اعتماد",
                SeverityBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["WeraStatusDangerBrush"]! },
        new() { Title = "فاتورة مورد INV-2041 مطابقة جزئية (فرق 2 وحدة)", Source = "المشتريات", Severity = "مطابقة جزئية" },
        new() { Title = "5 أصناف نافدة بالكامل (منها أرز مصري 1كجم)", Source = "المخزون", Severity = "نافد",
                SeverityBrush = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["WeraStatusDangerBrush"]! },
        new() { Title = "طابعة إيصالات المحطة 2 لم تعتمد بعد (NOT_CONFIGURED)", Source = "الأجهزة", Severity = "بانتظار الجرد" },
    };

    public static readonly ProductRow[] Products =
    {
        new() { Name = "جبنة رومي بلدي طازجة", Sku = "SKU-100294", Barcode = "6221031492015", Category = "ألبان وأجبان", SellForm = "وزني", Unit = "كجم", PriceInclVat = "189.00 ج.م", Stock = "324.500 كجم", Status = "نشط" },
        new() { Name = "أرز مصري كامل الحبة", Sku = "SKU-100295", Barcode = "6221031492039", Category = "بقالة", SellForm = "قطعة", Unit = "كيس 1كجم", PriceInclVat = "32.50 ج.م", Stock = "0 وحدة", Status = "نافد" },
        new() { Name = "مشروب غازي عائلي — علبة 12 كيس", Sku = "SKU-204510", Barcode = "6221031741003", Category = "مشروبات", SellForm = "عبوة", Unit = "علبة 12", PriceInclVat = "54.00 ج.م", Stock = "1,204 وحدة", Status = "نشط" },
        new() { Name = "زيت زيتون بكر — عبوة موزونة", Sku = "SKU-100301", Barcode = "PLU-3021", Category = "زيوت", SellForm = "وزني", Unit = "كجم", PriceInclVat = "285.00 ج.م", Stock = "86.200 كجم", Status = "نشط" },
        new() { Name = "شوكولاتة لبن — قطعة", Sku = "SKU-310022", Barcode = "6221103990012", Category = "حلويات", SellForm = "قطعة", Unit = "قطعة", PriceInclVat = "12.00 ج.م", Stock = "5,318 وحدة", Status = "نشط" },
    };

    public static readonly CartLine[] Cart =
    {
        new() { Name = "جبنة رومي بلدي", Sku = "SKU-100294", QtyOrWeight = "0.350 كجم", UnitPrice = "200.00", Discount = "—", Total = "70.00" },
        new() { Name = "أرز مصري 1كجم", Sku = "6221031492039", QtyOrWeight = "2 × كيس", UnitPrice = "32.50", Discount = "—", Total = "65.00" },
        new() { Name = "مشروب غازي عائلي", Sku = "SKU-204510", QtyOrWeight = "1 × علبة", UnitPrice = "54.00", Discount = "-5%", Total = "51.30" },
        new() { Name = "شوكولاتة لبن", Sku = "6221103990012", QtyOrWeight = "3 × قطعة", UnitPrice = "12.00", Discount = "—", Total = "36.00" },
    };

    // D-07 المحسوم (ترحيل V011): دقة 1g · خطوة 5g · Banker's للقرش · أقرب خطوة
    public const int WeightPrecisionG = 1;
    public const int WeightStepG = 5;
    public const decimal RefPricePerKg = 200.00m;
    public const decimal TargetAmount = 70.00m;
    // المثال المرجعي: 70.00 ÷ 200.00 = 0.350 كجم = 350g — على الخطوة بلا تعديل.
}
