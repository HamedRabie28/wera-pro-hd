-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V016 · G5: مكتبة الوسائط وصور المنتجات
-- المرجع: GFM-CSharp 2.4.1 §22 (بوابة G5) و§6.1/§6.2/§6.3 (مكتبة الصور والربط)
-- و§18 (الصلاحيات) و§4.4 حـ (الصور والمرفقات).
--
-- الغرض (بوابة G5 — مكتبة الوسائط وصور المنتجات):
--   1) أعمدة إضافية على catalog.media_assets (من V002): مفاتيح ثلاث نسخ مصغرة
--      مستقلة (thumb-catalog 320 / thumb-pos 128 / preview 1024 — تصغير فقط
--      لا تكبير، والأصل يُحتفظ به ولا يُستبدل §6.1) + ahash (بصمة إدراكية
--      64-bit لاكتشاف «المشتبه بصرياً» §6.2) + processed_at.
--      عمود thumb_key الأصلي (V002) يبقى كما هو بلا استخدام — توثيق فقط.
--   2) جداول مهام استيراد الصور الدفعي: حالة لكل ملف
--      PENDING/PROCESSING/DONE/FAILED/SKIPPED (§6.1: الاستئناف لا يعتمد على
--      عداد processed وحده — حالة كل ملف محفوظة في القاعدة) + حالة مهمة
--      OPEN/RUNNING/COMPLETED/CANCELLED مع إلغاء آمن واستئناف.
--   3) تفرد ربط الصورة بالهدف نفسه: فهرسان فريدان جزئيان على
--      catalog.product_images (media_id, product_id) و(media_id, sku_id) —
--      لا ربط مزدوج لنفس الصورة بنفس الهدف.
--   4) بصمة نطاق التخزين على المهمة (storage_scope): 16 سداسياً صغيراً من
--      SHA-256 لمسار جذر المخزن المُطبَّع — لا يعالج أي عامل إلا مهمات بصمته،
--      فلا يسرق مثيلٌ بجذر مختلف ملفات مهمةٍ بايتاتها المؤقتة خارج متناوله
--      (كانت ستفشل كلها بPROCESSING_FAILURE بلا سبب ظاهر). مثيلات تشترك في
--      الجذر (قرص مشترك) تتشارك البصمة فتتعاون عبر SKIP LOCKED بطبيعتها.
--   5) الصلاحيات (§18):
--      - catalog.media.read  → الأدوار السبعة كلها (كل محطة تعرض مصغرات
--        الكتالوج/POS — قراءة تكوين عمل، نمط V014/V015).
--      - catalog.media.manage → OWNER/MANAGER/STOREKEEPER (رصد PAGE-16:
--        رفع/ربط/فك ربط/رئيسية/إعادة معالجة/حذف أصل).
--      - catalog.import.run (موجود من V009 — استيراد المنتجات/الصور الدفعي):
--        يُمنح لـSTOREKEEPER بالإضافة إلى OWNER (رصد PAGE-17: م، خ).
--      صلاحية catalog.media.upload من V009 تبقى كما زُرعت (MANAGER/OWNER)
--      وتُغطى وظيفياً بـcatalog.media.manage في G5 — لا حذف (سياسة الإضافة
--      الأمامية فقط).
-- إضافة أمامية فقط (Forward Fix) — لا مساس بأي ترحيل مطبق.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) أعمدة media_assets الإضافية ─────────────────────────────────────────
ALTER TABLE catalog.media_assets
    ADD COLUMN IF NOT EXISTS thumb_catalog_key TEXT,
    ADD COLUMN IF NOT EXISTS thumb_pos_key      TEXT,
    ADD COLUMN IF NOT EXISTS preview_key        TEXT,
    ADD COLUMN IF NOT EXISTS ahash              BIGINT,
    ADD COLUMN IF NOT EXISTS processed_at       TIMESTAMPTZ;

