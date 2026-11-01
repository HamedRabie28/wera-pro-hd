# WERA PRO HD — WinUI 3 Prototype (G2)

> **الحالة الصادقة:** `DELIVERED_XAML_PENDING_WINDOWS_BUILD (B-05)` — كود XAML/C# كامل ومسلَّم، لكنه **لم يُبنَ بعد** لغياب بيئة Windows في بيئة التطوير الحالية (المانع B-05 موثق في BLOCKERS.md).
> وفق قاعدة 10 (§22): لا تُستبدل Screenshot حقيقية بصورة AI أو Render — لذلك معاينات `docs/design/concepts/` موسومة **CONCEPT — NOT IMPLEMENTED** ولا تُحسب دليل قبول؛ الصور الفعلية تُلتقط فور تجهيز بيئة البناء وقبل التعميم في G3+.

## ماذا يحتوي هذا المجلد؟

| الملف | الدور |
|---|---|
| `WeraPrototype.csproj` | مشروع WinUI 3 على .NET 10 + Windows App SDK |
| `App.xaml` / `App.xaml.cs` | نقطة الإقلاع ودمج DesignTokens |
| `Design/DesignTokens.xaml` | نسخة الـResourceDictionary (تطابق docs/design/DESIGN_TOKENS.json) |
| `ShellWindow.xaml` | Shell الإداري **بشريط علوي فقط — لا Sidebar** (§7.3) مع المجموعات العشر |
| `Views/HomePage.xaml` | HOME-01 — مركز التشغيل التنفيذي (LF-A) |
| `Views/CatalogPage.xaml` | PAGE-09 — كتالوج بجدول متقدم (LF-B) |
| `Views/ProductEditorPage.xaml` | PAGE-10 — محرر منتج متعدد الأقسام بمراجعة قبل الحفظ (LF-C) |
| `Views/PosWindow.xaml` | نافذة POS المستقلة بلا قائمة إدارية (LF-P) |
| `Views/WeightWindow.xaml` | نافذة الوزن — الطرق الست بإعدادات D-07 المحسومة |
| `Views/PaymentWindow.xaml` | نافذة الدفع — الوسائل وبطاقة معطلة بسببها الحقيقي (B-02) |
| `ViewModels/DevData.cs` | بيانات عرض موسومة `DEV DATA — G2 PROTOTYPE` فقط |

## البناء (عند توفر بيئة Windows موثقة — B-05)

```powershell
# المتطلبات: Windows 10 1809+ / Windows 11 · VS 2022 · .NET 10 SDK · Windows App SDK 1.7+
cd prototype
dotnet restore
dotnet build -c Release          # يجب أن ينجح بلا أخطاء — أي خطأ يُعالج قبل المتابعة
dotnet run                       # Shell + فتح POS من زر «فتح نقطة البيع»
```

**لقطات الاعتماد المطلوبة بعد البناء (تُلحق بـ PHASE_REPORT_G2 كملحق صور فعلية):**
Shell الإداري · HOME-01 · كتالوج الجدول المتقدم · محرر المنتج · POS · نافذة الوزن · نافذة الدفع — بمقاسي 1280×720 و1920×1080 وفي الوضعين RTL/LTR.

## قرارات مطبقة في الكود (حسم 2026-11-01)
- **D-01 (أ):** كل الأسعار المعروضة شاملة الضريبة 14% مع سطر فصل عند الترحيل (VAT_INCLUSIVE من الإعدادات).
- **D-07 (أ):** نافذة الوزن: دقة 1g · خطوة 5g · مبلغ مستهدف ← وزن لأقرب خطوة (200 ج.م/كجم × 70 ج.م = 350g) · Banker's للقرش.
- **D-16 (أ):** فاتورة المشتريات الداخلية: طباعة رول تلقائية بعد الاعتماد (قوالب 58/80 المستقلة في docs/design/templates/) · نسخة الإدارة إلكترونية · REPRINT دائماً موسوماً.

> **قاعدة:** لا قيم hardcoded للألوان/الأبعاد — كل شيء من `DesignTokens.xaml` (§7.2). أي استثناء يوثق سببه بتعليق في السطر نفسه.
