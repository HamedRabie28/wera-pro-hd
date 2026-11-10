// Views/MaintenancePage.xaml.cs — WERA PRO HD Desktop (G-ADMIN)
// PAGE-51: تشغيل/استعادة النسخ بحوارات تحذير صريحة، وتنظيف بحد احتفاظ،
// والحذف الكامل خلف بوابتَي تأكيد (مربعان + عبارة «حذف نهائي» + سبب) —
// كل الإجراءات تمر بالخادم بصلاحياتها الذرية وتوثَّق في التدقيق.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class MaintenancePage : Page
{
    private readonly MaintenanceViewModel _vm;

    public MaintenanceViewModel Vm => _vm;

    public MaintenancePage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<MaintenanceViewModel>();
        DataContext = _vm;

        RunBackupButton.Visibility = _vm.CanRunBackup ? Visibility.Visible : Visibility.Collapsed;
        CleanupButton.Visibility = _vm.CanCleanup ? Visibility.Visible : Visibility.Collapsed;
        CleanupButton2.Visibility = _vm.CanCleanup ? Visibility.Visible : Visibility.Collapsed;
        WipeButton.IsEnabled = false;
        BackupFirst.IsEnabled = _vm.CanWipe;
        UnderstandFinal.IsEnabled = _vm.CanWipe;
        WipeConfirmBox.IsEnabled = _vm.CanWipe;
        WipeReasonBox.IsEnabled = _vm.CanWipe;
        if (!_vm.CanWipe)
        {
            WipeGateText.Text = "الحذف الكامل مقيد بصلاحية ops.system.wipe — للمالك فقط (جلستك لا تملكها)";
        }

        BackupFirst.Click += (_, _) => UpdateWipeGate();
        UnderstandFinal.Click += (_, _) => UpdateWipeGate();
        WipeConfirmBox.TextChanged += (_, _) => UpdateWipeGate();

        _ = _vm.LoadBackupsAsync(default);
        _ = _vm.LoadStatsAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaintenanceViewModel.ErrorAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    ErrorText.Text = _vm.ErrorAr ?? "";
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
            if (e.PropertyName == nameof(MaintenanceViewModel.StatsAr) ||
                e.PropertyName == nameof(MaintenanceViewModel.DbNameAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    StatsText.Text = _vm.StatsAr;
                    DbNameText.Text = _vm.DbNameAr;
                });
            }
        };
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = MaintTabs.SelectedIndex;
        BackupsList.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanupView.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DangerView.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshBackups_Click(object sender, RoutedEventArgs e)
    {
        await _vm.LoadBackupsAsync(default);
        await _vm.LoadStatsAsync(default);
    }

    private async void RunBackup_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        var noteBox = new TextBox { Header = "ملاحظة على النسخة (اختياري — تُوثَّق في السجل)" };
        panel.Children.Add(noteBox);
        panel.Children.Add(new TextBlock
        {
            Text = "تُنشأ نسخة كاملة (pg_dump بصيغة custom) مع بصمة SHA-256، ويُتحقق من قابلية قراءتها قبل إعلان النجاح. انسخ الملف بعد ذلك إلى وسيط خارجي — نسخة القرص نفسه ليست خطة استعادة (§19 D-09).",
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraTextMutedBrush"],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "تشغيل نسخة احتياطية كاملة", Content = panel,
            PrimaryButtonText = "تشغيل النسخة", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _vm.RunBackupAsync(string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim(), default);
        await _vm.LoadStatsAsync(default);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BackupRow row) return;
        if (!_vm.CanRestore)
        {
            _vm.ErrorAr = "الاستعادة تتطلب صلاحية ops.backup.restore — إجراء حرج للمالك";
            return;
        }
        if (!row.Verified)
        {
            _vm.ErrorAr = "لا تُستعاد إلا نسخة موثقة قابلة للقراءة (VERIFIED) — شغّل نسخة جديدة إن شككت";
            return;
        }

        // بوابة التأكيد الأولى: تحذير المحتوى المُستبدل
        var warn = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "استعادة نسخة احتياطية — تحذير",
            Content = new StackPanel
            {
                Spacing = 8, MinWidth = 360,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"سيُستعاد محتوى النسخة «{row.FileName}» ({row.WhenAr} · {row.SizeAr}) وتُستبدل كل بيانات قاعدة البيانات الحالية بمحتواها. أي بيانات أُدخلت بعد هذه النسخة ستُفقد.",
                        FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "أغلق نوافذ POS المفتوحة وأبلغ المستخدمين — بعد الاستعادة أعِد تشغيل التطبيق.",
                        FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusWarningBrush"],
                    },
                },
            },
            PrimaryButtonText = "متابعة إلى التأكيد النهائي", CloseButtonText = "إلغاء الاستعادة",
        };
        if (await warn.ShowAsync() != ContentDialogResult.Primary) return;

        // بوابة التأكيد الثانية: عبارة «استعادة» + سبب
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        var confirmBox = new TextBox { Header = "اكتب كلمة «استعادة» حرفياً للتنفيذ" };
        var reasonBox = new TextBox { Header = "سبب الاستعادة (يُوثَّق في التدقيق)" };
        panel.Children.Add(confirmBox);
        panel.Children.Add(reasonBox);
        var final = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "التأكيد النهائي للاستعادة", Content = panel,
            PrimaryButtonText = "استعادة الآن", CloseButtonText = "إلغاء",
        };
        if (await final.ShowAsync() != ContentDialogResult.Primary) return;
        if (!string.Equals(confirmBox.Text.Trim(), "استعادة", StringComparison.Ordinal))
        {
            _vm.ErrorAr = "عبارة التأكيد يجب أن تكون «استعادة» حرفياً — لم يُنفَّذ شيء";
            return;
        }
        await _vm.RestoreAsync(row, confirmBox.Text.Trim(),
            string.IsNullOrWhiteSpace(reasonBox.Text) ? null : reasonBox.Text.Trim(), default);
        await _vm.LoadStatsAsync(default);
    }

    private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BackupRow row) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"حذف سجل النسخة #{row.BackupId}",
            Content = new TextBlock
            {
                Text = "يُحذف سجل النسخة من السجل فقط — ملف النسخة الفعلي على القرص يُدار يدوياً من مجلد النسخ. لا يمكن الاستعادة عبر النظام بعد حذف السجل.",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MinWidth = 320,
            },
            PrimaryButtonText = "حذف السجل", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _vm.DeleteBackupAsync(row, default);
        await _vm.LoadStatsAsync(default);
    }

    private async void Cleanup_Click(object sender, RoutedEventArgs e) => await RunCleanupAsync(RetentionDays);

    private async void Cleanup2_Click(object sender, RoutedEventArgs e) => await RunCleanupAsync(RetentionDays2);

    private async Task RunCleanupAsync(NumberBox source)
    {
        if (!_vm.CanCleanup)
        {
            _vm.ErrorAr = "التنظيف يتطلب صلاحية ops.system.cleanup — للمالك فقط";
            return;
        }
        var days = (int)Math.Clamp(source.Value, 7, 3650);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"تنظيف النظام بحد احتفاظ {days} يوماً",
            Content = new TextBlock
            {
                Text = $"سيُحذف ما هو أقدم من {days} يوماً: أحداث التدقيق، الجلسات المعلقة، الوسائط غير المرتبطة، ومهام الطباعة المنتهية. الأثر يُوثَّق بالأرقام لكل مجموعة ولا يشمل أي بيانات تشغيل حية.",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MinWidth = 340,
            },
            PrimaryButtonText = "تنفيذ التنظيف", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _vm.CleanupAsync(days, default);
    }

    private void UpdateWipeGate()
    {
        var phraseOk = string.Equals(WipeConfirmBox.Text.Trim(), "حذف نهائي", StringComparison.Ordinal);
        WipeButton.IsEnabled = _vm.CanWipe && BackupFirst.IsChecked == true && UnderstandFinal.IsChecked == true && phraseOk;
        WipeGateText.Text = !_vm.CanWipe ? "الحذف الكامل مقيد بصلاحية ops.system.wipe — للمالك فقط (جلستك لا تملكها)"
            : WipeButton.IsEnabled ? ""
            : "اكمل البوابات الثلاث: تأكيد النسخة + إدراك النهائية + كتابة «حذف نهائي» حرفياً";
    }

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        if (!WipeButton.IsEnabled) return;

        // بوابة الحوار الأخيرة فوق البوابات الثلاث في الصفحة
        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "آخر تأكيد: ستمحى كل بيانات التشغيل (منتجات، حركات، فواتير، سندات، تدقيق) نهائياً من " +
                   DbNameText.Text + ". يبقى المستخدمون والأدوار والإعدادات والهيكل التنظيمي.",
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusDangerBrush"],
        });
        var reason = string.IsNullOrWhiteSpace(WipeReasonBox.Text) ? null : WipeReasonBox.Text.Trim();
        panel.Children.Add(new TextBlock
        {
            Text = "السبب الموثَّق: " + (reason ?? "بدون سبب مُدخل"),
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraTextMutedBrush"],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "تنفيذ الحذف الكامل — هل أنت متيقن؟",
            Content = panel, PrimaryButtonText = "نعم — احذف كل بيانات التشغيل", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await _vm.WipeAllAsync("حذف نهائي", reason, default);
        BackupFirst.IsChecked = false;
        UnderstandFinal.IsChecked = false;
        WipeConfirmBox.Text = "";
        WipeReasonBox.Text = "";
        UpdateWipeGate();
    }
}
