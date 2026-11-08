# WERA PRO HD — ملف الإنتاج النهائي (Production Application)

**التاريخ:** 2026-11-08  
**الإصدار:** 0.3.0

هذا الدليل يوضح كيفية بناء **ملفات الإنتاج النهائية** القابلة للنشر على أجهزة العملاء،
وما الذي يوجد في كل منها، وكيف تشغّلها.

---

## 1) بنية مجلد الإنتاج `production/`

عند تنفيذ `installer\build-production.ps1` على جهاز Windows، يُنشأ مجلد `production/` يحتوي:

```
production/
├── api-server/
│   ├── linux-x64/          ← خادم API منشور (Self-Contained لـ Linux 64-bit)
│   │   ├── WeraPro.Api.dll
│   │   ├── WeraPro.Api     ← ملف تنفيذي مباشر (apphost) على Linux
│   │   ├── appsettings.json
│   │   └── (كل مكتبات .NET + Npgsql + EF + Serilog ...)
│   └── win-x64/            ← خادم API منشور (Self-Contained لـ Windows 64-bit)
│       ├── WeraPro.Api.exe
│       ├── WeraPro.Api.dll
│       └── appsettings.json
├── desktop-win-x86/        ← تطبيق سطح المكتب WinUI 3 (32-bit)
│   ├── WeraPro.Desktop.exe  ← الملف التنفيذي المباشر للعميل
│   ├── appsettings.json    ← يحتوي BaseUrl = http://localhost:5080/
│   └── (كل مكتبات WinUI 3 + .NET runtime)
├── desktop-win-x64/        ← تطبيق سطح المكتب WinUI 3 (64-bit)
│   └── (نفس محتويات x86 لكن أعرض ثنائيًا)
└── installers/             ← حزم Inno Setup النهائية للتوزيع
    ├── WeraProSetup-0.3.0-win-x86.exe  ← مثبّت 32-bit (يعمل على x86 و x64)
    └── WeraProSetup-0.3.0-win-x64.exe  ← مثبّت 64-bit (يعمل على x64 فقط)
```

> ملاحظة: مجلد `production/api-server/` في هذه الحزمة يحتوي بناء API جاهزًا (139 MB)
> لأنه قابل للبناء على Linux. أما `production/desktop-win-*/` فيُبنى على Windows فقط
> (WinUI 3 يتطلب Windows SDK) — استخدم `build-production.ps1` لإنتاجه.

---

## 2) ما الذي يُبنى على أي نظام؟

| المكون | Linux | Windows |
|--------|------|---------|
| `production/api-server/linux-x64/` | ✅ نعم | ✅ نعم (cross-compile) |
| `production/api-server/win-x64/`   | ✅ نعم (cross-compile) | ✅ نعم |
| `production/desktop-win-x86/`     | ❌ يتطلب Windows SDK | ✅ نعم |
| `production/desktop-win-x64/`     | ❌ يتطلب Windows SDK | ✅ نعم |
| `production/installers/*.exe`      | ❌ يتطلب Inno Setup + Win | ✅ نعم |

---

## 3) بناء كل شيء (Windows فقط)

```powershell
# البناء الكامل: API + Desktop + Installers
powershell -ExecutionPolicy Bypass -File installer\build-production.ps1

# أو بناء خادم API فقط (يعمل على Linux أيضًا)
powershell -ExecutionPolicy Bypass -File installer\build-production.ps1 -ApiOnly

# بناء حزم التثبيت فقط (32-bit + 64-bit)
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -BuildAll
```

---

## 4) تشغيل خادم API في الإنتاج

```bash
# Linux (من داخل production/api-server/linux-x64/)
export TOKENS__SIGNINGKEYSECRET="مفتاح-إنتاج-32-محرف-أو-أكثر"
export ASPNETCORE_URLS="http://0.0.0.0:5080"
export ASPNETCORE_ENVIRONMENT="Production"
./WeraPro.Api
```

```cmd
:: Windows (من داخل production\api-server\win-x64\)
set TOKENS__SIGNINGKEYSECRET=مفتاح-إنتاج-32-محرف-أو-أكثر
set ASPNETCORE_URLS=http://0.0.0.0:5080
set ASPNETCORE_ENVIRONMENT=Production
WeraPro.Api.exe
```

> ⚠️ ملاحظة مهمة: متغير البيئة يجب أن يكون `TOKENS__SIGNINGKEYSECRET` (بدون بادئة `WERA__`).
> هذا التصحيح طُبّق في `Program.cs` بتاريخ 2026-11-08.

---

## 5) إصلاحات الإطلاع الحرجة المطبّقة في هذا الإصدار

1. **`src/WeraPro.Desktop/Program.cs`** (جديد): نقطة دخول صريحة تستدعي
   `Bootstrap.Initialize()` قبل أي نوع WinUI — يحل مشكلة «التطبيق يعمل في الخلفية
   دون نافذة» و«الشاشة البيضاء» في وضع Unpackaged. تشمل نافذة خطأ Win32 (RTL)
   عند فشل التهيئة.

2. **`src/WeraPro.Desktop/appsettings.json`**: صُحح `Api:BaseUrl` من المنفذ
   الخاطئ `5400` إلى المنفذ الإنتاجي الصحيح `5080` (المطابق لخادم API الفعلي).

3. **`installer/WeraProSetup.iss`**: إضافة دعم معمارية **x86 (32-bit)** كامل —
   مسارات VcRedist منفصلة (x86 vs x64)، فحص سجل WOW6432Node، ArchitecturesAllowed
   يشمل `x86 x86compatible` (حزمة 32-bit تعمل على x86 و x64 معًا).

4. **`installer/build-installer.ps1`**: تحديث `ValidateSet` ليشمل `x86`،
   وإضافة معامل `-BuildAll` لبناء x86 + x64 دفعة واحدة.

5. **`installer/build-production.ps1`** (جديد): سكربت بناء إنتاجي شامل —
   يبني API (Self-Contained لـ Linux + Windows) و Desktop (x86 + x64)
   و حزم Inno Setup في خطوة واحدة.

---

## 6) التحقق من سلامة الإطلاع على جهاز العميل

بعد التثبيت عبر Inno Setup، إذا لم تظهر نافذة التطبيق:

1. ابحث عن العملية في إدارة المهام (`WeraPro.Desktop.exe`). إن كانت قائمة دون نافذة
   ← راجع ملف السجل: `%LOCALAPPDATA%\WERA_PRO_HD\logs\desktop-*.log` و`fatal-.log`.
2. تحقق من أن خادم API يعمل على `http://localhost:5080` — افتح المتصفح على
   `http://localhost:5080/health/live` (يجب أن يرجع `{"status":"Healthy"}`).
3. إن فشل Bootstrap فستظهر نافذة خطأ Win32 تلقائيًا (إصلاح #1) بدل البقاء معلّقًا.
4. أعد التثبيت مع صلاحيات المسؤول: `WeraProSetup-0.3.0-win-x64.exe /VERYSILENT /ALLUSERS`.
