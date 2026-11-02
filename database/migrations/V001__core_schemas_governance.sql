-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V001 · طبقة core: المخططات، الفروع، المحطات، الإعدادات،
-- المستخدمون والأدوار والصلاحيات والاعتمادات الثنائية
-- المرجع: GFM-CSharp 2.4.1 §2 (الطبقات) · §18 (الأمان والتدقيق) · §0.1 (قاعدة 4/13)
-- قاعدة المال والكمية: NUMERIC(19,4) للمال · NUMERIC(18,6) للكميات · TIMESTAMPTZ (UTC)
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) مخططات PostgreSQL (مطابقة لحدود الوحدات في Modular Monolith) ──────────
CREATE SCHEMA IF NOT EXISTS core;       -- التشغيل والحوكمة والأمن
CREATE SCHEMA IF NOT EXISTS catalog;    -- الكتالوج والوحدات والباركود
CREATE SCHEMA IF NOT EXISTS inv;        -- المخزون والحركات والجرد
CREATE SCHEMA IF NOT EXISTS purch;      -- المشتريات والموردون
CREATE SCHEMA IF NOT EXISTS sales;      -- العملاء والمبيعات والورديات
CREATE SCHEMA IF NOT EXISTS finance;    -- الخزينة والمصروفات والمحاسبة
CREATE SCHEMA IF NOT EXISTS print;      -- الطباعة وأجهزتها (GFM 2.4.1 §29–35)
CREATE SCHEMA IF NOT EXISTS audit;      -- التدقيق والـ Idempotency والجلسات
CREATE SCHEMA IF NOT EXISTS ops;        -- النسخ الاحتياطي والاستيراد والتشغيل

COMMENT ON SCHEMA core     IS 'التشغيل والحوكمة: الفروع والمحطات والإعدادات والمستخدمون والصلاحيات';
COMMENT ON SCHEMA catalog  IS 'كتالوج المنتجات: Product/SKU/Unit/Package/Barcode/PLU/VariableWeightProfile/الصور';
COMMENT ON SCHEMA inv      IS 'المخزون: دفتر الحركة والأرصدة والحجز والجرد والتحويلات والدفعات';
COMMENT ON SCHEMA purch    IS 'المشتريات: الموردون وأوامر الشراء والاستلامات وفواتير الموردين وسداداتها ومرتجعاتها';
COMMENT ON SCHEMA sales    IS 'المبيعات: العملاء والفواتير والسطور والمدفوعات والمرتجعات والسلال المعلقة والورديات وأحداث الدرج';
COMMENT ON SCHEMA finance  IS 'المالية: الخزائن والسندات والمصروفات ورأس المال والقيد المزدوج وطبقات التكلفة';
COMMENT ON SCHEMA print    IS 'الطباعة: ملفات الطابعات المعتمدة والقوالب والمهام وقواعد الطباعة التلقائية وسجل الأجهزة';
COMMENT ON SCHEMA audit    IS 'التدقيق: سجل العمليات الحساسة ومفاتيح Idempotency وجلسات المستخدمين';
COMMENT ON SCHEMA ops      IS 'التشغيل: سجلات النسخ الاحتياطي ووظائف الاستيراد الدفعي';

-- ── 2) سجل الترحيلات (خط دفاع أخير لترتيب EF Core Migrations في G3) ──────────
CREATE TABLE core.schema_migrations (
    version       INTEGER PRIMARY KEY,
    name          TEXT NOT NULL,
    checksum      TEXT NOT NULL,                    -- sha256 للملف كما طُبّق
    applied_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    applied_by    TEXT NOT NULL DEFAULT current_user
);
COMMENT ON TABLE core.schema_migrations IS 'سجل الترحيلات المطبقة: كل migration يسجل نسخته وبصمته — إعادة تطبيق نسخة معدلة مرفوضة';

-- ── 3) الفروع والمحطات (§7.1 اختيار الفرع/المخزن المسموح · §16 ق8 المنطقة الزمنية) ──
CREATE TABLE core.branches (
    branch_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code          TEXT NOT NULL UNIQUE,             -- كود قصير ثابت (BR001)
    name_ar       TEXT NOT NULL,
    name_en       TEXT,
    timezone      TEXT NOT NULL DEFAULT 'Africa/Cairo',
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT branches_timezone_nonempty CHECK (length(trim(timezone)) > 0)
);
COMMENT ON TABLE core.branches IS 'الفروع: النطاق التنظيمي للصلاحيات والتقارير — العرض الزمني بتوقيت الفرع المعتمد والتخزين UTC';
COMMENT ON COLUMN core.branches.timezone IS 'معرف منطقة زمنية IANA (افتراضي Africa/Cairo) — يُستخدم للعرض والتقارير لا للتخزين';

CREATE TABLE core.stations (
    station_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id     BIGINT NOT NULL REFERENCES core.branches(branch_id),
    code          TEXT NOT NULL,
    name_ar       TEXT NOT NULL,
    is_pos        BOOLEAN NOT NULL DEFAULT TRUE,
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (branch_id, code)
);
COMMENT ON TABLE core.stations IS 'محطات العمل: كل محطة POS تنتمي لفرع واحد — طابعة الإيصال تُعيَّن لها عبر print.printer_profiles';

