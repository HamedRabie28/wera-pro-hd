# WERA PRO HD — حزمة وثائق المرحلة G0 (Discovery, Product Decisions & Scope Lock)

> **المنتج:** WERA PRO HD — Retail Operations Workbench
> **المرجع التنفيذي:** مواصفة البناء الرئيسية GFM-CSharp 2.1 (Greenfield Master Build Specification) + ملحق وسائط الطباعة 58/80/A4 (Rev.2)
> **التقنية المعتمدة:** C# / .NET 10 LTS / WinUI 3 (XAML + MVVM) / ASP.NET Core 10 / PostgreSQL
> **تاريخ التسليم:** 1 نوفمبر 2026 (Rev.2: دمج ملحق الطباعة) · **حالة البوابة:** READY_FOR_OWNER_REVIEW

---

## محتويات الحزمة — الأساس (Rev.1: G0 الأصلية)

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
| `PHASE_REPORT_G0.md` | تقرير المرحلة G0 وقرار البوابة وطلب الاعتماد (بملحق إحالة Rev.2) | القسم 25 |
| `assets/wera_architecture_diagram.png` | المخطط المعماري المرجعي (PNG عالي الدقة) | القسم 27 |

## محتويات Rev.2 — دمج ملحق وسائط الطباعة (58/80mm + A4 تفصيلي فقط)

| الملف | الغرض | المصدر
|---|---|---|
| `spec/GFM_CSharp_2.4.1_Proposed_Unified_Draft_AR.md` | المسودة الموحدة المقترحة (1,950 سطراً): 2.1 حرفياً + 21 رقعة + الأقسام 29–35 | ملحق الوسائط §0–§14 |
| `spec/SPEC_CHANGES_BEFORE_AFTER_GFM_2.4.1_AR.md` | المقارنة قبل/بعد لكل رقعة وقسم + تدقيق A4 الكامل | ملحق الوسائط §14.8 |
| `docs/printing/` (13 ملفاً + دليلان) | سياسة الوسائط، مصفوفتا الطابعات والقوالب، التوجيه، مواصفتا 58/80، مواصفة A4، مصفوفة الاختبارات الـ25، الفشل وإعادة المحاولة، الخصوصية، تقرير المرحلة، ومصفوفة المتطلبات PMR (38 قاعدة) | ملحق الوسائط §13 |
| تحديثات السجلات (Rev.2 موسومة) | `DECISIONS_REQUIRED` (D-15..D-18) · `PRODUCT_DECISIONS` (PD-26..33) · `BLOCKERS` (B-07) · `ASSUMPTIONS` (A-13..15) · `PAGE_CATALOG` (المجموعة 8: صفحات 67–70 + 4 نوافذ) · `REQUIREMENTS_TRACEABILITY` (105 ← 143 متطلباً) | — |

## قواعد الحالة (موحدة في كل الملفات)

- المتطلبات: `NOT_STARTED · IN_PROGRESS · IMPLEMENTED_NOT_TESTED · IMPLEMENTED_TESTED_PASS · BLOCKED_EXTERNAL_DEPENDENCY · BLOCKED_PRODUCT_DECISION · FAILED · NOT_IN_SCOPE_APPROVED`
- الأجهزة والخدمات: `CONNECTED · DISCONNECTED · UNKNOWN · NOT_CONFIGURED · ERROR`
- البوابات: `PASS · FAIL · BLOCKED · READY_FOR_OWNER_REVIEW`
- **الفرق الجوهري:** `TESTED_PASS` إنجاز تقني، و`APPROVED_BY_OWNER` قبول منتجي — ولا تُعتبر مرحلة معتمدة إلا بقرار صريح من صاحب المنتج.

## أمر التنفيذ الحالي

هذه الحزمة تُغلق بوابة **G0 فقط** (Rev.1 الأساس + Rev.2 دمج الطباعة — لا كود ولا جداول ولا ترحيلات ولا عمليات مالية). تبدأ **G1 (Domain Model & Database Design)** حصريًا بعد اعتماد صريح مثل: «اعتمد GFM 2.4.1» أو «واصل إلى المرحلة التالية» — ومع الاعتماد يُستحسن حسم D-15/D-16 لأنهما يحكمان تصميم قوالب G2.
