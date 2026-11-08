// Views/ProductEditorWindow.xaml.cs — WERA PRO HD Desktop (G4)
// سلوك المحرر متعدد الأقسام: مسودات محلية → مراجعة إلزامية → طلب ذرّي واحد.
// لا حفظ قبل اعتماد المراجعة، ولا كتابة جزئية على الخادم أبداً (§4.4).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class ProductEditorWindow : Window
{
    private readonly ProductEditorViewModel _vm;

    public ProductEditorViewModel Vm => _vm;

    public ProductEditorWindow()
    {
        InitializeComponent();
        Title = "WERA PRO HD — محرر منتج";

        _vm = ActivatorUtilities.CreateInstance<ProductEditorViewModel>(App.Host.Services);
        RootGrid.DataContext = _vm;   // قوالب الصيغ تربط قوائم الوحدات عبر DataContext
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProductEditorViewModel.CanSave)
                or nameof(ProductEditorViewModel.IsSaving)
                or nameof(ProductEditorViewModel.ErrorAr)
                or nameof(ProductEditorViewModel.SuccessAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
            }
        };
        _vm.ProductCreated += OnProductCreated;

        SkusControl.ItemsSource = _vm.Skus;
        PackagesControl.ItemsSource = _vm.Packages;
        ReviewList.ItemsSource = _vm.ReviewLines;

        // ربط قسم الهوية بمسودات النموذج (تحديث ثنائي الاتجاه عبر الأحداث)
        CodeField.TextChanged += (_, _) => _vm.Code = CodeField.Text;
        NameArField.TextChanged += (_, _) => _vm.NameAr = NameArField.Text;
        NameEnField.TextChanged += (_, _) => _vm.NameEn = NameEnField.Text;
        CategoryField.SelectionChanged += (_, _) => _vm.CategoryIndex = CategoryField.SelectedIndex;
        TrackLotsCheck.Checked += (_, _) => _vm.TrackLots = true;
        TrackLotsCheck.Unchecked += (_, _) => _vm.TrackLots = false;
        TrackSerialsCheck.Checked += (_, _) => _vm.TrackSerials = true;
        TrackSerialsCheck.Unchecked += (_, _) => _vm.TrackSerials = false;

        _ = LoadReferenceAsync();
    }

    private async Task LoadReferenceAsync()
    {
        await _vm.LoadReferenceAsync(default);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
    }

    private void RefreshState()
    {
        SavingRing.IsActive = _vm.IsSaving;
        StatusText.Text = _vm.ErrorAr ?? _vm.SuccessAr ?? string.Empty;
        SaveBtn.IsEnabled = _vm.CanSave && ReviewApproveCheck.IsChecked == true;
    }

    private void AddSku_Click(object sender, RoutedEventArgs e) => _vm.AddSkuCommand.Execute(null);

    private void RemoveSku_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int ordinal }
            && _vm.Skus.FirstOrDefault(s => s.Ordinal == ordinal) is { } draft)
        {
            _vm.RemoveSkuCommand.Execute(draft);
        }
    }

    private void AddPackage_Click(object sender, RoutedEventArgs e) => _vm.AddPackageCommand.Execute(null);

    private void RemovePackage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PackageDraft draft })
        {
            _vm.RemovePackageCommand.Execute(draft);
        }
    }

    private void BuildReview_Click(object sender, RoutedEventArgs e)
    {
        _vm.BuildReview();
        ReviewApproveCheck.IsChecked = false;
        RefreshState();
    }

    private void ReviewApprove_Changed(object sender, RoutedEventArgs e)
    {
        _vm.ReviewApproved = ReviewApproveCheck.IsChecked == true;
        RefreshState();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SaveCommand.ExecuteAsync(null);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, RefreshState);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnProductCreated(ProductDto product)
    {
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            // نجاح كامل: نغلق المحرر — جدول الكتالوج يعيد التحميل عند عودته
            StatusText.Text = _vm.SuccessAr;
            Close();
        });
    }
}
