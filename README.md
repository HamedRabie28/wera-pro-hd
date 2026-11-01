# WERA PRO HD — حزمة وثائق المرحلة G0 (Discovery, Product Decisions & Scope Lock)

> **المنتج:** WERA PRO HD — Retail Operations Workbench
> **المرجع التنفيذي:** مواصفة البناء الرئيسية GFM-CSharp 2.1 (Greenfield Master Build Specification)
> **التقنية المعتمدة:** C# / .NET 10 LTS / WinUI 3 (XAML + MVVM) / ASP.NET Core 10 / PostgreSQL
> **تاريخ التسليم:** 1 نوفمبر 2026 · **حالة البوابة:** READY_FOR_OWNER_REVIEW

---

## محتويات الحزمة

| الملف | الغرض | المصدر في البرومت |
|---|---|---|
| `BASELINE_PRODUCT_SCOPE.md` | نطاق المنتج وأهدافه وحدوده وغير الأهداف | القسم 27 + G0 |
| `PRODUCT_DECISIONS.md` | القرارات التجارية والتقنية المعتمدة أصلًا من الوثيقة الأم | القسم 0.2 |
| `DECISIONS_REQUIRED.md` | القرارات التي تحتاج اعتماد صاحب المنتج — بخيارات وتأثير وتوصية | القسم 0.2 + G0 |
| `ASSUMPTIONS_REGISTER.md` | سجل الافتراضات غير الحساسة مع المصدر وتاريخ الانتهاء | القسم 0.2 |
| `BLOCKERS.md` | الموانع الخارجية وتأثير كل مانع | القسم 0.2 |
| `PAGE_CATALOG.md` | فهرس الصفحات الـ66: page_id والدور والإجراء ومصدر البيانات والبوابة | القسم 17 |
| `WORKFLOW_MAP.md` | خرائط التدفقات: بيع، مرتجع، شراء، جرد، وردية، والطرق الست للوزن | القسم 27 |
| `REQUIREMENTS_TRACEABILITY.csv` | ربط كل متطلب بصفحة/خدمة/جدول/اختبار/دليل/بوابة/حالة | القسم 0.2 |
| `ARCHITECTURE_BRIEF.md` | المعمارية المرجعية وقرارات ADR وهيكل الحل وخط الأساس التقني | القسم 2 |
| `TEST_STRATEGY.md` | استراتيجية الاختبارات وبوابات G0–G14 وبروتوكول اللقطات | القسم 21 + 22 |
| `PHASE_REPORT_G0.md` | تقرير المرحلة G0 وقرار البوابة وطلب الاعتماد | القسم 25 |
| `assets/wera_architecture_diagram.png` | المخطط المعماري المرجعي (PNG عالي الدقة) | القسم 27 |

## قواعد الحالة (موحدة في كل الملفات)

- المتطلبات: `NOT_STARTED · IN_PROGRESS · IMPLEMENTED_NOT_TESTED · IMPLEMENTED_TESTED_PASS · BLOCKED_EXTERNAL_DEPENDENCY · BLOCKED_PRODUCT_DECISION · FAILED · NOT_IN_SCOPE_APPROVED`
- الأجهزة والخدمات: `CONNECTED · DISCONNECTED · UNKNOWN · NOT_CONFIGURED · ERROR`
- البوابات: `PASS · FAIL · BLOCKED · READY_FOR_OWNER_REVIEW`
- **الفرق الجوهري:** `TESTED_PASS` إنجاز تقني، و`APPROVED_BY_OWNER` قبول منتجي — ولا تُعتبر مرحلة معتمدة إلا بقرار صريح من صاحب المنتج.

## أمر التنفيذ الحالي

هذه الحزمة تُغلق بوابة **G0 فقط**. لا صفحات إنتاج ولا ترحيلات قاعدة بيانات ولا عمليات مالية نُفذت في هذه المرحلة، التزامًا بالقسم 27. تبدأ **G1 (Domain Model & Database Design)** حصريًا بعد اعتماد صريح مثل: «اعتمد G0» أو «واصل إلى المرحلة التالية».
