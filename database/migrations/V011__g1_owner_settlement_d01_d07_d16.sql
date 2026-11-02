-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V011 · حسم قرارات المالك D-01 / D-07 / D-16
-- المرجع الحوكمي: أمر المالك الصادر 2026-11-01:
--   «اعتمد G1 ويُستحسن معه حسم D-01 (الضريبة) وD-07 (دقة الوزن) وD-16 (طباعة المشتريات)»
-- المصدر التفصيلي للخيارات والآثار: docs/DECISIONS_REQUIRED.md v1.2 (سجل الحسم)
--
-- القاعدة: القيم كانت توصيات موسومة PENDING_OWNER_CONFIRMATION — هذا الترحيل
-- يرقّيها إلى OWNER_CONFIRMED دون تغيير البنية (البنية كانت جاهزة منذ V001/V009).
-- لا يعدّل أي ترحيل مطبق سابقاً (Forward Fix) ولا يمس بيانات تشغيلية.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) D-01 (أ) — مصر + ضريبة على الأسعار (VAT-inclusive) ───────────────────
-- محرك الضريبة قابل للتهيئة (TAX-001 يبقى كذلك)، والفوترة الإلكترونية ETA
-- مرحلة لاحقة منفصلة بعد اعتماد نطاقها (كانت أصلاً APPROVED_WITH_SPEC).
UPDATE core.business_settings
   SET confirmation_status = 'OWNER_CONFIRMED',
       description_ar = description_ar || ' · محسوم بأمر المالك 2026-11-01 (الخيار أ)',
       updated_at = now()
 WHERE setting_key IN ('tax.policy_mode', 'tax.standard_rate')
   AND decision_ref = 'D-01'
   AND confirmation_status = 'PENDING_OWNER_CONFIRMATION';

-- ── 2) D-07 (أ) — دقة 1g · خطوة 5g · Banker's Rounding للقرش · أقرب خطوة ────
-- المثال المرجعي محفوظ: 200 EGP/kg × مبلغ 70 EGP → 350g (على الخطوة بلا تعديل).
UPDATE core.business_settings
   SET confirmation_status = 'OWNER_CONFIRMED',
       description_ar = description_ar || ' · محسوم بأمر المالك 2026-11-01 (الخيار أ)',
       updated_at = now()
 WHERE setting_key IN ('weighing.precision_g', 'weighing.step_g',
                       'weighing.amount_rounding', 'weighing.weight_rounding')
   AND decision_ref = 'D-07'
   AND confirmation_status = 'PENDING_OWNER_CONFIRMATION';

-- ── 3) D-16 (أ) — طباعة المشتريات الداخلية بعد الاعتماد + نسخة الإدارة إلكترونية ──
--    قواعد §29.10 تبقى كما صممت في V009 — الحسم يثبّت التوصية (أ) نصاً:
--    · فاتورة المشتريات الداخلية: طباعة رول تلقائية بعد الاعتماد/الترحيل
--      على طابعة المالية/المشتريات (دور SUPPLIER_DOCUMENTS).
--    · نسخة الإدارة: حفظ إلكتروني فقط؛ الطباعة بإعداد إداري منفصل.
--    · إعادة الطباعة متاحة دائماً بعلامة REPRINT، وA4 التفصيلي بإجراء صريح فقط.
UPDATE print.auto_print_rules
   SET notes = 'طباعة رول تلقائية بعد الاعتماد/الترحيل على طابعة المالية (محسوم D-16 الخيار أ — أمر المالك 2026-11-01؛ إعادة الطباعة دائماً بعلامة REPRINT)'
 WHERE document_type = 'PURCHASE_INTERNAL_INVOICE';

UPDATE print.auto_print_rules
   SET notes = 'حفظ إلكتروني فقط؛ الطباعة بإعداد إداري منفصل (محسوم D-16 الخيار أ — أمر المالك 2026-11-01)'
 WHERE document_type = 'ADMIN_COPY';

-- إعداد D-16 في سجل الإعدادات الموحد كان أصلاً APPROVED_WITH_SPEC (§29.11) —
-- يوثَّق الحسم في وصفه دون تغيير قيمته (EXPLICIT_ONLY تبقى ملزمة نصاً):
UPDATE core.business_settings
   SET description_ar = description_ar || ' · حسم D-16 كاملاً بأمر المالك 2026-11-01 (الخيار أ)',
       updated_at = now()
 WHERE setting_key = 'print.a4_policy'
   AND confirmation_status = 'APPROVED_WITH_SPEC';

-- ── 4) تحقق داخلي: لا يبقى أي إعداد معلق للقرارات الثلاثة ──────────────────
DO $$
DECLARE pending_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO pending_count
      FROM core.business_settings
     WHERE decision_ref IN ('D-01', 'D-07')
       AND confirmation_status = 'PENDING_OWNER_CONFIRMATION';
    IF pending_count > 0 THEN
        RAISE EXCEPTION 'V011 check failed: % D-01/D-07 settings still PENDING', pending_count;
    END IF;
END $$;

COMMIT;