COMMENT ON COLUMN catalog.media_assets.thumb_catalog_key IS 'مفتاح نسخة مصغرة للكتالوج (حد أقصى 320px — تصغير فقط لا تكبير) §6.1';
COMMENT ON COLUMN catalog.media_assets.thumb_pos_key      IS 'مفتاح نسخة مصغرة لـPOS (حد أقصى 128px) — لا يُحمَّل الأصل في تغيير السطر §6.3';
COMMENT ON COLUMN catalog.media_assets.preview_key        IS 'مفتاح المعاينة الكبيرة (حد أقصى 1024px) — تُطلب بطلب معاينة منفصل §6.3';
COMMENT ON COLUMN catalog.media_assets.ahash              IS 'بصمة إدراكية 64-bit (متوسط 8×8 رمادياً) — لاكتشاف «المشتبه أنه مكرر بصرياً»؛ تقرير فقط لا دمج/حذف تلقائي §6.2';
COMMENT ON COLUMN catalog.media_assets.processed_at       IS 'آخر معالجة ناجحة (فك/تصغير/ترميز) — تُحدَّث عند إعادة المعالجة';
COMMENT ON COLUMN catalog.media_assets.thumb_key IS 'عمود G1 الأصلي — غير مستخدم في G5 (تُستخدم المفاتيح الثلاثة المستقلة أعلاه)؛ يبقى بلا حذف (سياسة الإضافة الأمامية)';

-- ── 2) تفرد ربط الصورة بالهدف نفسه (لا ربط مزدوج) ──────────────────────────
CREATE UNIQUE INDEX IF NOT EXISTS uq_product_images_media_product
    ON catalog.product_images (media_id, product_id) WHERE product_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_product_images_media_sku
    ON catalog.product_images (media_id, sku_id) WHERE sku_id IS NOT NULL;

-- ── 3) مهام استيراد الصور الدفعي (§6.1: حالة كل ملف في القاعدة) ───────────
CREATE TABLE catalog.media_import_jobs (
    job_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    label         TEXT NOT NULL,
    state         TEXT NOT NULL DEFAULT 'OPEN',
    storage_scope CHAR(16) NOT NULL,
    created_by    BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at    TIMESTAMPTZ,
    finished_at   TIMESTAMPTZ,
    CONSTRAINT mij_state_valid CHECK (state IN ('OPEN', 'RUNNING', 'COMPLETED', 'CANCELLED')),
    CONSTRAINT mij_open_not_started CHECK (state <> 'OPEN' OR started_at IS NULL),
    CONSTRAINT mij_finished_terminal CHECK (state NOT IN ('COMPLETED', 'CANCELLED') OR finished_at IS NOT NULL),
    CONSTRAINT mij_scope_format CHECK (storage_scope ~ '^[0-9a-f]{16}$')
);
COMMENT ON TABLE catalog.media_import_jobs IS 'مهام استيراد الصور الدفعي: حالة المهمة محفوظة في القاعدة — الإلغاء الآمن يتوقف عن الالتقاط الجديد، والاستئناف يقرأ حالات الملفات (لا عداد processed) §6.1؛ storage_scope تمنع عمالاً بجذر تخزين مختلف من لمس بايتاتها';
COMMENT ON COLUMN catalog.media_import_jobs.storage_scope IS 'بصمة نطاق التخزين (16 سداسياً من SHA-256 لمسار الجذر المُطبَّع) — لا يعالج عاملٌ إلا مهمات بصمته؛ الجذور المشتركة تتعاون عبر SKIP LOCKED';

CREATE INDEX idx_media_import_jobs_running ON catalog.media_import_jobs (storage_scope, job_id) WHERE state = 'RUNNING';

