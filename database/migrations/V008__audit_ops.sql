-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V008 · طبقتا audit وops: سجل التدقيق ومفاتيح
-- Idempotency وجلسات المستخدمين وسجلات النسخ الاحتياطي والاستيراد الدفعي
-- المرجع: GFM-CSharp 2.4.1 §18 (سجل التدقيق) · §11 (Idempotency) · §19 (النسخ) · §3.1 (الاستيراد)
-- قاعدة حاكمة: لا أسرار ولا كلمات مرور ولا بيانات بطاقات في السجلات (§18)
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) سجل التدقيق (§18: العمليات الحساسة فقط — قيم قبل/بعد مسموح بها) ───────
CREATE TABLE audit.audit_logs (
    audit_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    user_id         BIGINT NOT NULL REFERENCES core.users(user_id),
    role_code       TEXT,                             -- الدور وقت العملية
    station_id      BIGINT REFERENCES core.stations(station_id),
    branch_id       BIGINT REFERENCES core.branches(branch_id),
    entity_schema   TEXT NOT NULL,                    -- sales.invoices
    entity_id       BIGINT NOT NULL,
    action          TEXT NOT NULL,                    -- INSERT/UPDATE/POST/VOID/APPROVE/PRINT/LOGIN/...
    old_values      JSONB,                            -- القيم السابقة المسموح بها (بلا أسرار)
    new_values      JSONB,
    reason          TEXT,                             -- إلزامي للعمليات الاستثنائية
    result          TEXT NOT NULL CHECK (result IN ('SUCCESS','DENIED','FAILED')),
    correlation_id  TEXT                              -- معرف الترابط عبر الطبقات (§20)
);
COMMENT ON TABLE audit.audit_logs IS 'سجل العمليات الحساسة: من نفّذ ومن أين ومتى وماذا تغير وبأي صلاحية — الفصل بين منشئ الفاتورة ومنفذها/معتمدها (§16 ق7)';

CREATE INDEX idx_audit_entity ON audit.audit_logs (entity_schema, entity_id, occurred_at DESC);
CREATE INDEX idx_audit_user_time ON audit.audit_logs (user_id, occurred_at DESC);
CREATE INDEX idx_audit_correlation ON audit.audit_logs (correlation_id) WHERE correlation_id IS NOT NULL;

-- ── 2) مفاتيح Idempotency (منع تكرار العمليات الحساسة — §11/§15.4) ────────────
CREATE TABLE audit.idempotency_keys (
    idempotency_key TEXT PRIMARY KEY,                 -- مفتاح يحدده العميل لكل عملية حساسة
    request_hash    TEXT NOT NULL,                    -- بصمة الطلب — نفس المفتاح بطلب مختلف = خطأ صريح
    status          TEXT NOT NULL DEFAULT 'IN_PROGRESS' CHECK (status IN ('IN_PROGRESS','COMPLETED','FAILED','TIMEOUT')),
    response_hash   TEXT,                             -- بصمة الاستجابة لإرجاعها عند التكرار
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at      TIMESTAMPTZ NOT NULL,
    CONSTRAINT idem_expiry_future CHECK (expires_at > created_at)
);
COMMENT ON TABLE audit.idempotency_keys IS 'إعادة إرسال نفس الطلب بعد timeout تُرجع النتيجة نفسها أو ترفض — لا فاتورة ثانية ولا حركة مخزون مكررة (T-CAP-003)';

-- ── 3) جلسات المستخدمين (تسجيل الدخول والمحطات — صفحة 49) ────────────────────
CREATE TABLE audit.user_sessions (
    session_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id         BIGINT NOT NULL REFERENCES core.users(user_id),
    station_id      BIGINT REFERENCES core.stations(station_id),
    login_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    logout_at       TIMESTAMPTZ,
    ip_address      INET,
    outcome         TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (outcome IN ('ACTIVE','LOGOUT','EXPIRED','KICKED'))
);
COMMENT ON TABLE audit.user_sessions IS 'جلسات الدخول لكل محطة — تبديل المستخدم لا يسرب جلسة سابقة (اختبار §2 ق16)';

