// WeightWindow.xaml.cs — الحساب الحي بإعدادات D-07 المحسومة (DEV DATA)
// القاعدة: الدقة/الخطوة/التقريب تُقرأ من إعدادات النشاط (core.business_settings) لا ثوابت كود.
// في النموذج تُحاكى القيم المحسومة: 1g / 5g / Banker's 0.01 / أقرب خطوة.
using Microsoft.UI.Xaml;
using System.Globalization;

namespace WeraPrototype.Views;

public sealed partial class WeightWindow : Window
{
    private readonly decimal _pricePerKg;
    private const int StepG = 5;                 // D-07: أقرب خطوة (من الإعدادات)
    private const decimal Epsilon = 0.005m;      // لتعويض خطأ الفاصلة العائمة في القسمة

    public WeightWindow(decimal pricePerKg)
    {
        InitializeComponent();
        _pricePerKg = pricePerKg;
        Title = "نافذة الوزن (DEV DATA — G2 PROTOTYPE)";
        Recalculate(DevData.TargetAmount);
    }

    private void AmountBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (decimal.TryParse(AmountBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            Recalculate(amount);
    }

    /// <summary>وزن من مبلغ: (المبلغ ÷ سعر الكجم) × 1000 غرام ثم تقريب لأقرب خطوة معتمدة.</summary>
    private void Recalculate(decimal amount)
    {
        var exactG = amount / _pricePerKg * 1000m;
        var steppedG = Math.Round(exactG / StepG, MidpointRounding.ToEven) * StepG;
        var finalAmount = Math.Round(steppedG * _pricePerKg / 1000m, 2, MidpointRounding.ToEven); // Banker's
        var adjusted = Math.Abs(exactG - Math.Floor(exactG)) < Epsilon && exactG != steppedG;

        DisplayedWeight.Text = $"{steppedG:0} g";
        ExactWeight.Text = $"{exactG:0.000} g";
        LiveCalc.Text = $"{amount:0.00} ج.م ÷ {_pricePerKg:0.00} ج.م/كجم = {exactG / 1000m:0.000} كجم " +
                        $"(المثال المرجعي D-07: 70 ÷ 200 = 350g على الخطوة بلا تعديل)\n" +
                        $"الوزن على الخطوة: {steppedG:0} g · المبلغ النهائي بعد Banker's: {finalAmount:0.00} ج.م" +
                        (adjusted ? " · حُسب المبلغ من الوزن المُقرَّب" : "");
    }

    private void AddToCart_Click(object sender, RoutedEventArgs e) => Close();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