CREATE TABLE catalog.media_import_files (
    file_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id       BIGINT NOT NULL REFERENCES catalog.media_import_jobs(job_id) ON DELETE CASCADE,
    ordinal      INTEGER NOT NULL,
    client_name  TEXT NOT NULL,
    staging_key  TEXT,
    size_bytes   BIGINT NOT NULL CHECK (size_bytes > 0),
    state        TEXT NOT NULL DEFAULT 'PENDING',
    sha256       TEXT,
    error_code   TEXT,
    skip_reason  TEXT,
    media_id     BIGINT REFERENCES catalog.media_assets(media_id),
    processed_at TIMESTAMPTZ,
    CONSTRAINT mif_state_valid CHECK (state IN ('PENDING', 'PROCESSING', 'DONE', 'FAILED', 'SKIPPED')),
    -- نتيجة الملف متسقة مع حالته دائماً:
    CONSTRAINT mif_done_shape CHECK (
        (state = 'DONE'    AND media_id IS NOT NULL AND sha256 IS NOT NULL AND error_code IS NULL AND skip_reason IS NULL) OR
        (state = 'FAILED'  AND media_id IS NULL     AND error_code IS NOT NULL AND skip_reason IS NULL) OR
        (state = 'SKIPPED' AND media_id IS NULL     AND sha256 IS NOT NULL AND error_code IS NULL AND skip_reason IS NOT NULL) OR
        (state IN ('PENDING', 'PROCESSING') AND media_id IS NULL AND sha256 IS NULL AND error_code IS NULL AND skip_reason IS NULL)
    ),
    -- staging_key متاح لكل حالة عدا FAILED عند الإضافة (رُفض قبل التخزين المؤقت)؛
    -- FAILED أثناء المعالجة له staging (بايتات مؤقتة تُكنس عند الإقفال):
    CONSTRAINT mif_staging_shape CHECK (state = 'FAILED' OR staging_key IS NOT NULL),
    UNIQUE (job_id, ordinal)
);
COMMENT ON TABLE catalog.media_import_files IS 'ملفات مهمة استيراد: حالة لكل ملف PENDING/PROCESSING/DONE/FAILED/SKIPPED — الاستئناف يقرأها ولا يعتمد على عداد؛ client_name للعرض فقط ولا يُستخدم مساراً أبداً (§6.1 لا مسارات محلية)';

CREATE INDEX idx_media_import_files_job_state ON catalog.media_import_files (job_id, state);

-- ── 4) الصلاحيات ────────────────────────────────────────────────────────────
INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('catalog.media.read',   'catalog', 'قراءة مكتبة الوسائط ومصغرات الكتالوج/POS (كل محطة)'),
    ('catalog.media.manage', 'catalog', 'إدارة مكتبة الوسائط: رفع/ربط/فك ربط/صورة رئيسية/إعادة معالجة/حذف أصل')
ON CONFLICT (code) DO NOTHING;

-- 4-أ) قراءة الوسائط لكل الأدوار السبعة (نمط V014/V015: القراءة تكوين عمل)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'catalog.media.read'
ON CONFLICT DO NOTHING;

-- 4-ب) إدارة المكتبة: المالك والمدير وأمين المخزن (رصد PAGE-16: م، مش، خ)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'catalog.media.manage'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- 4-ج) الاستيراد الدفعي (V009 موجود؛ المالك يملكه من زرع OWNER الكامل):
--      يُمنح لـSTOREKEEPER (رصد PAGE-17: م، خ)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'catalog.import.run'
 WHERE r.code = 'STOREKEEPER'
ON CONFLICT DO NOTHING;

-- ═══ تحقق داخلي: القراءة للسبعة، والإدارة للثلاثة، والاستيراد للمالك/المخازني ═══
DO $$
DECLARE
    missing_read    INTEGER;
    missing_manage  INTEGER;
    missing_import  INTEGER;
BEGIN
    SELECT COUNT(*) INTO missing_read
      FROM core.roles r
     WHERE NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id AND p.code = 'catalog.media.read');

    SELECT COUNT(*) INTO missing_manage
      FROM core.roles r
     WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
       AND NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id AND p.code = 'catalog.media.manage');

    SELECT COUNT(*) INTO missing_import
      FROM core.roles r
     WHERE r.code IN ('OWNER', 'STOREKEEPER')
       AND NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id AND p.code = 'catalog.import.run');

    IF missing_read > 0 THEN
        RAISE EXCEPTION 'V016 check failed: % roles missing catalog.media.read', missing_read;
    END IF;
    IF missing_manage > 0 THEN
        RAISE EXCEPTION 'V016 check failed: % roles missing catalog.media.manage', missing_manage;
    END IF;
    IF missing_import > 0 THEN
        RAISE EXCEPTION 'V016 check failed: % roles missing catalog.import.run', missing_import;
    END IF;
END $$;

COMMIT;
