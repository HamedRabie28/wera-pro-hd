-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V012 · حسم قرارات المالك D-02 / D-03
-- المرجع الحوكمي: أمر المالك الصادر 2026-11-02 مع اعتماد حزمة مراجعة G2:
--   «اعتمد [حزمة G2] واحسم D-02 (طريقة التكلفة) وD-03 (المخزون السالب)
--    وتوفير بيئة Windows (رفع B-05 للقطات المعلقة)»
-- المصدر التفصيلي للخيارات والآثار: docs/DECISIONS_REQUIRED.md (سجل الحسم)
--
-- القاعدة: القيم كانت توصيات موسومة PENDING_OWNER_CONFIRMATION منذ V009 —
-- هذا الترحيل يرقّيها إلى OWNER_CONFIRMED دون تغيير البنية (Forward Fix،
-- لا يعدّل أي ترحيل مطبق سابقاً ولا يمس بيانات تشغيلية).
-- الخيارات المحسومة = التوصية المسجلة لكل قرار:
--   D-02 → (أ) المتوسط المرجح المتحرك WAC مع لقطة تكلفة وقت الترحيل.
--   D-03 → (ب) السماح بالسالم بموافقة مشرف + سبب مسجل (لا خيار أ ولا ج).
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) D-02 (أ) — المتوسط المرجح المتحرك (WAC) ─────────────────────────────
-- تكلفة الوحدة تتحدث مع كل استلام؛ تعمل على كل صيغ البيع (قطعة/كيس/علبة/وزن)
-- ويُحفظ لها snapshot وقت الترحيل؛ البنية تسمح بإضافة FIFO لاحقاً كمرحلة منفصلة.
UPDATE core.business_settings
   SET confirmation_status = 'OWNER_CONFIRMED',
       description_ar = description_ar || ' · محسوم بأمر المالك 2026-11-02 (الخيار أ)',
       updated_at = now()
 WHERE setting_key = 'inventory.costing_method'
   AND decision_ref = 'D-02'
   AND confirmation_status = 'PENDING_OWNER_CONFIRMATION';

-- ── 2) D-03 (ب) — سماح بالسالب بموافقة مشرف وسبب مسجل ────────────────────────
-- سياسة على مستوى النشاط قابلة للتشديد لكل فرع/صنف؛ كل عملية سالبة تتطلب
-- موافقة مشرف وسبباً مسجلاً في audit_logs (reason إلزامي) — البنية جاهزة منذ V001.
UPDATE core.business_settings
   SET confirmation_status = 'OWNER_CONFIRMED',
       description_ar = description_ar || ' · محسوم بأمر المالك 2026-11-02 (الخيار ب)',
       updated_at = now()
 WHERE setting_key = 'inventory.negative_stock_policy'
   AND decision_ref = 'D-03'
   AND confirmation_status = 'PENDING_OWNER_CONFIRMATION';

-- ── 3) تحقق داخلي: لا يبقى أي إعداد معلق للقرارين ──────────────────────────
DO $$
DECLARE pending_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO pending_count
      FROM core.business_settings
     WHERE decision_ref IN ('D-02', 'D-03')
       AND confirmation_status = 'PENDING_OWNER_CONFIRMATION';
    IF pending_count > 0 THEN
        RAISE EXCEPTION 'V012 check failed: % D-02/D-03 settings still PENDING', pending_count;
    END IF;

    -- تحقق القيم: القيمتين المحسومتين تطابقان الخيارين المعتمدين نصاً
    IF NOT EXISTS (SELECT 1 FROM core.business_settings
                    WHERE setting_key = 'inventory.costing_method'
                      AND value_text = 'WEIGHTED_AVERAGE'
                      AND confirmation_status = 'OWNER_CONFIRMED') THEN
        RAISE EXCEPTION 'V012 check failed: inventory.costing_method must be WEIGHTED_AVERAGE/OWNER_CONFIRMED';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM core.business_settings
                    WHERE setting_key = 'inventory.negative_stock_policy'
                      AND value_text = 'SUPERVISOR_APPROVAL'
                      AND confirmation_status = 'OWNER_CONFIRMED') THEN
        RAISE EXCEPTION 'V012 check failed: inventory.negative_stock_policy must be SUPERVISOR_APPROVAL/OWNER_CONFIRMED';
    END IF;
END $$;

COMMIT;