-- ── 4) سجل إعدادات النشاط (كل قرارات D-01..D-09 قابلة للتهيئة هنا — لا ثوابت في الكود) ──
CREATE TABLE core.business_settings (
    setting_id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    setting_key           TEXT NOT NULL,                    -- مثل tax.policy_mode
    scope_type            TEXT NOT NULL DEFAULT 'BUSINESS' CHECK (scope_type IN ('BUSINESS','BRANCH')),
    branch_id             BIGINT REFERENCES core.branches(branch_id),
    value_text            TEXT NOT NULL,
    value_type            TEXT NOT NULL DEFAULT 'string' CHECK (value_type IN ('string','integer','decimal','boolean','enum','json')),
    category              TEXT NOT NULL CHECK (category IN ('TAX','INVENTORY','PRICING','WEIGHT','RETURNS','CREDIT','PAYMENTS','OFFLINE','RETENTION','ACCOUNTING','PACKAGING','PRINTING','GENERAL')),
    decision_ref          TEXT,                            -- D-01..D-18 إن وُجد
    confirmation_status   TEXT NOT NULL DEFAULT 'APPROVED_WITH_SPEC'
                            CHECK (confirmation_status IN ('APPROVED_WITH_SPEC','PENDING_OWNER_CONFIRMATION','OWNER_CONFIRMED')),
    description_ar        TEXT NOT NULL,
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT settings_branch_scope CHECK ((scope_type = 'BRANCH') = (branch_id IS NOT NULL))
);
CREATE UNIQUE INDEX uq_settings_scope
    ON core.business_settings (setting_key, scope_type, COALESCE(branch_id, 0));
COMMENT ON TABLE core.business_settings IS 'سجل الإعدادات الموحد: القرارات التجارية (ضريبة/تكلفة/سالب/وزن/خصومات/احتفاظ) إعدادات نشاط — لا تُكتب في الكود';
COMMENT ON COLUMN core.business_settings.decision_ref IS 'مرجع القرار في DECISIONS_REQUIRED (D-xx) — يربط الإعداد بمصدره الحوكمي';
COMMENT ON COLUMN core.business_settings.confirmation_status IS 'PENDING_OWNER_CONFIRMATION: قيمة توصية بانتظار حسم المالك (البنية تعمل والقيمة قابلة للتغيير)';

-- ── 5) المستخدمون والأدوار والصلاحيات (§18: صلاحيات server-side وأقل صلاحية) ──
CREATE TABLE core.users (
    user_id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username          TEXT NOT NULL UNIQUE,
    display_name_ar   TEXT NOT NULL,
    email             TEXT,
    password_hash     TEXT NOT NULL,                   -- Argon2id/bcrypt فقط — لا نص صريح أبداً
    is_active         BOOLEAN NOT NULL DEFAULT TRUE,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE core.users IS 'المستخدمون: تخزين hash فقط — ممنوع كتابة كلمات المرور أو الأسرار في السجلات أو الإعدادات (§18)';

CREATE TABLE core.roles (
    role_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code          TEXT NOT NULL UNIQUE,                -- OWNER/MANAGER/SUPERVISOR/CASHIER/...
    name_ar       TEXT NOT NULL,
    description   TEXT
);
COMMENT ON TABLE core.roles IS 'الأدوار المرجعية: OWNER، MANAGER، SUPERVISOR، CASHIER، STOREKEEPER، ACCOUNTANT، AUDITOR';

CREATE TABLE core.permissions (
    permission_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,              -- مثل sales.discount.over_15
    domain_area     TEXT NOT NULL,                     -- sales/inventory/finance/printing/...
    description_ar  TEXT NOT NULL
);
COMMENT ON TABLE core.permissions IS 'فهرس الصلاحيات الذري: صلاحيات منفصلة لتعديل السعر والخصم والمرتجع ورد الأموال وإلغاء مستند وفتح الدرج وإغلاق الوردية (§18)';

CREATE TABLE core.role_permissions (
    role_id       BIGINT NOT NULL REFERENCES core.roles(role_id) ON DELETE CASCADE,
    permission_id BIGINT NOT NULL REFERENCES core.permissions(permission_id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE core.user_roles (
    user_role_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id     BIGINT NOT NULL REFERENCES core.users(user_id) ON DELETE CASCADE,
    role_id     BIGINT NOT NULL REFERENCES core.roles(role_id) ON DELETE CASCADE,
    branch_id   BIGINT REFERENCES core.branches(branch_id),   -- NULL = كل الفروع
    granted_by  BIGINT REFERENCES core.users(user_id),
    granted_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX uq_user_roles_scope
    ON core.user_roles (user_id, role_id, COALESCE(branch_id, 0));
COMMENT ON TABLE core.user_roles IS 'إسناد الأدوار نطاقه فرع أو كل الفروع — تحقق الخادم من النطاق في كل عملية';

-- ── 6) الاعتمادات الثنائية (فصل الواجبات: لا يوافق صاحب العملية على نفسه — §18) ──
CREATE TABLE core.approvals (
    approval_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entity_type    TEXT NOT NULL,                      -- sales.refund / inv.adjustment / ...
    entity_id      BIGINT NOT NULL,
    requested_by   BIGINT NOT NULL REFERENCES core.users(user_id),
    decided_by     BIGINT REFERENCES core.users(user_id),
    decision       TEXT NOT NULL DEFAULT 'PENDING' CHECK (decision IN ('PENDING','APPROVED','REJECTED')),
    reason         TEXT,
    requested_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    decided_at     TIMESTAMPTZ,
    CONSTRAINT approvals_no_self CHECK (decided_by IS NULL OR decided_by <> requested_by)
);
COMMENT ON TABLE core.approvals IS 'سجل الاعتمادات الثنائية: يمنع القيد موافقة منفذ العملية على نفسه — يخدم D-03/D-04/D-06';

-- فهرس بحث سريع عن الاعتمادات المعلقة لكيان
CREATE INDEX idx_approvals_entity ON core.approvals (entity_type, entity_id);
CREATE INDEX idx_approvals_pending ON core.approvals (decision, requested_at) WHERE decision = 'PENDING';

COMMIT;
