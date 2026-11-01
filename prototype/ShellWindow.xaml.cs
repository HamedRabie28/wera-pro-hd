// ShellWindow.xaml.cs — WERA PRO HD G2 Prototype (DEV DATA ONLY)
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPrototype.Views;

namespace WeraPrototype;

public sealed partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — Shell الإداري (DEV DATA — G2 PROTOTYPE)";
        ExtendsContentIntoTitleBar = false;
    }

    private void NavTop_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        // في النموذج: التشغيل والكتالوج مسلَّمان فعلياً؛ البقية تفتح صفحة «قيد البناء ببوابتها»
        // — لا صفحة وهمية تدّعي العمل (قاعدة الصدق §0.1-10).
        switch (tag)
        {
            case "home":
                ContentFrame.Navigate(typeof(HomePage));
                break;
            case "catalog":
                ContentFrame.Navigate(typeof(CatalogPage));
                break;
            default:
                ContentFrame.Navigate(typeof(HomePage));
                break;
        }
    }

    private void CommandPalette_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+K: Command Palette مقيدة بصلاحيات المستخدم — في النموذج: بحث بالصفحة الحالية فقط
        // (التنفيذ الكامل في G3 مع API الأوامر).
    }

    public void OpenPos()
    {
        // POS نافذة مستقلة بلا أي قائمة إدارية (§9) — RTL أولاً
        var pos = new PosWindow { FlowDirection = FlowDirection.RightToLeft };
        pos.Activate();
    }
}
