-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V006 · طبقة finance: الخزائن والسندات والمصروفات
-- ورأس المال والقيد المزدوج وطبقات التكلفة وتسويات مزودي الدفع
-- المرجع: GFM-CSharp 2.4.1 §14 (الورديات والخزينة ورأس المال) · §15 (المحاسبة) · D-02/D-12
-- قواعد حاكمة: التحويل الداخلي لا يولد إيراداً ولا مصروفاً (§14.2) · القيد متوازن قبل
-- الترحيل (§15.2) · لا ترحيل في فترة مغلقة (§15.2) · درج الكاشير ≠ البنك ≠ مزود الدفع
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) حسابات الخزينة (أرصدة منفصلة — §14.2) ────────────────────────────────
CREATE TABLE finance.treasury_accounts (
    treasury_account_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,
    name_ar         TEXT NOT NULL,
    account_kind    TEXT NOT NULL CHECK (account_kind IN ('CASH_DRAWER','SAFE','BANK','PAYMENT_PROVIDER')),
    branch_id       BIGINT REFERENCES core.branches(branch_id),   -- NULL = مركزي
    currency        TEXT NOT NULL DEFAULT 'EGP',
    is_active       BOOLEAN NOT NULL DEFAULT TRUE
);
COMMENT ON TABLE finance.treasury_accounts IS 'درج الكاشير والخزينة والبنك ومزود الدفع أرصدة منفصلة — ليست رصيداً واحداً (§14.2)';

-- ربط المدفوعات والسدادات المؤجلة بحسابات الخزينة
ALTER TABLE sales.payments
    ADD CONSTRAINT payments_treasury_fk FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id);
ALTER TABLE purch.supplier_payments
    ADD CONSTRAINT suppay_treasury_fk FOREIGN KEY (treasury_account_id) REFERENCES finance.treasury_accounts(treasury_account_id);

-- ── 2) حركات الخزينة (أثر واحد لكل حركة — §14.2) ─────────────────────────────
CREATE TABLE finance.treasury_transactions (
    txn_id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    treasury_account_id BIGINT NOT NULL REFERENCES finance.treasury_accounts(treasury_account_id),
    direction       SMALLINT NOT NULL CHECK (direction IN (1,-1)),   -- 1 قبض / -1 صرف
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    source_doc_type TEXT NOT NULL,                    -- sales.payment / finance.voucher / finance.expense / finance.capital_event
    source_doc_id   BIGINT NOT NULL,
    reason          TEXT,
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    branch_id       BIGINT NOT NULL REFERENCES core.branches(branch_id)
);
COMMENT ON TABLE finance.treasury_transactions IS 'دفتر الخزينة: كل حدث مالي ينشئ أثراً واحداً هنا — المرتجع لا يخصم النقدية مرتين (§15.4)';

CREATE INDEX idx_treasury_txn_account_time ON finance.treasury_transactions (treasury_account_id, occurred_at DESC);
CREATE INDEX idx_treasury_txn_source ON finance.treasury_transactions (source_doc_type, source_doc_id);

-- ── 3) السندات: قبض/صرف/تحويل (§14.2) ───────────────────────────────────────
CREATE TABLE finance.vouchers (
    voucher_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    voucher_number  TEXT NOT NULL UNIQUE,
    voucher_type    TEXT NOT NULL CHECK (voucher_type IN ('RECEIPT','DISBURSEMENT','TRANSFER')),
    treasury_account_id   BIGINT NOT NULL REFERENCES finance.treasury_accounts(treasury_account_id),
    counter_treasury_account_id BIGINT REFERENCES finance.treasury_accounts(treasury_account_id), -- إلزامي للتحويل
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    counterparty_type TEXT CHECK (counterparty_type IN ('CUSTOMER','SUPPLIER','EMPLOYEE','OWNER','OTHER')),
    counterparty_id BIGINT,
    reason          TEXT NOT NULL CHECK (length(trim(reason)) >= 3),
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT voucher_transfer_accounts CHECK (
        voucher_type <> 'TRANSFER' OR (counter_treasury_account_id IS NOT NULL AND counter_treasury_account_id <> treasury_account_id)),
    CONSTRAINT voucher_transfer_no_counterparty CHECK (
        voucher_type <> 'TRANSFER' OR counterparty_type IS NULL)
);
COMMENT ON TABLE finance.vouchers IS 'سندات القبض والصرف والتحويل: التحويل الداخلي بين خزينتين لا يولد إيراداً ولا مصروفاً (§14.2) — الترحيل ينشئ حركتي خزينة متقابلتين';

