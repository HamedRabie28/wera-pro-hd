-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V007 · طبقة print: سجل الأجهزة وملفات الطابعات المعتمدة
-- وقوالب المستندات ومهام الطباعة وقواعد الطباعة التلقائية
-- المرجع: GFM-CSharp 2.4.1 §10 (الأجهزة) · §29.7 (Printer Profiles) · §29.8 (PrintJob)
--          · §29.9 (الفشل وإعادة المحاولة) · §29.10 (الطباعة التلقائية) · §35 (الاختبارات)
-- قواعد حاكمة: لا يتحول فشل 80 إلى 58 أو A4 تلقائياً · A4 تفصيلي بإجراء صريح فقط ·
-- مهمة طباعة لكل مستند بمفتاح Idempotency فريد · الحالة UNKNOWN لا تعيد الطباعة عمياء
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) سجل الأجهزة الصادق (§10.6: لا محاكاة في الإنتاج — الحالة حقيقية) ──────
CREATE TABLE print.device_registry (
    device_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    station_id      BIGINT REFERENCES core.stations(station_id),
    device_type     TEXT NOT NULL CHECK (device_type IN ('BARCODE_SCANNER','SCALE','RECEIPT_PRINTER','LABEL_PRINTER','CASH_DRAWER','PAYMENT_TERMINAL','A4_PRINTER')),
    device_model    TEXT,
    connection_mode TEXT,                             -- USB_HID / SERIAL / NETWORK / BLUETOOTH
    status          TEXT NOT NULL DEFAULT 'NOT_CONFIGURED'
                    CHECK (status IN ('CONNECTED','DISCONNECTED','UNKNOWN','NOT_CONFIGURED','ERROR')),
    last_checked_at TIMESTAMPTZ,                      -- نتيجة فحص فعلي حديثة — لا ادعاء اتصال بلا فحص
    last_check_result TEXT,
    notes           TEXT,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE print.device_registry IS 'سجل الأجهزة: افتراض كل جهاز NOT_CONFIGURED حتى فحص فعلي موثق — فصل حالة التنفيذ عن حالة الاتصال (§0.1 ق11)';

CREATE INDEX idx_devices_station ON print.device_registry (station_id);

-- ── 2) ملفات الطابعات المعتمدة (§29.7.1 — كل الحقول المطلوبة نصاً) ─────────────
CREATE TABLE print.printer_profiles (
    printer_profile_id  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    printer_id          TEXT NOT NULL UNIQUE,          -- معرف الطابعة الفيزيائي
    windows_queue_name  TEXT NOT NULL,
    device_model        TEXT NOT NULL,
    driver_name         TEXT NOT NULL,
    station_id          BIGINT REFERENCES core.stations(station_id),   -- طابعة محطة أو فرع
    branch_id           BIGINT REFERENCES core.branches(branch_id),
    media_class         TEXT NOT NULL CHECK (media_class IN ('ROLL_80','ROLL_58','A4_DETAILED','LABEL')),
    media_width_mm      NUMERIC(6,2) NOT NULL CHECK (media_width_mm > 0),      -- العرض الاسمي
    printable_width_mm  NUMERIC(6,2) NOT NULL CHECK (printable_width_mm > 0),  -- القابل للطباعة الفعلي
    verified_media_width_mm NUMERIC(6,2) CHECK (verified_media_width_mm > 0),  -- بعد المعايرة (G10)
    dpi_x               INTEGER NOT NULL CHECK (dpi_x > 0),
    dpi_y               INTEGER NOT NULL CHECK (dpi_y > 0),
    left_margin_mm      NUMERIC(6,2) NOT NULL DEFAULT 0 CHECK (left_margin_mm >= 0),
    right_margin_mm     NUMERIC(6,2) NOT NULL DEFAULT 0 CHECK (right_margin_mm >= 0),
    roll_mode           TEXT CHECK (roll_mode IN ('CONTINUOUS','AUTO_CUT','TEAR_OFF')),
    max_continuous_length_mm NUMERIC(10,2),
    adapter_kind        TEXT NOT NULL DEFAULT 'ESC_POS' CHECK (adapter_kind IN ('ESC_POS','WINDOWS_DRIVER','ZPL','TSPL')),
    is_calibrated       BOOLEAN NOT NULL DEFAULT FALSE,-- معايرة الوسيط شرط التوجيه التلقائي (§29.4)
    last_test_at        TIMESTAMPTZ,
    last_test_result    TEXT CHECK (last_test_result IN ('PASS','FAIL','UNKNOWN')),
    last_tested_by      BIGINT REFERENCES core.users(user_id),
    profile_version     INTEGER NOT NULL DEFAULT 1,    -- يرتفع مع كل تغيير معتمد (§29.7.3)
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT prn_widths_sane CHECK (printable_width_mm <= media_width_mm),
    CONSTRAINT prn_verified_sane CHECK (verified_media_width_mm IS NULL OR ABS(verified_media_width_mm - printable_width_mm) <= 2.0)
);
COMMENT ON TABLE print.printer_profiles IS 'ملف الطابعة المعتمد: تغيير الجهاز/الوسيط يوقف التوجيه ويعاير ويعتمد من مسؤول قبل استئناف AUTO (§29.7.3) — لا يعني 80mm أن القابل للطباعة 80mm';
COMMENT ON COLUMN print.printer_profiles.is_calibrated IS 'التوجيه التلقائي معطل حتى نجاح معايرة الوسيط وTest Receipt موسوم (اختبار 25)';

