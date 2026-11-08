# WERA PRO HD — بيان المشروع الكامل (Project Manifest)

**التاريخ:** 2026-11-08  
**هذه الحزمة تحتوي المشروع بالكامل:** كل المصدر + كل الوسائط والصور

---

## 0) سجل إصلاحات هذا الإصدار

1. **إصلاح Program.cs (سطر 54):** رسالة خطأ مفتاح التوقيع كانت تشير إلى متغير بيئة خاطئ
   `WERA__TOKENS__SIGNINGKEYSECRET` — صُححت إلى `TOKENS__SIGNINGKEYSECRET`
   (متغيرات البيئة تُقرأ بدون بادئة WERA__). أعيد البناء: 0 أخطاء / 0 تحذيرات.
2. **نقطة دخول Desktop صريحة (Program.cs جديد):** `Bootstrap.Initialize()` قبل أي نوع WinUI —
   يحل مشكلة «التطبيق يعمل في الخلفية دون نافذة» و«الشاشة البيضاء» في وضع Unpackaged.
3. **تصحيح منفذ API:** `appsettings.json` كان يشير إلى 5400 — صُحح إلى 5080 (المنفذ الفعلي).
4. **دعم 32-bit و 64-bit:** مثبّت Inno Setup يدعم x86 + x64 + ARM64 مع تشغيليات VC++ منفصلة.
5. **سكربتات الإنتاج:** `build-production.ps1` (بناء شامل) و `build-installer.ps1 -BuildAll`.

## 1) صفحات تطبيق سطح المكتب (WinUI 3) — 17 صفحة

| # | الصفحة | الوصف | XAML | الكود |
|---|--------|-------|------|------|
| 1 | ShellWindow | الإطار الرئيسي (القائمة الجانبية والتنقل) | ✓ | ✓ |
| 2 | LoginWindow | نافذة تسجيل الدخول | ✓ | ✓ |
| 3 | PosWindow | شاشة نقطة البيع POS | ✓ | ✓ |
| 4 | CatalogPage | صفحة الكتالوج والأصناف | ✓ | ✓ |
| 5 | FinancePage | صفحة المالية والخزينة | ✓ | ✓ |
| 6 | InventoryHubPage | صفحة مركز المخزون | ✓ | ✓ |
| 7 | PurchasingHubPage | صفحة مركز المشتريات | ✓ | ✓ |
| 8 | PartiesPage | صفحة الأطراف (عملاء/موردين) | ✓ | ✓ |
| 9 | PartyStatementWindow | نافذة كشف حساب طرف | ✓ | ✓ |
| 10 | ProductEditorWindow | نافذة محرر المنتج | ✓ | ✓ |
| 11 | WarehousesPage | صفحة المخازن | ✓ | ✓ |
| 12 | StocktakePage | صفحة الجرد | ✓ | ✓ |
| 13 | TransfersPage | صفحة التحويلات | ✓ | ✓ |
| 14 | MovementCardWindow | نافذة كارت حركة الصنف | ✓ | ✓ |
| 15 | MediaLibraryPage | صفحة مكتبة الوسائط | ✓ | ✓ |
| 16 | MediaImportWindow | نافذة استيراد الوسائط | ✓ | ✓ |
| 17 | MediaPreviewWindow | نافذة معاينة الوسائط | ✓ | ✓ |

**حوارات مساندة:** `AdjustmentDialog.cs` ✓، `MediaLinkDialog.cs` ✓، `PosDialogs.cs` ✓، `ShiftCloseDialog.cs` ✓

## 2) مكونات المشروع

- **src/WeraPro.Api** — خادم ASP.NET Core 10 (124 نقطة نهاية، 12 وحدة)
- **src/WeraPro.Desktop** — تطبيق سطح المكتب WinUI 3 (17 صفحة + 4 حوارات)
- **src/WeraPro.Application / Domain / Infrastructure / BackgroundServices** — الطبقات الخدمية
- **src/WeraPro.Hardware.Abstractions / Contracts** — عقود الأجهزة والواجهات المشتركة
- **database/** — PostgreSQL 16: 19 ترحيلة (V001→V019)، 85 جدولاً في 9 مخططات
- **installer/** — Inno Setup (عربي RTL، Self-Contained، x86+x64+ARM64، Windows 10 1809+/11)
- **tests/** — اختبارات xUnit لكل الطبقات (Domain/Application/Infrastructure/Api/Architecture)
- **docs/** — التوثيق الكامل + صور التصميم + مخططات ERD
- **prototype/ — spec/ — .github/** — النموذج الأولي، المواصفات، CI/CD

## 3) إعادة البناء من المصدر

```bash
dotnet build WeraPro.sln -c Release          # بناء كل الحلول (0 أخطاء)
cd src/WeraPro.Api && dotnet run -c Release  # تشغيل خادم API
export TOKENS__SIGNINGKEYSECRET="<مفتاح 32+ محرفاً>"   # مفتاح التوقيع (إلزامي)
powershell -File installer/build-installer.ps1 -BuildAll  # مثبّت Windows (x86+x64)
```

## 4) حسابات الاختبار

| المستخدم | كلمة المرور | الدور |
|---|---|---|
| g7.owner | Owner#G7-Pass | OWNER |
| g7.cashier | Cashier#G7-Pass | CASHIER |
| g7.supervisor | Supervisor#G7-Pass | SUPERVISOR |
| g7.manager | Manager#G7-Pass | MANAGER |
| g7.storekeeper | Storekeeper#G7-Pass | STOREKEEPER |
| g7.auditor | Auditor#G7-Pass | AUDITOR |

(تسجيل الدخول يتطلب branchId=5 و stationId=2 لبيانات G7)