-- ── 4) المصروفات (§14.4) ────────────────────────────────────────────────────
CREATE TABLE finance.expense_categories (
    category_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,
    name_ar         TEXT NOT NULL,
    parent_id       BIGINT REFERENCES finance.expense_categories(category_id),
    is_active       BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE finance.expenses (
    expense_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    expense_number  TEXT NOT NULL UNIQUE,
    category_id     BIGINT NOT NULL REFERENCES finance.expense_categories(category_id),
    branch_id       BIGINT NOT NULL REFERENCES core.branches(branch_id),
    expense_date    DATE NOT NULL,
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    tax_amount      NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (tax_amount >= 0),
    payment_method  TEXT NOT NULL CHECK (payment_method IN ('CASH','BANK_TRANSFER','CHEQUE','CARD')),
    treasury_account_id BIGINT REFERENCES finance.treasury_accounts(treasury_account_id),
    beneficiary     TEXT,
    cost_center     TEXT,
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','APPROVED','POSTED','CANCELLED')),
    approval_id     BIGINT REFERENCES core.approvals(approval_id),
    attachment_media_id BIGINT REFERENCES catalog.media_assets(media_id),
    notes           TEXT,
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE finance.expenses IS 'المصروف: اعتماد ثم سداد — أثر مالي واحد على الخزينة/البنك والإلغاء بعكس موثق لا حذف (§14.4)';

-- ── 5) رأس المال والأحداث الافتتاحية (§14.3) ────────────────────────────────
CREATE TABLE finance.capital_events (
    capital_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_number    TEXT NOT NULL UNIQUE,
    event_type      TEXT NOT NULL CHECK (event_type IN ('OWNER_CONTRIBUTION','OWNER_WITHDRAWAL','LOAN_RECEIVED','LOAN_REPAYMENT','OPENING_BALANCE')),
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    treasury_account_id BIGINT REFERENCES finance.treasury_accounts(treasury_account_id),
    event_date      DATE NOT NULL,
    description     TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    approval_id     BIGINT REFERENCES core.approvals(approval_id),
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT capital_opening_needs_approval CHECK (event_type <> 'OPENING_BALANCE' OR approval_id IS NOT NULL)
);
COMMENT ON TABLE finance.capital_events IS 'رأس المال بحدث موثق (مساهمة/سحب/قرض/رصيد افتتاحي معتمد) — لا يُساوى بالمبيعات أو الصندوق أو قيمة المخزون (§14.3، D-14)';

-- ── 6) القيد المزدوج (يبني جاهزاً ويعمل بحسب نطاق D-12 المعتمد) ───────────────
CREATE TABLE finance.gl_accounts (
    gl_account_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,             -- دليل حسابات هرمي (1 أصول، 2 خصوم...)
    name_ar         TEXT NOT NULL,
    account_type    TEXT NOT NULL CHECK (account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
    parent_id       BIGINT REFERENCES finance.gl_accounts(gl_account_id),
    is_postable     BOOLEAN NOT NULL DEFAULT TRUE,    -- حساب ترحيل لا مجلد
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT gl_not_self_parent CHECK (parent_id IS NULL OR parent_id <> gl_account_id)
);
COMMENT ON TABLE finance.gl_accounts IS 'دليل الحسابات: يُشغَّل بالكامل عند اعتماد D-12 (أ) — البنية جاهزة منذ G1 والتقارير تعلن نطاقها';

CREATE TABLE finance.fiscal_periods (
    period_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name            TEXT NOT NULL,
    period_start    DATE NOT NULL,
    period_end      DATE NOT NULL,
    status          TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','CLOSED')),
    closed_at       TIMESTAMPTZ,
    closed_by       BIGINT REFERENCES core.users(user_id),
    CONSTRAINT period_range CHECK (period_end >= period_start)
);
COMMENT ON TABLE finance.fiscal_periods IS 'الفترة المالية: الترحيل في فترة CLOSED ممنوع — الاستثناء الإجرائي موثق بموافقة (§15.2)';

CREATE UNIQUE INDEX uq_fiscal_periods_range ON finance.fiscal_periods (period_start, period_end);

CREATE TABLE finance.journal_entries (
    entry_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_number    TEXT NOT NULL UNIQUE,
    period_id       BIGINT NOT NULL REFERENCES finance.fiscal_periods(period_id),
    source_doc_type TEXT NOT NULL,                    -- sales.invoice / purch.receipt / finance.expense / ...
    source_doc_id   BIGINT NOT NULL,
    description     TEXT,
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','REVERSED')),
    posted_at       TIMESTAMPTZ,
    reversed_by_entry_id BIGINT REFERENCES finance.journal_entries(entry_id),
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE finance.journal_entries IS 'القيد يرتبط بمصدره التجاري دائماً — القيد المرحل لا يعدل بل يُعكس بقيد مرتبط (§15.2)';

CREATE INDEX idx_journal_source ON finance.journal_entries (source_doc_type, source_doc_id);

CREATE TABLE finance.journal_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_id        BIGINT NOT NULL REFERENCES finance.journal_entries(entry_id) ON DELETE CASCADE,
    gl_account_id   BIGINT NOT NULL REFERENCES finance.gl_accounts(gl_account_id),
    debit           NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (debit >= 0),
    credit          NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (credit >= 0),
    line_no         INTEGER NOT NULL,
    CONSTRAINT jl_debit_xor_credit CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0))
);
COMMENT ON TABLE finance.journal_lines IS 'سطر القيد: مدين أو دائن — لا الاثنان معاً ولا صفر';

