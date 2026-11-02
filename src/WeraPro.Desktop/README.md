# WeraPro.Desktop — WinUI 3 (G3)

تطبيق سطح المكتب: الإقلاع وتسجيل الدخول والتنقل العلوي وفتح POS المستقلة — متصل بالـAPI عبر `IHttpClientFactory` **بلا أي وصول مباشر لقاعدة البيانات** (ADR-002، مفروض بفحص معماري آلي في `tests/WeraPro.Api.Tests`).

## البناء (Windows فقط — المانع B-05)

```powershell
# من جذر الحل WERA_PRO_HD
dotnet restore
dotnet build src/WeraPro.Desktop -c Release

# تشغيل النموذج (unpackaged) مع تشغيل الخادم أولاً
dotnet run --project src/WeraPro.Api -c Release   # نافذة 1
dotnet run --project src/WeraPro.Desktop -c Release
```

- العنوان الأساسي للـAPI في `appsettings.json` (`Api:BaseUrl`).
- المستخدم يُنشأ في قاعدة الاختبار بأداة التهيئة (bcrypt) — لا مستخدم افتراضي مطبوع في الكود.
- السجلات: `%LOCALAPPDATA%\WERA_PRO_HD\logs\desktop-*.log`.

## ما يعمل في G3 (بصدق)

| يعمل فعلياً | معطل بسبب معلن |
|---|---|
| الإقلاع + DI + Serilog | كل أدوات الكاشير (G7) |
| تسجيل دخول JWT حقيقي (فرع/محطة/جلسة موثقة) | الكتالوج والمحرر (G4) |
| Shell بشريط علوي + هوية حقيقية + حالة اتصال حقيقية | المخزون/المشتريات (G6) |
| فتح POS المستقلة (قشرة + إعدادات D-07 من الخادم) | الدفع (G7/B-02) والطباعة (G10/B-07) |
| تجديد الجلسة الشفاف + الخروج الموثق | التغليف MSIX (G14/D-13) |

القاعدة: أي زر لا يعمل يظهر معطلاً مع سبب — لا وظيفة وهمية (§0.1 ق10).
