# OWNER SETTLEMENT EVIDENCE — أدلة حسم قرارات المالك (V011)

> **التاريخ:** 2026-11-01 06:19 · **الترحيل:** `V011__g1_owner_settlement_d01_d07_d16.sql`
> **الأمر الحوكمي:** «اعتمد G1 ويُستحسن معه حسم D-01 (الضريبة) وD-07 (دقة الوزن) وD-16 (طباعة المشتريات» — 2026-11-01
> **السياسة:** Forward-Fix — لا تعديل على ترحيل مطبق؛ الحسم ترحيل جديد يرقّي الإعدادات إلى `OWNER_CONFIRMED` بلا تغيير بنية.

## 1) مسار الترقية الحي (TEST B) — الحالة قبل وبعد

```text
قبل الحسم: head=V010 · PENDING=18 · OWNER_CONFIRMED=0 · ملاحظة قاعدة المشتريات: «طباعة رول بعد الاعتماد/الترحيل على طابعة المالية (D-16 توصية…»
بعد الحسم: head=V011 · PENDING=12 · OWNER_CONFIRMED=6 · ملاحظة قاعدة المشتريات: «طباعة رول تلقائية بعد الاعتماد/الترحيل على طابعة المالية (مح…»
```

## 2) الإعدادات المحسومة بعد V011 — قاعدة wera_g1_a

```text
setting_key                 القيمة                  decision  status
──────────────────────────  ──────────────────────  ────────  ──────────────────
print.a4_policy             EXPLICIT_ONLY          D-16     APPROVED_WITH_SPEC
tax.einvoice_integration    DISABLED               D-01     APPROVED_WITH_SPEC
tax.policy_mode             VAT_INCLUSIVE          D-01     OWNER_CONFIRMED
tax.standard_rate           0.1400                 D-01     OWNER_CONFIRMED
weighing.amount_rounding    ROUND_HALF_EVEN_0.01   D-07     OWNER_CONFIRMED
weighing.precision_g        1                      D-07     OWNER_CONFIRMED
weighing.step_g             5                      D-07     OWNER_CONFIRMED
weighing.weight_rounding    NEAREST_STEP           D-07     OWNER_CONFIRMED
```

## 3) قواعد الطباعة المتأثرة بحسم D-16

```text
◆ ADMIN_COPY · policy=ELECTRONIC_ONLY · printer_role=—
  حفظ إلكتروني فقط؛ الطباعة بإعداد إداري منفصل (محسوم D-16 الخيار أ — أمر المالك 2026-11-01)
◆ PURCHASE_INTERNAL_INVOICE · policy=AUTO_AFTER_POST · printer_role=SUPPLIER_DOCUMENTS
  طباعة رول تلقائية بعد الاعتماد/الترحيل على طابعة المالية (محسوم D-16 الخيار أ — أمر المالك 2026-11-01؛ إعادة الطباعة دائماً بعلامة REPRINT)
```

## 4) التحقق: لا يبقى إعداد معلق للقرارات المحسومة

```text
wera_g1_a: معلق D-01/D-07 = 0 (متوقع 0) · المعلق المتبقي = 12 (D-02, D-03, D-04, D-05, D-06, D-08, D-09, D-12, D-13, D-14)
wera_g1_b: معلق D-01/D-07 = 0 (متوقع 0) · المعلق المتبقي = 12 (D-02, D-03, D-04, D-05, D-06, D-08, D-09, D-12, D-13, D-14)
```

## 5) اختبارات القيود بعد الحسم — لا انكسار (كلا المسارين)

**TEST A — إنشاء نظيف:**

```text
T-DDL-33     Ops         PASS              نسخة احتياطية «ناجحة» دون verified_readable لا تُحتسب ناجحة (§19)
T-ENV-01     Env         PASS              اكتمال المخططات التسعة وجداولها ومنظر المطابقة
T-ENV-02     Env         PASS              إعدادات D-01..D-18 موجودة وموسومة بحالة الحسم الصادقة (بعد حسم D-0
T-FLW-06     Env         ENV_LIMITATION    فهارس trigram للبحث النصي — تصريح صادق عن حالة البيئة (§0.1 ق10)
----------------------------------------------------------------------------------------------------------------
PASS=40 · FAIL=0 · ERROR=0 · ENV_LIMITATION=1 · TOTAL=41
env: wera_g1_a — PostgreSQL 16.2 on x86_64-pc-linux-gnu, compiled by gcc (GCC) 10.2.1 202
saved: /home/z/my-project/download/WERA_PRO_HD/database/tests/CONSTRAINT_TEST_RESULTS_A.md
```

**TEST B — ترقية:**

```text
T-DDL-33     Ops         PASS              نسخة احتياطية «ناجحة» دون verified_readable لا تُحتسب ناجحة (§19)
T-ENV-01     Env         PASS              اكتمال المخططات التسعة وجداولها ومنظر المطابقة
T-ENV-02     Env         PASS              إعدادات D-01..D-18 موجودة وموسومة بحالة الحسم الصادقة (بعد حسم D-0
T-FLW-06     Env         ENV_LIMITATION    فهارس trigram للبحث النصي — تصريح صادق عن حالة البيئة (§0.1 ق10)
----------------------------------------------------------------------------------------------------------------
PASS=40 · FAIL=0 · ERROR=0 · ENV_LIMITATION=1 · TOTAL=41
env: wera_g1_b — PostgreSQL 16.2 on x86_64-pc-linux-gnu, compiled by gcc (GCC) 10.2.1 202
saved: /home/z/my-project/download/WERA_PRO_HD/database/tests/CONSTRAINT_TEST_RESULTS_B.md
```
