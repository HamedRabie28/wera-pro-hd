// Views/HomePage.xaml.cs — WERA PRO HD (G9 · HOME-01 الرئيسية التنفيذية)
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

/// <summary>
/// الرئيسية التنفيذية PAGE-01 (HOME-01) — تحمل بياناتها عند كل ظهور
/// وتحدّثها تلقائياً كل 60 ثانية. تطلب فتح POS عبر حدث تشترك فيه القشرة.
/// </summary>
public sealed partial class HomePage : Page
{
    public HomeViewModel Vm { get; }

    /// <summary>تطلبه القشرة لفتح نافذة POS المستقلة (§9.0).</summary>
    public event Action? OpenPosRequested;

    public HomePage()
    {
        InitializeComponent();
        Vm = App.Host.Services.GetRequiredService<HomeViewModel>();
        DataContext = Vm;
        Vm.RequestOpenPos += () => _ = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => OpenPosRequested?.Invoke());
        Loaded += async (_, _) => await Vm.LoadAsync();
        Unloaded += (_, _) => Vm.StopAutoRefresh();
    }
}
