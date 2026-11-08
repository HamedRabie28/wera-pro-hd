// Program.cs — WERA PRO HD Desktop (تحصين 2026-11-08)
// نقطة الدخول الصريحة لتطبيق WinUI 3 غير المُغلَّف (Unpackaged Self-Contained).
//
// لماذا هذا الملف ضروري؟ (إصلاح أخطاء "العملية في الخلفية بلا نافذة" / "شاشة بيضاء"):
//   في وضع WindowsPackageType=None، يحتاج إطار WinUI 3 / Windows App SDK إلى تهيئة
//   صريحة (Bootstrap.Initialize) قبل أي استخدام لأي نوع من Microsoft.UI.*.
//   بدونها قد يبدأ التطبيق كعملية حية في إدارة المهام دون إنشاء نافذة، أو يعرض
//   نافذة بيضاء فارغة عند فشل تحميل موارد XamlControlsResources.
//   هذا الحل المعتمد من مايكروسوفت (وثائق Windows App SDK: "Initialize the bootstrap
//   API") — نضيفه صراحةً بدلاً من الاعتماد على التهيئة التلقائية الهشّة.
//
// الترتيب:
//   1) Bootstrap.Initialize         ← يحمّل Windows App SDK runtime
//      (الصنف Bootstrap في نطاق DynamicDependency — لا WindowsAppRuntime)
//   2) Global exception handlers    ← قبل أي شيء قد يرمي
//   3) Application.Start(new App)   ← يفتح مضخة رسائل WinUI
//
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace WeraPro.Desktop;

internal static class Program
{
    // Win32 MessageBoxW من user32.dll — بلا أي اعتماد إضافي (لا System.Windows.Forms)
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_RTLREADING = 0x00100000;  // اتجاه RTL للنص العربي
    private const uint MB_SETFOREGROUND = 0x00010000;

    private static readonly string LogDirectory = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "WERA_PRO_HD", "logs");
    private static readonly string FatalLogPath =
        System.IO.Path.Combine(LogDirectory, "fatal-.log");

    [STAThread]
    private static void Main(string[] args)
    {
        // 1) تهيئة Windows App SDK قبل أي نوع WinUI
        try
        {
            // رقم إصدار Windows App SDK 2.6 (major=2, minor=6) — إلزامي في هذا التوقيع
            Bootstrap.Initialize(0x0002_0006);
        }
        catch (Exception ex)
        {
            // اكتب السجل ثم اعرض رسالة Win32 (لا نافذة XAML متاحة قبل Bootstrap)
            try
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                System.IO.File.AppendAllText(FatalLogPath,
                    $"[{DateTimeOffset.Now:O}] [Program.Bootstrap]{Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* قرص محمي — تجاهل */ }

            try
            {
                _ = MessageBoxW(IntPtr.Zero,
                    "تعذّر تهيئة إطار Windows App SDK." + Environment.NewLine + Environment.NewLine +
                    "السبب: " + ex.Message + Environment.NewLine + Environment.NewLine +
                    "تأكد من أن نظامك Windows 10 (إصدار 1809 / بناء 17763) أو أحدث،" + Environment.NewLine +
                    "ثم أعد تثبيت WERA PRO HD." + Environment.NewLine + Environment.NewLine +
                    "تفاصيل الخطأ محفوظة في:" + Environment.NewLine + FatalLogPath,
                    "WERA PRO HD — خطأ في الإقلاع",
                    MB_OK | MB_ICONERROR | MB_RTLREADING | MB_SETFOREGROUND);
            }
            catch { /* حتى MessageBox فشل — لا مجال سوى الإنهاء */ }
            System.Environment.Exit(1);
        }

        // 2) معالج استثناءات مبكر على مستوى AppDomain (قبل XAML)
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                System.IO.File.AppendAllText(FatalLogPath,
                    $"[{DateTimeOffset.Now:O}] [Program.Main/AppDomain]{Environment.NewLine}" +
                    $"{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* لا شيء آخر يمكن عمله */ }
        };

        // 3) بدء تطبيق WinUI — تمرير args كسلسلة (تتجاوزها واجهة WinUI لاحقًا)
        // لا نستخدم معامل اللامدا «_» كهدف إسناد — نوعه
        // ApplicationInitializationCallbackParams وليس App (CS0029).
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var app = new App();   // Application يحتفظ بمرجع ذاتي (App.Current)
        });
    }
}