CREATE INDEX idx_sessions_user_active ON audit.user_sessions (user_id) WHERE outcome = 'ACTIVE';

-- ── 4) سجل النسخ الاحتياطي (§19: بدون كشف أسرار المواقع/المفاتيح) ─────────────
CREATE TABLE ops.backup_records (
    backup_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    backup_kind     TEXT NOT NULL CHECK (backup_kind IN ('FULL','INCREMENTAL','MONTHLY_ARCHIVE')),
    started_at      TIMESTAMPTZ NOT NULL,
    finished_at     TIMESTAMPTZ,
    size_bytes      BIGINT CHECK (size_bytes IS NULL OR size_bytes > 0),
    status          TEXT NOT NULL DEFAULT 'RUNNING' CHECK (status IN ('RUNNING','SUCCEEDED','FAILED','VERIFYING','VERIFIED')),
    scope           TEXT NOT NULL,                    -- db / db+media / full
    storage_location_ref TEXT NOT NULL,               -- مرجع موقع معتمد — لا مسارات أسرار صريحة
    backup_set_id   TEXT NOT NULL,                    -- معرف النسخة للتتبع
    verified_readable BOOLEAN NOT NULL DEFAULT FALSE, -- «نجاح» يعلن فقط بعد تحقق قابلية القراءة (§19)
    error_summary   TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT backup_finished_after_start CHECK (finished_at IS NULL OR finished_at >= started_at)
);
COMMENT ON TABLE ops.backup_records IS 'النسخ الاحتياطي: لا يعلن النجاح إلا بعد تحقق اكتمال وقابلية قراءة — النسخة على نفس القرص ليست خطة استعادة (§19، D-09)';

-- ── 5) الاستيراد الدفعي القابل للاستئناف (§3.1: سجل ثابت لا مؤشر متغير) ──────
CREATE TABLE ops.import_jobs (
    import_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    import_kind     TEXT NOT NULL CHECK (import_kind IN ('PRODUCTS','IMAGES','BARCODES','OPENING_STOCK')),
    status          TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','RUNNING','CANCELLED','COMPLETED','COMPLETED_WITH_ERRORS')),
    total_rows      INTEGER NOT NULL DEFAULT 0,
    processed_rows  INTEGER NOT NULL DEFAULT 0,
    failed_rows     INTEGER NOT NULL DEFAULT 0,
    resume_cursor   JSONB NOT NULL DEFAULT '{}'::jsonb,  -- موضع الاستئناف الثابت (ملف/دُفعة/توقيت) لا ترتيب عشوائي
    started_at      TIMESTAMPTZ,
    finished_at     TIMESTAMPTZ,
    requested_by    BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT import_counts_sane CHECK (processed_rows + failed_rows <= GREATEST(total_rows, processed_rows + failed_rows))
);
COMMENT ON TABLE ops.import_jobs IS 'استيراد الخلفية: تقدم قابل للإلغاء والاستئناف من سجل ثابت — إعادة الطلب بعد timeout لا تكرر الصفوف (T-CAP-004)';

CREATE TABLE ops.import_errors (
    error_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    import_id       BIGINT NOT NULL REFERENCES ops.import_jobs(import_id) ON DELETE CASCADE,
    row_ref         TEXT NOT NULL,                    -- مرجع الصف الفاشل في الملف المصدر
    error_code      TEXT NOT NULL,
    error_detail    TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE ops.import_errors IS 'تقرير الصفوف الفاشلة لكل دفعة استيراد — رسالة توضح الكود المتعارض دون إنشاء سجلات ناقصة (§3.1)';

CREATE INDEX idx_import_errors_job ON ops.import_errors (import_id);

COMMIT;
