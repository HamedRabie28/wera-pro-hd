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
6. **إصلاح خطأ ترجمة في UsersPage.xaml.cs (حوار الأدوار):** سطر يستدعي متغيراً غير معرّف
   (`rows.Add(...)`) من مسودة سابقة — حُذف السطر الميت؛ البناء الآن يمر بلا CS0103.
7. **إصلاح تعريف مكرر في WeraApiClient.cs:** `PutAsync<TReq,TRes>` كان معرفاً مرتين
   (سطر 761 الأصلي + نسخة مكررة أضيفت مع دوال G-ADMIN) — حُذفت النسخة المكررة؛ يزول CS0111.
   تم التحقق: 23 ملف XAML سليم، لا تكرارات تعريف، كل معالجات الأحداث والأسماء والموارد متطابقة.
8. **مجموعة سجلات البيع G7 (PAGE-03..08):** ثلاث واجهات جديدة — SalesLogPage
   (فواتير محطة الجلسة + السلال المعلقة + مطابقة الورديات وحركات الدرج) و
   InvoiceDetailWindow (سطور/دفعات/مهام طباعة + إعادة طباعة موسومة REPRINT) و
   ReturnsPage (مرتجع بحد المتبقي لكل سطر + طلب اعتماد وبته + ترحيل بوسيلة الدفع الأصلية).
9. **توسعة عقود G7 لإصلاح خطأ كامن:** PosPreviewLine أصبح يحمل LineId وRefundedQty
   (كان حوار مرتجع POS يمرر رقم السطر الظاهر LineNo بدل المعرف الحقيقي فيفشل الربط
   بالأصل) — الحد القابل للرد الآن = المباع − المردود سابقاً ويُعرض لكل سطر.
   و HeldCartDto أصبح يحمل CartJson (لقطة السلة) لعرض محتوى المعلقات بلا استعادة —
   التغيير عبر الطبقات الأربع (مستودع → مجال → عقد → تخطيط) بمعاملات اختيارية لا تكسر البناء.

## 1) صفحات تطبيق سطح المكتب (WinUI 3) — 23 صفحة

| # | الصفحة | الوصف | XAML | الكود |
|---|--------|-------|------|------|
| 1 | ShellWindow | الإطار الرئيسي (القائمة العلوية والتنقل) | ✓ | ✓ |
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
| 18 | UsersPage | المستخدمون والأدوار والصلاحيات (PAGE-47): إنشاء/تعديل/تعطيل/كلمة مرور/إسناد أدوار + مصفوفة صلاحيات الأدوار السبعة | ✓ | ✓ |
| 19 | SettingsPage | الإعدادات العامة (PAGE-52): كل قرارات D-xx بحقيقة حسمها + تعديل بصلاحية المالك | ✓ | ✓ |
| 20 | MaintenancePage | النسخ الاحتياطي والصيانة (PAGE-51): نسخ pg_dump موثقة ببصمة SHA-256 + استعادة ببوابتَي تأكيد + تنظيف بحد احتفاظ + منطقة خطر الحذف الكامل | ✓ | ✓ |
| 21 | SalesLogPage | سجل المبيعات (G7): فواتير البيع (PAGE-03) + السلال المعلقة بعرض اللقطة (PAGE-06) + مطابقة الورديات وحركات الدرج (PAGE-08) | ✓ | ✓ |
| 22 | InvoiceDetailWindow | تفاصيل الفاتورة وإعادة الطباعة (PAGE-04): سطور بلقطات السعر + دفعات بحالتها ومزودها + سجل مهام الطباعة + نسخة REPRINT موسومة | ✓ | ✓ |
| 23 | ReturnsPage | المرتجعات والاستردادات (PAGE-05): مرتبط بالأصل بحد المتبقي لكل سطر + طلب اعتماد وبته (V005) + ترحيل بوسيلة الدفع الأصلية (D-04) | ✓ | ✓ |

**حوارات مساندة:** `AdjustmentDialog.cs` ✓، `MediaLinkDialog.cs` ✓، `PosDialogs.cs` ✓، `ShiftCloseDialog.cs` ✓، وحوارات G-ADMIN مدمجة برمجياً (إنشاء/تعديل/كلمة مرور/أدوار/نسخ/استعادة/تنظيف/حذف كامل)

## 2) مكونات المشروع

- **src/WeraPro.Api** — خادم ASP.NET Core 10 (141 نقطة نهاية، 15 وحدة — أضيفت 17 نقطة في وحدات Users/Settings/Maintenance)
- **src/WeraPro.Desktop** — تطبيق سطح المكتب WinUI 3 (23 صفحة + 4 حوارات مستقلة + حوارات G-ADMIN المدمجة)
- **src/WeraPro.Application / Domain / Infrastructure / BackgroundServices** — الطبقات الخدمية
- **src/WeraPro.Hardware.Abstractions / Contracts** — عقود الأجهزة والواجهات المشتركة
- **database/** — PostgreSQL 16: 20 ترحيلة (V001→V020 — أضيفت V020 لسلسلة إسناد النسخ وصلاحيات الإدارة)، 85 جدولاً في 9 مخططات
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
