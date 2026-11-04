// Views/PartyStatementWindow.xaml.cs — WERA PRO HD Desktop (G6)
// كشف حساب طرف: يعرض افتتاحي/حركة/ختامي من مستندات الخادم المرحّلة فقط.
using Microsoft.UI.Xaml;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class PartyStatementWindow : Window
{
    public PartyStatementWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — كشف حساب";
        App.ActiveWindow = this;
    }

    public void Show(PartyStatementResponse statement, string title)
    {
        TitleText.Text = title;
        OpeningText.Text = statement.OpeningBalance.ToString("N2") + " ج.م";
        ChargesText.Text = "+" + statement.TotalCharges.ToString("N2") + " / −" + statement.TotalCredits.ToString("N2");
        ClosingText.Text = statement.ClosingBalance.ToString("N2") + " ج.م";
        EntriesList.Items.Clear();
        foreach (var entry in statement.Entries)
        {
            EntriesList.Items.Add(new StatementLineRow { Source = entry });
        }
    }
}
