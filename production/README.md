# WERA PRO HD — مجلد الإنتاج (production/)

هذا المجلد يحتوي **ملفات الإنتاج النهائية** الجاهزة للنشر على أجهزة العملاء.

## ماذا يوجد هنا؟

| المسار | الوصف | الحجم | كيف يُنتج |
|--------|------|------|-----------|
| `api-server/` | خادم API مُصمّم Self-Contained (يعمل بلا أي متطلبات مسبقة) | ~139 MB | `dotnet publish -c Release --self-contained` (على Linux) |
| `api-server-fd/` | خادم API Framework-Dependent (يحتاج .NET 10 Runtime) | ~23 MB | `dotnet publish -c Release --self-contained false -r linux-x64` |

## ⚠️ ملاحظة مهمة حول التحميل

المجلدات `api-server/` و `api-server-fd/` **غير مُضمّنة** في أرشيف المشروع الرئيسي
`WERA_PRO_HD_Project_Export_2026-11-08.zip` لأن أحجامها تتجاوز حد التحميل في واجهة العميل،
وغير متتبعة في git (مستبعدة عبر `.gitignore` — تُبنى محلياً).

## إنتاج النسخة Self-Contained الكاملة (لا متطلبات مسبقة)

للحصول على نسخة لا تحتاج أي runtime على الخادم (~139 MB لكن تعمل على جهاز نظيف):

```bash
# على Linux:
cd src/WeraPro.Api
dotnet publish -c Release -r linux-x64 --self-contained true -o ../../production/api-server
# الناتج: production/api-server/linux-x64/WeraPro.Api (تنفيذي مباشر)
```

```powershell
# على Windows (يبني نسخ x86 و x64 من Desktop أيضاً):
powershell -ExecutionPolicy Bypass -File installer\build-production.ps1
# الناتج: production/api-server/win-x64/ + production/desktop-win-x86/ + production/desktop-win-x64/
#         + production/installers/WeraProSetup-0.3.0-win-x86.exe + ...-win-x64.exe
```

راجع `PRODUCTION_BUILD.md` في جذر المشروع للتفاصيل الكاملة.

## قائمة محتويات هذا المجلد

```
production/
└── README.md              ← هذا الملف (api-server و api-server-fd تُبنى محلياً)
```
