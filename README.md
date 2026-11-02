# WERA PRO HD — حزمة وثائق المرحلة G0 (Discovery, Product Decisions & Scope Lock)

> **المنتج:** WERA PRO HD — Retail Operations Workbench
> **المرجع التنفيذي:** مواصفة البناء الرئيسية GFM-CSharp 2.1 (Greenfield Master Build Specification) + ملحق وسائط الطباعة 58/80/A4 (Rev.2)
> **التقنية المعتمدة:** C# / .NET 10 LTS / WinUI 3 (XAML + MVVM) / ASP.NET Core 10 / PostgreSQL
> **التاريخ:** 2 نوفمبر 2026 · **حالة البوابات:** G0 = `APPROVED_BY_OWNER` · **G1 = `APPROVED` + حسم D-01/D-07/D-16 (V011)** · **G2 = `APPROVED_BY_OWNER` (2026-11-02) + حسم D-02/D-03 (V012)** · **G3 = `READY_FOR_OWNER_REVIEW_WITH_B05_CONDITION` (مكتملة البناء والاختبارات؛ صور العميل وبناؤه معلقان على B-05)**

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

## محتويات G1 — نموذج المجال وتصميم قاعدة البيانات (بدأت باعتماد «اعتمد GFM 2.4.1»)

| الملف/المجلد | الغرض | المصدر في المواصفة |
|---|---|---|
| `docs/DOMAIN_MODEL.md` | السياقات التسعة والتجمعات وسجل الثوابت الـ27 INV-xxx وترجمة EF Core | §22 G1 |
| `docs/DATABASE_SCHEMA.md` | خريطة المخططات والاصطلاحات وخطة الفهارس 5M/10M وسياسة التقسيم المؤجل والترحيل/الرجوع والأمان | §3 · §22 G1 |
| `docs/DATA_DICTIONARY.md` | قاموس بيانات مولّد آلياً من قاعدة حية — 79 جدولاً بكل عمود/قيد/فهرس | §22 G1 |
| `docs/FINANCIAL_DEFINITIONS.md` | المؤشرات المالية الـ14 الواجبة بصيغها ومصادرها وتوقيت اعترافها | §15.1 |
| `docs/WEIGHT_AND_UNIT_RULES.md` | الأبعاد والتحويلات والطرق الست لإدخال الوزن وخطوات الحساب والتقريب (D-07) | §4 · §5 · §9.3 |
| `docs/ERD/` | ثمانية مخططات علاقات عربية RTL @2x + HTML مرجعي مضمّن ذاتياً | §22 G1 |
| `database/migrations/V001..V010_*.sql` | الترحيلات الخطية بمشغلات خطوط الحماية + بيانات مرجعية موسومة | §22 G1 · §21.2 |
| `database/scripts/run_migrations.py` | عدّاء الترحيلات ببصمات sha256 وسياسة Forward-Fix | §21.2 |
| `database/tests/run_constraint_tests.py` | 41 اختبار قيود وتدفقات مرجعية | §21.2 |
| `database/tests/CONSTRAINT_TEST_RESULTS_A/B.md` | أدلة التنفيذ الفعلي: إنشاء نظيف + ترقية V009→V010 | بوابة القبول |
| `database/migrations/V011__g1_owner_settlement_d01_d07_d16.sql` | حسم المالك بأمر «اعتمد G1 ويُستحسن معه حسم D-01/D-07/D-16»: إعدادات → OWNER_CONFIRMED وقواعد الطباعة مثبتة | سجل الحسم 2026-11-01 |
| `database/tests/OWNER_SETTLEMENT_EVIDENCE.md` | دليل تطبيق V011 على مساري الاختبار وإعادة الـ41 اختباراً (40 PASS · 0 FAIL) | بوابة القبول |
| `docs/PHASE_REPORT_G1.md` | تقرير المرحلة وقرار البوابة (**APPROVED**) وطلب الاعتماد | §25 |
| `WERA_PRO_HD_G1_Report.pdf` (في جذر download/) | تقرير المراجعة الرئيسي لصاحب المنتج | §25 |

## محتويات G2 — نظام التصميم والنموذج التفاعلي (بدأت باعتماد «اعتمد G1» وحسم القرارات الثلاثة)

| الملف/المجلد | الغرض | المصدر في المواصفة |
|---|---|---|
| `docs/DESIGN_SYSTEM.md` | الاتجاه البصري الواحد «Premium Operations, Calm Surfaces» ولوحتا الوضعين وقواعد الخطوط والشبكة | §6 · §7 · §22 G2 |
| `docs/design/DESIGN_TOKENS.json` + `DesignTokens.xaml` | المصدر القانوني للقيم (16 قسماً) + ResourceDictionary مطابق حرفياً لـWinUI 3 | §7.2 |
| `docs/COMPONENT_CATALOG.md` | جرد المكونات بحالاتها الإحدى عشرة وقواعد RTL والوصول ومعيار الجدول §7.2.1 | §7 · §22 G2 |
| `docs/PAGE_DESIGN_CATALOG.md` | مواصفات الصفحات الـ70 بعائلات التخطيط التسع — تخطيط مخصص لكل نوع | §7.4 · §33 |
| `docs/POS_VISUAL_SPEC.md` | مواصفة POS المستقلة: المناطق والمقاسات والتدفق وF-keys والحالات | §9 · §5 |
| `docs/design/concepts/` | 9 معاينات CONCEPT موسومة «CONCEPT — NOT IMPLEMENTED» (قاعدة 10) + HTML مرجعي | §22 G2 |
| `docs/design/templates/` | 24 قالباً (12 نوعاً × 58/80 مستقلة) + أزواج مقارنة + معاينتا A4 تفصيليتان + فهرس | §29 · §33 · §22 G2 |
| `docs/design/DESIGN_QA_RESULTS.md` | فحص RTL/LTR والألوان والمحاذاة والجداول والفلاتر والحالات والمقاسات — 12/12 PASS | §22 G2 |
| `prototype/` | نموذج WinUI 3: csproj + Shell بشريط علوي فقط + 6 شاشات مطلوبة ببيانات DEV | §22 G2 · B-05 |
| `docs/PHASE_REPORT_G2.md` | تقرير المرحلة وقرار البوابة وطلب الاعتماد | §25 |
| `WERA_PRO_HD_G2_Report.pdf` (في جذر download/) | تقرير المراجعة الرئيسي — القسم 1: سجل اعتماد G1 وحسم القرارات | §25 |