-- (أ) توازن القيد عند الالتزام (قيد مؤجل يسمح بترتيب الإدراج الحر)
CREATE OR REPLACE FUNCTION finance.tf_journal_balanced()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_debit NUMERIC(19,4); v_credit NUMERIC(19,4); v_status TEXT;
BEGIN
    SELECT status INTO v_status FROM finance.journal_entries WHERE entry_id = COALESCE(NEW.entry_id, OLD.entry_id);
    SELECT COALESCE(SUM(debit),0), COALESCE(SUM(credit),0) INTO v_debit, v_credit
      FROM finance.journal_lines WHERE entry_id = COALESCE(NEW.entry_id, OLD.entry_id);
    IF v_status = 'POSTED' AND v_debit <> v_credit THEN
        RAISE EXCEPTION 'JOURNAL_UNBALANCED: مدين % ≠ دائن % — لا يرحّل قيد غير متوازن (§15.2)', v_debit, v_credit
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_journal_balanced
    AFTER INSERT OR UPDATE OR DELETE ON finance.journal_lines
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finance.tf_journal_balanced();

-- (ب) عند الترحيل: الفترة مفتوحة + القيد متوازن (§15.2)
CREATE OR REPLACE FUNCTION finance.tf_journal_period_open()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_period TEXT; v_debit NUMERIC(19,4); v_credit NUMERIC(19,4);
BEGIN
    SELECT fp.status INTO v_period
      FROM finance.journal_entries je JOIN finance.fiscal_periods fp ON fp.period_id = je.period_id
     WHERE je.entry_id = NEW.entry_id;
    IF NEW.status = 'POSTED' AND v_period <> 'OPEN' THEN
        RAISE EXCEPTION 'FISCAL_PERIOD_CLOSED: الترحيل في فترة مغلقة ممنوع — فتح استثنائي بموافقة موثقة (§15.2)'
            USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.status = 'POSTED' AND OLD.status = 'DRAFT' THEN
        SELECT COALESCE(SUM(debit),0), COALESCE(SUM(credit),0) INTO v_debit, v_credit
          FROM finance.journal_lines WHERE entry_id = NEW.entry_id;
        IF v_debit <> v_credit THEN
            RAISE EXCEPTION 'JOURNAL_UNBALANCED: مدين % ≠ دائن % — لا يرحّل قيد غير متوازن (§15.2)', v_debit, v_credit
                USING ERRCODE = 'check_violation';
        END IF;
        IF v_debit = 0 THEN
            RAISE EXCEPTION 'JOURNAL_EMPTY: لا يرحّل قيد بلا أسطر';
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_journal_period_open
    BEFORE UPDATE OF status ON finance.journal_entries
    FOR EACH ROW EXECUTE FUNCTION finance.tf_journal_period_open();

