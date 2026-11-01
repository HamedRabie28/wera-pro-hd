// App.xaml.cs — WERA PRO HD G2 Prototype (DEV DATA ONLY)
using Microsoft.UI.Xaml;

namespace WeraPrototype;

public partial class App : Application
{
    public static Window? Shell { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // RTL أولاً (§6): النافذة الرئيسية عربية RTL — تبديل اللغة يختبر LTR من Shell.
        Shell = new ShellWindow { FlowDirection = FlowDirection.RightToLeft };
        Shell.Activate();
    }
}