## أمر التنفيذ الحالي

## محتويات G3 — Solution Foundation, Backend & Desktop Shell (بدأت بأمر المالك «اعتمد G3» + «اكمل» 2026-11-02)

| الملف/المجلد | الغرض | المصدر في المواصفة |
|---|---|---|
| `WeraPro.sln` + ملفات البناء المركزية | الحل: 7 مشاريع مصدر + 3 اختبارات بإدارة إصدارات مركزية وTreatWarningsAsErrors | §22 G3 · §2 |
| `src/WeraPro.Domain/` | قواعد صرفة: Result/MoneyRounding (Banker's) + أمان بنطاق فرع + WeightCalculator (مثال D-07 الملزم 350g) | §1 · D-07 |
| `src/WeraPro.Contracts/` | عقود v1 المشتركة بين الخادم والعميل — عقد واحد لا نسخ | §11.1 |
| `src/WeraPro.Application/` | حالات الاستخدام والعقود المجردة والتحقق الموحد (ADR-007: FluentValidation) | §1 · §22 G3 |
| `src/WeraPro.Infrastructure/` | EF Core/Npgsql (أسماء مطابقة حرفياً + inet/jsonb) + SqlMigrationRunner الموازي لـrun_migrations.py (ADR-009) + bcrypt/JWT + فحص جاهزية | §22 G3 · §21.2 |
| `src/WeraPro.Api/` | ASP.NET Core 10: JWT بتجديد hash-only مُدار، صلاحيات ذرية بنطاق فرع، ترابط، أخطاء 7807، Rate Limiting، Health، OpenAPI | §11 · §18 |
| `src/WeraPro.Desktop/` | WinUI 3 كامل بتصميم G2 المعتمد: دخول/Shell/POS مستقلة عبر IHttpClientFactory — بلا أي اتصال بالقاعدة (مفروض آلياً) | §22 G3 · §9 |
| `src/WeraPro.Hardware.Abstractions/` | عقود الأجهزة الستة typed بحالة UNKNOWN صادقة (ADR-005) | §2 |
| `tests/` (3 مشاريع · 56 اختباراً) | وحدات + ترحيلات على PG حقيقي + تكامل/عقد/RBAC/ترابط/429/معمارية | §21.1 |
| `database/migrations/V012..V014` | حسم D-02/D-03 (V012) + رموز التجديد (V013) + صلاحيتا قراءة الأساس (V014) | §11.2 · §18 |
| `.github/workflows/ci.yml` | CI مصفوفي: ubuntu (بناء+اختبارات+تنسيق+اعتماديات) وwindows (بناء Desktop) | §21 |
| `docs/PHASE_REPORT_G3.md` + `docs/G3_TWO_CLIENTS_EVIDENCE.md` + `database/tests/CONSTRAINT_TEST_RESULTS_G3_V014.md` | تقرير المرحلة ودليل «عميلان على خادم الاختبار» وأدلة القيود على رأس V014 | §25 · بوابة G3 |

**أمر التنفيذ الحالي (قديم):**
**اعتماد G1 صدر مع حسم القرارات الثلاثة** («اعتمد G1 ويُستحسن معه حسم D-01 (الضريبة) وD-07 (دقة الوزن) وD-16 (طباعة المشتريات» — 2026-11-01): أُغلقت G1 بقرار `APPROVED`، وحُسمت القرارات بالخيار (أ) لكل منها وسُجلت في ترحيل V011 المطبّق فعلياً على مساري الاختبار (40/41 اختباراً ناجحاً بعده). **G2 مكتملة ومعروضة للاعتماد** (القرار: READY_FOR_OWNER_REVIEW — اتجاه بصري واحد وTokens مركزية و24 قالباً مستقلاً ونموذج WinUI جاهز للبناء و12/12 فحص تصميم؛ بناء النموذج معلّق على بيئة Windows — المانع B-05 بصدق). **التطور اللاحق (2026-11-02):** اعتُمدت G2 بأمر المالك مع حسم D-02 (أ: WAC) وD-03 (ب: موافقة مشرف) — ترحيل V012 — ثم صدر «اعتمد G3» + «اكمل» فبُنيت G3 كاملة بأدلة قابلة للإعادة (بناء Release نظيف · 56/56 اختباراً · 40/41 قيد قاعدة · عميلان حقيقيان) وبقيت صور العميل وبناؤه معلقة على B-05 (بيئة Windows). القاعدة مستمرة: لا تُعتبر بوابة معتمدة إلا بقرار صريح من صاحب المنتج بعد اكتمالها بدقة — ولا تبدأ G4 (الكتالوج) إلا باعتماد G3.