-- (ج) القيد المرحل لا يعدل ولا يحذف
CREATE OR REPLACE FUNCTION finance.tf_journal_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND OLD.status = 'POSTED' THEN
        RAISE EXCEPTION 'JOURNAL_IMMUTABLE: القيد المرحل لا يُحذف — العكس بقيد معاكس مرتبط (§15.2)';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'POSTED'
       AND NEW.status NOT IN ('POSTED','REVERSED') THEN
        RAISE EXCEPTION 'JOURNAL_IMMUTABLE: القيد المرحل لا يتحول إلا إلى معكوس';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'POSTED' AND NEW.status = 'POSTED'
       AND (NEW.period_id <> OLD.period_id OR NEW.source_doc_type <> OLD.source_doc_type
            OR NEW.source_doc_id <> OLD.source_doc_id) THEN
        RAISE EXCEPTION 'JOURNAL_IMMUTABLE: بيانات القيد المرحل مجمّدة';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

CREATE TRIGGER trg_journal_immutable
    BEFORE UPDATE OR DELETE ON finance.journal_entries
    FOR EACH ROW EXECUTE FUNCTION finance.tf_journal_immutable();

-- منع إضافة/تعديل أسطر قيد مرحل (قيد الإدخال فقط قبل الترحيل)
CREATE OR REPLACE FUNCTION finance.tf_journal_lines_frozen()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status TEXT;
BEGIN
    SELECT status INTO v_status FROM finance.journal_entries
     WHERE entry_id = COALESCE(NEW.entry_id, OLD.entry_id);
    IF v_status = 'POSTED' THEN
        RAISE EXCEPTION 'JOURNAL_LINES_FROZEN: أسطر القيد المرحل مجمّدة';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

CREATE TRIGGER trg_journal_lines_frozen
    BEFORE INSERT OR UPDATE OR DELETE ON finance.journal_lines
    FOR EACH ROW EXECUTE FUNCTION finance.tf_journal_lines_frozen();

-- ── 7) طبقات التكلفة — المتوسط المرجح المتحرك (D-02) ─────────────────────────
CREATE TABLE finance.cost_layers (
    layer_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    costing_method  TEXT NOT NULL DEFAULT 'WAC' CHECK (costing_method IN ('WAC','FIFO')),
    avg_unit_cost   NUMERIC(19,4) NOT NULL CHECK (avg_unit_cost >= 0),   -- تكلفة الوحدة الأساسية
    qty_on_layer    NUMERIC(18,6) NOT NULL DEFAULT 0,
    last_receipt_at TIMESTAMPTZ,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (sku_id, warehouse_id)
);
COMMENT ON TABLE finance.cost_layers IS 'حالة التكلفة الحالية (WAC افتراضياً بانتظار D-02): متوسط متحرك يُحدَّث مع كل استلام — الفاتورة تحفظ لقطة التكلفة وقت ترحيلها لا تكلفة اليوم';

-- ── 8) تسويات مزودي الدفع (authorization ≠ settlement — §15.4) ────────────────
CREATE TABLE finance.provider_settlements (
    settlement_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    provider_name   TEXT NOT NULL,                    -- D-11: معطل حتى اعتماد المزود
    settlement_date DATE NOT NULL,
    gross_amount    NUMERIC(19,4) NOT NULL CHECK (gross_amount >= 0),
    fees            NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (fees >= 0),
    net_amount      NUMERIC(19,4) NOT NULL,
    bank_treasury_account_id BIGINT REFERENCES finance.treasury_accounts(treasury_account_id),
    reference_no    TEXT,
    reconciled      BOOLEAN NOT NULL DEFAULT FALSE,
    reconciled_at   TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT settlement_math CHECK (ABS(net_amount + fees - gross_amount) < 0.005)
);
COMMENT ON TABLE finance.provider_settlements IS 'تسوية مزود الدفع: مطابقة ما صُرّح به مع ما وصل البنك فعلاً بعد العمولات — التقارير تفصل التحصيل عن التسوية (§16 ق2)';

COMMIT;