-- ── 3) ربط الطابعات بأنواع المستندات (§29.7.2 — تعيين لكل محطة × نوع مستند) ───
CREATE TABLE print.printer_document_roles (
    role_assignment_id  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    printer_profile_id  BIGINT NOT NULL REFERENCES print.printer_profiles(printer_profile_id),
    station_id          BIGINT NOT NULL REFERENCES core.stations(station_id),
    document_role       TEXT NOT NULL CHECK (document_role IN (
        'CUSTOMER_RECEIPT','ADMIN_COPY','SUPPLIER_DOCUMENTS','FINANCE_VOUCHERS',
        'CONFIDENTIAL_PAYROLL','A4_DETAILED_REPORTS','BARCODE_LABELS')),
    is_auto_routing_enabled BOOLEAN NOT NULL DEFAULT FALSE,   -- يفعَّل فقط بعد المعايرة والاعتماد
    UNIQUE (station_id, document_role)
);
COMMENT ON TABLE print.printer_document_roles IS 'دور الطابعة لكل محطة ونوع مستند — مستند حساس لا يذهب لطابعة مشتركة دون سياسة وصول (اختبار 21)';

-- ── 4) قوالب المستندات (قالب مستقل لكل مستند × وسيط — §29.3) ─────────────────
CREATE TABLE print.document_templates (
    template_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    template_code   TEXT NOT NULL,                    -- ROLL_MEDIUM_80 / ROLL_SMALL_58 / A4_DETAILED_FULL ...
    document_type   TEXT NOT NULL,                    -- CUSTOMER_RECEIPT / SUPPLIER_INVOICE / PAYROLL_SLIP ...
    media_class     TEXT NOT NULL CHECK (media_class IN ('ROLL_80','ROLL_58','A4_DETAILED','LABEL')),
    version         INTEGER NOT NULL DEFAULT 1,       -- نسخة القالب المستخدمة تحفظ مع المهمة (§29.7.3)
    body_definition JSONB NOT NULL,                   -- بنية القالب المعتمدة (Layout Engine G2)
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (template_code, document_type, media_class, version)
);
COMMENT ON TABLE print.document_templates IS 'قالب مستقل لكل نوع مستند × وسيط — ممنوع تكبير/تصغير قالب وسيط لآخر؛ نسخة القالب تحفظ مع كل مهمة فلا يتغير مستند مرحل بتغيير القالب';

