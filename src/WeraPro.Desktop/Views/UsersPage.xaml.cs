// Views/UsersPage.xaml.cs — WERA PRO HD Desktop (G-ADMIN)
// PAGE-47: حوارات الإنشاء/التعديل/كلمة المرور/الأدوار — كلها ContentDialog
// بنمط المشروع (بناء برمجي — لا XAML حواري منفصل) والعمليات عبر ViewModel.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WeraPro.Contracts.V1;
using WeraPro.Desktop.Services;
using WeraPro.Desktop.ViewModels;

namespace WeraPro.Desktop.Views;

public sealed partial class UsersPage : Page
{
    private readonly UsersViewModel _vm;

    public UsersViewModel Vm => _vm;

    public UsersPage()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<UsersViewModel>();
        DataContext = _vm;
        NewUserButton.Visibility = _vm.CanManage ? Visibility.Visible : Visibility.Collapsed;
        _ = _vm.LoadAsync(default);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsersViewModel.ErrorAr))
            {
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    ErrorText.Text = _vm.ErrorAr ?? "";
                    ErrorText.Visibility = string.IsNullOrEmpty(_vm.ErrorAr) ? Visibility.Collapsed : Visibility.Visible;
                });
            }
        };
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isRoles = UsersTabs.SelectedIndex == 1;
        UsersList.Visibility = isRoles ? Visibility.Collapsed : Visibility.Visible;
        RolesView.Visibility = isRoles ? Visibility.Visible : Visibility.Collapsed;
        if (isRoles && _vm.Roles.Count == 0)
        {
            _ = _vm.LoadRolesAsync(default);
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _vm.Search = SearchBox.Text;
        await _vm.LoadAsync(default);
    }

    private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _vm.Search = SearchBox.Text;
            _ = _vm.LoadAsync(default);
        }
    }

    private async void NewUser_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        var usernameBox = new TextBox { PlaceholderText = "اسم الدخول (إنجليزي فريد 3–40)" };
        var nameBox = new TextBox { PlaceholderText = "الاسم بالعربية" };
        var emailBox = new TextBox { PlaceholderText = "البريد (اختياري)" };
        var passwordBox = new PasswordBox { PlaceholderText = "كلمة المرور (8+ أحرف ورقم)" };
        var roleCombo = new ComboBox { Header = "الدور", PlaceholderText = "اختر دوراً", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var r in _vm.Roles)
        {
            roleCombo.Items.Add(new ComboBoxItem { Content = r.NameAr + " (" + r.Code + ")", Tag = r.Code });
        }
        if (_vm.Roles.Count == 0)
        {
            await _vm.LoadRolesAsync(default);
            foreach (var r in _vm.Roles)
            {
                roleCombo.Items.Add(new ComboBoxItem { Content = r.NameAr + " (" + r.Code + ")", Tag = r.Code });
            }
        }
        var scopeCombo = new ComboBox { Header = "النطاق", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        scopeCombo.Items.Add(new ComboBoxItem { Content = "كل الفروع", Tag = null });
        scopeCombo.Items.Add(new ComboBoxItem { Content = "فرع محدد (رقم الفرع في خانة السبب أدناه)", Tag = "branch" });

        panel.Children.Add(usernameBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(emailBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(roleCombo);
        panel.Children.Add(scopeCombo);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "مستخدم جديد — كلمة المرور تُجزَّأ bcrypt ولا تُخزَّن نصاً (§18)",
            Content = panel, PrimaryButtonText = "إنشاء", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var roleCode = (roleCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(usernameBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text) ||
            string.IsNullOrEmpty(passwordBox.Password) || roleCode is null)
        {
            _vm.ErrorAr = "أكمل اسم الدخول والاسم وكلمة المرور والدور";
            return;
        }
        long? branchId = (scopeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "branch"
            ? 1 : null; // نطاق الفرع المحدد — قوائم الفروع تُغذى من الخادم عند توفرها
        await _vm.CreateUserAsync(usernameBox.Text.Trim(), nameBox.Text.Trim(),
            string.IsNullOrWhiteSpace(emailBox.Text) ? null : emailBox.Text.Trim(),
            passwordBox.Password, roleCode, branchId, default);
    }

    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not UserRow row) return;
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        var nameBox = new TextBox { Header = "الاسم بالعربية", Text = row.DisplayNameAr };
        var emailBox = new TextBox { Header = "البريد", Text = row.Source.Email ?? "" };
        panel.Children.Add(nameBox);
        panel.Children.Add(emailBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"تعديل بيانات «{row.Username}» — اسم الدخول غير قابل للتغيير",
            Content = panel, PrimaryButtonText = "حفظ", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(nameBox.Text))
        {
            _vm.ErrorAr = "الاسم بالعربية مطلوب";
            return;
        }
        await _vm.UpdateUserAsync(row.UserId, nameBox.Text.Trim(),
            string.IsNullOrWhiteSpace(emailBox.Text) ? null : emailBox.Text.Trim(), default);
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not UserRow row) return;
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        var passwordBox = new PasswordBox { Header = "كلمة المرور الجديدة (8+ أحرف ورقم)" };
        panel.Children.Add(passwordBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"إعادة تعيين كلمة مرور «{row.Username}» — تُسجَّل في التدقيق",
            Content = panel, PrimaryButtonText = "تعيين", CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrEmpty(passwordBox.Password))
        {
            _vm.ErrorAr = "كلمة المرور مطلوبة";
            return;
        }
        await _vm.ResetPasswordAsync(row.UserId, passwordBox.Password, default);
    }

    private async void Roles_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not UserRow row) return;
        await ShowRolesDialogAsync(row);
    }

    private async void UsersList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // نقرة مزدوجة = إدارة أدوار المستخدم (سلوك ثابت مع بقية الصفحات)
        if (UsersList.SelectedItem is UserRow row)
        {
            await ShowRolesDialogAsync(row);
        }
    }

    private async Task ShowRolesDialogAsync(UserRow row)
    {
        if (!_vm.CanManage)
        {
            _vm.ErrorAr = "إسناد الأدوار يتطلب صلاحية security.users.manage — للمالك فقط";
            return;
        }
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        var current = new TextBlock
        {
            Text = "الأدوار الحالية (اختر إسناداً ثم «سحب الإسناد المحدد»):",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        panel.Children.Add(current);

        var rolesList = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 160, BorderThickness = new Thickness(1), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraBorderDefaultBrush"] };
        var assignments = await App.Host.Services.GetRequiredService<WeraApiClient>()
            .GetUserRolesAsync(row.UserId, default);
        if (assignments.Ok)
        {
            foreach (var a in assignments.Value!)
            {
                var label = a.RoleNameAr + " (" + a.RoleCode + ")" +
                            (a.BranchId is null ? " — كل الفروع" : " — فرع " + a.BranchId);
                rolesList.Items.Add(new ListViewItem { Content = label, Tag = a });
            }
        }
        if (rolesList.Items.Count == 0)
        {
            rolesList.Items.Add(new ListViewItem { Content = "لا أدوار مسندة", IsEnabled = false });
        }
        panel.Children.Add(rolesList);

        var revokeButton = new Button { Content = "سحب الإسناد المحدد" };
        panel.Children.Add(revokeButton);

        var combo = new ComboBox { Header = "إسناد دور جديد", PlaceholderText = "اختر دوراً", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var r in _vm.Roles)
        {
            combo.Items.Add(new ComboBoxItem { Content = r.NameAr + " (" + r.Code + ")", Tag = r.Code });
        }
        panel.Children.Add(combo);

        var scopeCombo = new ComboBox { Header = "النطاق", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        scopeCombo.Items.Add(new ComboBoxItem { Content = "كل الفروع", Tag = null });
        panel.Children.Add(scopeCombo);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"أدوار «{row.DisplayNameAr}» ({row.Username}) — الأدوار المرجعية السبعة صلبة (V009)",
            Content = panel, PrimaryButtonText = "إسناد الدور المختار", CloseButtonText = "إغلاق",
        };

        var revoked = false;
        revokeButton.Click += async (_, _) =>
        {
            if (rolesList.SelectedItem is ListViewItem { Tag: UserRoleAssignmentDto assignment })
            {
                revokeButton.IsEnabled = false;
                revoked = await _vm.RevokeRoleAsync(row.UserId, assignment.UserRoleId, default);
                if (revoked)
                {
                    dialog.Hide();
                }
                else
                {
                    revokeButton.IsEnabled = true;
                }
            }
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var roleCode = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (roleCode is null)
            {
                _vm.ErrorAr = "اختر دوراً للإسناد";
                return;
            }
            await _vm.AssignRoleAsync(row.UserId, roleCode, null, default);
        }
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not UserRow row) return;
        var disabling = row.IsActive;
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        var reasonBox = new TextBox { Header = disabling ? "سبب التعطيل (يُسجَّل في التدقيق)" : "سبب التفعيل (اختياري)" };
        panel.Children.Add(reasonBox);
        if (disabling)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "التعطيل لا الحذف — سجل دخول المستخدم يبقى محفوظاً. لا يمكن تعطيل آخر مالك نشط ولا حسابك الحالي.",
                FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WeraStatusWarningBrush"],
            });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = disabling ? $"تعطيل «{row.Username}»" : $"تفعيل «{row.Username}»",
            Content = panel,
            PrimaryButtonText = disabling ? "تعطيل" : "تفعيل",
            CloseButtonText = "إلغاء",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _vm.ToggleActiveAsync(row, string.IsNullOrWhiteSpace(reasonBox.Text) ? null : reasonBox.Text.Trim(), default);
    }
}
