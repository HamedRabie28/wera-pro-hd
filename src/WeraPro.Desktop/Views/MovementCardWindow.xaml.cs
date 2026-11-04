// Views/MovementCardWindow.xaml.cs — WERA PRO HD Desktop (G6)
// كرت حركة المنتج (PAGE-19): يُحمَّل من الخادم عند الفتح — لا كاش محلي
// (مصدر حقيقة واحد §2.5) والرصيد الجاري محسوب من الدفتر حرفياً.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class MovementCardWindow : Window
{
    private readonly MovementCardViewModel _vm;

    public MovementCardWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — كرت حركة المنتج";
        App.ActiveWindow = this;
        _vm = App.Host.Services.GetRequiredService<MovementCardViewModel>();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MovementCardViewModel.IsLoading))
            {
                _ = DispatcherQueue.TryRunAsync(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    LoadingRing.Visibility = _vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                    ErrorText.Text = _vm.ErrorAr;
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
    }

    public void LoadFor(long skuId, long? warehouseId, string skuCode)
    {
        TitleText.Text = $"كرت حركة {skuCode}" + (warehouseId is null ? " — كل المخازن" : "");
        _ = _vm.LoadAsync(skuId, warehouseId, skuCode, default);
    }
}
