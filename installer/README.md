# WERA PRO HD — حزمة التثبيت الاحترافية (Windows Installer)

> **أمر المالك (2026-11-07):** حزمة تثبيت احترافية تُثبِّت التطبيق بلا أخطاء ولا مشاكل، ولا يعمل في الخلفية دون ظهور، مع أيقونة وشكل خارجي متقدم — متوافقة مع كل إصدارات ويندوز المدعومة.

## الملفات

| الملف | الغرض |
|---|---|
| `WeraProSetup.iss` | سكربت Inno Setup 6.3+ — معالج تثبيت عربي RTL كامل بهوية بصرية احترافية |
| `build-installer.ps1` | سكربت البناء الكامل: نشر Self-Contained ثم ترجمة الحزمة عبر ISCC |
| `Assets/setup.ico` | أيقونة المثبِّت (نفس هوية التطبيق · 7 مقاسات) |
| `Assets/wizard_large.bmp` | بانر المعالج الجانبي 164×314 (تصميم داكن متطور) |
| `Assets/wizard_small.bmp` | شعار المعالج العلوي 55×58 |
| `publish/` | ناتج `dotnet publish` (يولّده سكربت البناء — محجوز في .gitignore) |
| `redist/` | `vc_redist.x64.exe` اختياري (ينزّله السكربت تلقائيًا) |
| `output/` | الحزمة النهائية `WeraProSetup-<إصدار>-win-x64.exe` |

## البناء بخطوة واحدة (على ويندوز)

```powershell
# المتطلبات: .NET SDK 10 + Inno Setup 6.3+
winget install Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup

powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

الناتج: `installer\output\WeraProSetup-0.3.0-win-x64.exe`

أو **بدون أي ويندوز محلي** — عبر GitHub Actions (ملف `.github/workflows/build-installer.yml`):
ادفع وسمًا مثل `v0.3.0` وستُبنى الحزمة تلقائيًا وتُرفق كـ Artifact + GitHub Release.

## خيارات سكربت البناء

| الخيار | الوصف |
|---|---|
| `-Platform arm64` | حزمة لأجهزة ARM64 الأصلية (الافتراضي `x64`) |
| `-AppVersion 0.4.0` | تجاوز رقم الإصدار |
| `-SkipPublish` | استخدام ناتج publish سابق |
| `-SkipVcRedist` | بناء الحزمة دون تجميع VC++ Redist |

## لماذا هذه الحزمة لا تفشل؟ (قرارات الهندسة)

1. **Unpackaged Self-Contained**: `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + نشر `--self-contained` — لا يحتاج جهاز العميل إلى .NET ولا Windows App Runtime ولا شيء إطلاقًا.
2. **قضية «يعمل في الخلفية دون نافذة» عولجت من جذورها**:
   - كانت تُبنى نسخة MSIX-packaged ثم تُشغَّل مباشرة → فشل صامت؛ الآن Unpackaged رسميًا.
   - `appsettings.json` يُقرأ من `AppContext.BaseDirectory` لا من دليل العمل (اختصار ابدأ كان يجعل CWD = System32).
   - حارس نافذة واحدة: المحاولة الثانية تُفعِّل نافذة النسخة القائمة بدل تكرار عمليات خلفية.
   - معالجات استثناءات شاملة + نافذة خطأ مرئية دائمًا + سجل فشل في `%LOCALAPPDATA%\WERA_PRO_HD\logs`.
3. **الاختصارات تُنشأ بـ `WorkingDir={app}`** — حزام أمان إضافي.
4. **VC++ Redist مكون اختياري يُثبَّت صامتًا عند الحاجة فقط** (بعض نسخ ويندوز النظيفة تفتقر لأحدث تشغيليات C++ التي يحتاجها Windows App SDK).
5. **الترقية والاستبدال**: نفس `AppId` → تثبيت فوق القديم يغلقه بأمان (`CloseApplications`) ويعيد فتحه بعد الترقية.

## التوافق

| النظام | الحالة |
|---|---|
| Windows 11 (كل الإصدارات) | ✅ مدعوم |
| Windows 10 (1809 / بناء 17763 فأحدث) | ✅ مدعوم |
| Windows 10 قبل 1809 · Windows 8/8.1/7 | ❌ مستحيل معماريًا — WinUI 3 من مايكروسوفت لا يعمل عليها (يظهر رسالة عربية واضحة) |
| المعالجات | x64 (شامل أجهزة ARM64 عبر محاكاة x64) + حزمة ARM64 أصلية اختيارية |

## التثبيت الصامت (نشر مؤسسي)

```bat
WeraProSetup-0.3.0-win-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS
```

- `/CURRENTUSER` بدل `/ALLUSERS`: تثبيت للمستخدم الحالي دون صلاحيات مدير.
- `/DIR="D:\WeraPro"` لاختيار مجلد، `/LOG` لسجل تثبيت كامل في `%TEMP%`.
- رمز الخروج 0 = نجاح.

## بعد التثبيت

- الاختصارات: سطح المكتب + قائمة ابدأ «WERA PRO HD» + إلغاء تثبيت قياسي من إعدادات ويندوز.
- الأيقونة: هوية WERA PRO HD (W أبيض على تدرج كحلي↔تركوازي بخط ذهبي) — نفسها في شريط المهام والنافذة والمثبِّت.
- يتصل التطبيق بخادم WeraPro API (العنوان الافتراضي `http://localhost:5400/` من `appsettings.json` — قابل للتعديل من مجلد التثبيت).