-- ── 5) مهام الطباعة (§29.8 — مهمة مستقلة لكل مستند) ──────────────────────────
CREATE TABLE print.print_jobs (
    job_id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_id         BIGINT NOT NULL,              -- معرف المستند الأصلي (فاتورة/سند/تقرير)
    document_type       TEXT NOT NULL,
    template_id         BIGINT NOT NULL REFERENCES print.document_templates(template_id),
    template_version    INTEGER NOT NULL,             -- لقطة نسخة القالب وقت الإنشاء
    printer_profile_id  BIGINT NOT NULL REFERENCES print.printer_profiles(printer_profile_id),
    printer_profile_version INTEGER NOT NULL,         -- لقطة نسخة الملف وقت الإنشاء
    media_class         TEXT NOT NULL CHECK (media_class IN ('ROLL_80','ROLL_58','A4_DETAILED','LABEL')),
    printable_width_mm  NUMERIC(6,2) NOT NULL,        -- لقطة العرض الفعلي وقت الإنشاء
    dpi                 INTEGER NOT NULL,
    copy_type           TEXT NOT NULL CHECK (copy_type IN ('CUSTOMER','ADMIN','REPRINT','ORIGINAL','PAYMENT_VOUCHER','STATEMENT')),
    status              TEXT NOT NULL DEFAULT 'QUEUED' CHECK (status IN (
        'QUEUED','PROCESSING','SUBMITTED_TO_SPOOLER','COMPLETED_CONFIRMED','FAILED',
        'UNKNOWN','HELD_CONFIGURATION_MISMATCH','BLOCKED_PRINTER_UNAVAILABLE','CANCELLED','REPRINT_REQUESTED')),
    attempt_count       INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    max_attempts        INTEGER NOT NULL DEFAULT 3,
    failure_reason      TEXT,
    idempotency_key     TEXT NOT NULL UNIQUE,         -- مهمة فريدة لكل مستند/نسخة (§29.9)
    requested_by        BIGINT NOT NULL REFERENCES core.users(user_id),
    correlation_id      TEXT,
    related_reprint_of  BIGINT REFERENCES print.print_jobs(job_id),  -- إعادة الطباعة ترتبط بالأصل بعلامة REPRINT
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at        TIMESTAMPTZ
);
COMMENT ON TABLE print.print_jobs IS 'مهمة طباعة مستقلة لكل مستند: فشلها لا يلغي البيع — لا ينشأ مستند مالي عند إعادة الطباعة، ولا يتحول الوسيط عند الفشل (§29.9)';
COMMENT ON COLUMN print.print_jobs.related_reprint_of IS 'إعادة الطباعة الصريحة مهمة جديدة مرتبطة بالأصل بعلامة نسخة إعادة طباعة — لا نسخة مالية جديدة';

CREATE INDEX idx_print_jobs_doc ON print.print_jobs (document_type, document_id);
CREATE INDEX idx_print_jobs_status ON print.print_jobs (status, created_at);
CREATE INDEX idx_print_jobs_correlation ON print.print_jobs (correlation_id) WHERE correlation_id IS NOT NULL;

-- ── 6) قواعد الطباعة التلقائية (§29.10 — سياسة مستقلة لكل نوع مستند) ──────────
CREATE TABLE print.auto_print_rules (
    rule_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_type   TEXT NOT NULL UNIQUE,
    policy          TEXT NOT NULL CHECK (policy IN ('AUTO_AFTER_POST','ELECTRONIC_ONLY','EXPLICIT_USER','EXPLICIT_A4_DETAILED')),
    printer_role    TEXT,                             -- الدور المستهدف عند AUTO
    notes           TEXT,
    CONSTRAINT a4_never_auto CHECK (
        document_type <> 'A4_DETAILED_REPORT' OR policy = 'EXPLICIT_A4_DETAILED')
);
COMMENT ON TABLE print.auto_print_rules IS 'سياسة طباعة مستقلة لكل نوع مستند — نسخة الإدارة إلكترونية والطباعة بإعداد إداري، وA4 التفصيلي بإجراء صريح فقط (§29.10/§29.11)';

COMMIT;
