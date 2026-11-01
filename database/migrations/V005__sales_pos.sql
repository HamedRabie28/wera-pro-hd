-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V005 · طبقة sales: العملاء والفواتير والسطور والمدفوعات
-- والمرتجعات والسلال المعلقة والورديات وأحداث درج النقد
-- المرجع: GFM-CSharp 2.4.1 §9 (POS) · §5 (الطرق الست للوزن) · §12.1 · §14.1
-- قواعد حاكمة: لقطات السعر/التكلفة/التحويل عند الترحيل (§16 ق6) · المستند المرحل
-- لا يُعدل بصمت (§0.1 ق6) · مصدر الوزن مسجل دائماً ولا يُعرض المشتق كقراءة ميزان
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) العملاء (§13: آجل بحد ائتمان عند اعتماد D-05) ────────────────────────
CREATE TABLE sales.customers (
    customer_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,
    name_ar         TEXT NOT NULL,
    phone           TEXT,
    email           TEXT,
    tax_id          TEXT,
    credit_limit    NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (credit_limit >= 0),  -- D-05
    credit_enabled  BOOLEAN NOT NULL DEFAULT FALSE,     -- التمكين لعملاء محددين فقط
    payment_terms_days INTEGER NOT NULL DEFAULT 0,
    account_status  TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (account_status IN ('ACTIVE','SUSPENDED','ARCHIVED')),
    notes           TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT cust_credit_needs_limit CHECK (NOT credit_enabled OR credit_limit > 0)
);
COMMENT ON TABLE sales.customers IS 'العملاء: رصيد الحساب مشتق من الفواتير الآجلة والتحصيلات (لا يُخزن رقماً حراً) — الآجل معطل افتراضياً بانتظار D-05';

-- ── 2) الفواتير (الرأس) ─────────────────────────────────────────────────────
CREATE TABLE sales.invoices (
    invoice_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_number  TEXT NOT NULL,                     -- تسلسل لكل فرع (BR-2026-000001)
    branch_id       BIGINT NOT NULL REFERENCES core.branches(branch_id),
    station_id      BIGINT NOT NULL REFERENCES core.stations(station_id),
    shift_id        BIGINT,                            -- FK يُضاف بعد إنشاء sales.shifts أدناه
    cashier_user_id BIGINT NOT NULL REFERENCES core.users(user_id),
    customer_id     BIGINT REFERENCES sales.customers(customer_id),   -- NULL = عميل نقدي
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','VOID','PARTIAL_RETURN','FULL_RETURN')),
    currency        TEXT NOT NULL DEFAULT 'EGP',
    tax_mode_snapshot TEXT NOT NULL DEFAULT 'VAT_INCLUSIVE' CHECK (tax_mode_snapshot IN ('VAT_INCLUSIVE','VAT_EXCLUSIVE','ZERO_TAX')),
    gross_total     NUMERIC(19,4) NOT NULL DEFAULT 0,
    line_discount_total NUMERIC(19,4) NOT NULL DEFAULT 0,
    invoice_discount    NUMERIC(19,4) NOT NULL DEFAULT 0,
    tax_total       NUMERIC(19,4) NOT NULL DEFAULT 0,
    net_total       NUMERIC(19,4) NOT NULL DEFAULT 0,  -- صافي المبيعات دون الضريبة وفق التعريف المعتمد
    paid_total      NUMERIC(19,4) NOT NULL DEFAULT 0,
    returned_total  NUMERIC(19,4) NOT NULL DEFAULT 0,
    idempotency_key TEXT,                              -- منع تكرار نفس أمر الدفع (§15.4)
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    posted_at       TIMESTAMPTZ,
    voided_at       TIMESTAMPTZ,
    void_reason     TEXT,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (branch_id, invoice_number)
);
COMMENT ON TABLE sales.invoices IS 'فاتورة البيع: الحالة DRAFT قابلة للتحرير، وPOSTED مجمّدة بالكامل — الإلغاء VOID والمرتجع مستندان لا حذف (§0.1 ق6)';
COMMENT ON COLUMN sales.invoices.tax_mode_snapshot IS 'لقطة سياسة الضريبة وقت الترحيل (D-01) — تغيير السياسة لاحقاً لا يعيد حساب التاريخ';
COMMENT ON COLUMN sales.invoices.idempotency_key IS 'مفتاح Idempotency لعملية إنشاء الفاتورة — إعادة الإرسال بعد timeout لا تنشئ فاتورة ثانية';

CREATE INDEX idx_invoices_branch_time ON sales.invoices (branch_id, created_at DESC);
CREATE INDEX idx_invoices_shift ON sales.invoices (shift_id);
CREATE INDEX idx_invoices_status ON sales.invoices (status);
CREATE INDEX idx_invoices_customer ON sales.invoices (customer_id) WHERE customer_id IS NOT NULL;
CREATE UNIQUE INDEX uq_invoices_idem ON sales.invoices (idempotency_key) WHERE idempotency_key IS NOT NULL;

-- ── 3) سطور الفاتورة — لقطات كاملة + مصدر الوزن (§16 ق6) ─────────────────────
CREATE TABLE sales.invoice_lines (
    line_id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_id          BIGINT NOT NULL REFERENCES sales.invoices(invoice_id) ON DELETE CASCADE,
    line_no             INTEGER NOT NULL,
    sku_id              BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    sale_mode           TEXT NOT NULL CHECK (sale_mode IN ('COUNT','WEIGHT','PACK','SERIALIZED')),  -- لقطة صيغة البيع
    package_id          BIGINT REFERENCES catalog.packages(package_id),     -- عند بيع صيغة PACK
    qty                 NUMERIC(18,6) NOT NULL CHECK (qty > 0),             -- الكمية بوحدة البيع
    uom_id              BIGINT NOT NULL REFERENCES catalog.units(uom_id),   -- وحدة البيع
    conversion_factor   NUMERIC(18,6) NOT NULL,                             -- لقطة التحويل للوحدة الأساسية
    base_qty            NUMERIC(18,6) GENERATED ALWAYS AS (qty * conversion_factor) STORED,
    -- الوزن (صيغة WEIGHT فقط): القيمة والمصدر مسجلان دائماً (§5.1)
    weight_kg           NUMERIC(14,3),
    weight_source       TEXT CHECK (weight_source IN
                        ('SCALE_READING','QUICK_PICK','MANUAL_WEIGHT','AMOUNT_DERIVED','WEIGHT_LABEL','PACK_PREWEIGHED')),
    amount_target       NUMERIC(19,4),                 -- المبلغ المستهدف عند AMOUNT_DERIVED
    tare_kg             NUMERIC(14,3),
    -- لقطات التسعير والضريبة (لا يُعاد حساب التاريخ بأسعار اليوم — §16 ق6)
    unit_price          NUMERIC(19,4) NOT NULL CHECK (unit_price > 0),
    price_list_id       BIGINT REFERENCES catalog.price_lists(price_list_id),
    tax_treatment       TEXT NOT NULL DEFAULT 'TAXABLE' CHECK (tax_treatment IN ('TAXABLE','ZERO_RATED','EXEMPT')),
    tax_rate_snapshot   NUMERIC(7,4) NOT NULL DEFAULT 0 CHECK (tax_rate_snapshot >= 0),
    line_discount       NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (line_discount >= 0),
    invoice_discount_share NUMERIC(19,4) NOT NULL DEFAULT 0,   -- توزيع حتمي محفوظ للمجموع (§16 ق5)
    tax_amount          NUMERIC(19,4) NOT NULL DEFAULT 0,
    net_amount          NUMERIC(19,4) NOT NULL,        -- صافي السطر بعد الخصومات
    -- لقطة التكلفة وقت الترحيل (§15.3)
    cost_snapshot       NUMERIC(19,4) CHECK (cost_snapshot IS NULL OR cost_snapshot >= 0),
    cost_method_snapshot TEXT CHECK (cost_method_snapshot IN ('WAC','FIFO')),
    serial_id           BIGINT REFERENCES inv.serials(serial_id),   -- صيغة SERIALIZED
    notes               TEXT,
    UNIQUE (invoice_id, line_no),
    CONSTRAINT line_weight_fields CHECK (sale_mode <> 'WEIGHT' OR (weight_kg IS NOT NULL AND weight_source IS NOT NULL AND weight_kg > 0)),
    CONSTRAINT line_weight_only_for_weight CHECK (sale_mode = 'WEIGHT' OR (weight_kg IS NULL AND weight_source IS NULL)),
    CONSTRAINT line_amount_target_source CHECK (amount_target IS NULL OR weight_source = 'AMOUNT_DERIVED'),
    CONSTRAINT line_tare_nonneg CHECK (tare_kg IS NULL OR tare_kg >= 0)
);
COMMENT ON TABLE sales.invoice_lines IS 'سطر الفاتورة يحمل لقطات كاملة (سعر/ضريبة/تحويل/تكلفة) — ربح الفواتير التاريخية لا يُحسب بتكلفة اليوم أبداً';
COMMENT ON COLUMN sales.invoice_lines.weight_source IS 'SCALE_READING ميزان · QUICK_PICK وزن سريع · MANUAL_WEIGHT يدوي · AMOUNT_DERIVED مشتق من مبلغ (لا يعرض كقراءة ميزان) · WEIGHT_LABEL ملصق موزون · PACK_PREWEIGHED عبوة معدة';

CREATE INDEX idx_invoice_lines_sku ON sales.invoice_lines (sku_id);
CREATE INDEX idx_invoice_lines_invoice ON sales.invoice_lines (invoice_id);

-- ── 4) المدفوعات (تفويض ≠ تحصيل — §15.4) ────────────────────────────────────
CREATE TABLE sales.payments (
    payment_id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_id          BIGINT NOT NULL REFERENCES sales.invoices(invoice_id),
    payment_method      TEXT NOT NULL CHECK (payment_method IN ('CASH','CARD','SPLIT_CASH','SPLIT_CARD','CREDIT','OTHER')),
    amount              NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    status              TEXT NOT NULL DEFAULT 'CAPTURED' CHECK (status IN ('AUTHORIZED','CAPTURED','SETTLED','FAILED','REFUNDED','CANCELLED')),
    provider_name       TEXT,                          -- مزود الدفع (D-11 معطل حتى الاعتماد)
    provider_reference  TEXT,                          -- مرجع المزود — لا PIN ولا CVV ولا PAN كامل (§9.6)
    treasury_account_id BIGINT,                        -- الخزينة/درج الكاشير (FK يضاف في V006)
    received_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    settled_at          TIMESTAMPTZ,
    created_by          BIGINT NOT NULL REFERENCES core.users(user_id),
    CONSTRAINT pay_credit_no_provider CHECK (payment_method <> 'CREDIT' OR (provider_name IS NULL AND provider_reference IS NULL))
);
COMMENT ON TABLE sales.payments IS 'المدفوعات: تصريح البطاقة AUTHORIZED ليس تحصيلاً نهائياً — التحصيل CAPTURED/SETTLED حالة مستقلة تفصل في التقارير (§16 ق2)';
COMMENT ON COLUMN sales.payments.provider_reference IS 'مرجع المزود فقط — تخزين PIN/CVV/PAN الكامل ممنوع منعاً باتاً (T-PAY-003)';

CREATE INDEX idx_payments_invoice ON sales.payments (invoice_id);
CREATE INDEX idx_payments_method_status ON sales.payments (payment_method, status);

-- ── 5) المرتجعات ورد الأموال (ترتبط بالأصل وترد بوسيلة الدفع الأصلية — D-04) ──
CREATE TABLE sales.refunds (
    refund_id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    refund_number       TEXT NOT NULL UNIQUE,
    original_invoice_id BIGINT NOT NULL REFERENCES sales.invoices(invoice_id),  -- الربط بالأصل إلزامي
    branch_id           BIGINT NOT NULL REFERENCES core.branches(branch_id),
    approval_id         BIGINT REFERENCES core.approvals(approval_id),          -- اعتماد مشرف إلزامي (D-04)
    status              TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    refund_method       TEXT NOT NULL CHECK (refund_method IN ('CASH','CARD','CREDIT_REVERSAL')),
    restock             BOOLEAN NOT NULL DEFAULT TRUE,   -- إعادة للمخزون أم هالك
    total               NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (total >= 0),
    reason              TEXT NOT NULL CHECK (length(trim(reason)) >= 3),
    posted_at           TIMESTAMPTZ,
    created_by          BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT refund_approval_required CHECK (approval_id IS NOT NULL)
);
COMMENT ON TABLE sales.refunds IS 'المرتجع: اعتماد مشرف + سبب + رد بوسيلة الدفع الأصلية (البطاقة للبطاقة — D-04)؛ الترحيل يعكس المخزون والمال مرة واحدة مرتبطة بالأصل';

CREATE TABLE sales.refund_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    refund_id       BIGINT NOT NULL REFERENCES sales.refunds(refund_id) ON DELETE CASCADE,
    original_line_id BIGINT NOT NULL REFERENCES sales.invoice_lines(line_id),   -- مرجع السطر الأصلي
    qty             NUMERIC(18,6) NOT NULL CHECK (qty > 0),    -- لا يتجاوز المباع (تحقق الخدمة + اختبار)
    unit_refund     NUMERIC(19,4) NOT NULL CHECK (unit_refund >= 0),
    line_total      NUMERIC(19,4) NOT NULL CHECK (line_total >= 0),
    UNIQUE (refund_id, original_line_id)
);

-- ── 6) السلال المعلقة (§9: تعليق واستعادة دون فقد) ───────────────────────────
CREATE TABLE sales.held_carts (
    cart_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    station_id      BIGINT NOT NULL REFERENCES core.stations(station_id),
    held_by         BIGINT NOT NULL REFERENCES core.users(user_id),
    customer_id     BIGINT REFERENCES sales.customers(customer_id),
    cart_snapshot   JSONB NOT NULL,                    -- لقطة السلة كاملة قابلة للاستعادة
    held_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    restored_at     TIMESTAMPTZ,
    restored_by     BIGINT REFERENCES core.users(user_id)
);
COMMENT ON TABLE sales.held_carts IS 'سلة معلقة بلقطة JSONB كاملة — الاستعادة لا تنشئ فاتورة مكررة ولا تفقد مسودة (اختبار G7)';

CREATE INDEX idx_held_carts_station ON sales.held_carts (station_id) WHERE restored_at IS NULL;

-- ── 7) الورديات (فتح/إغلاق برصيد موثق وفرق مسجل — §14.1) ────────────────────
CREATE TABLE sales.shifts (
    shift_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    station_id      BIGINT NOT NULL REFERENCES core.stations(station_id),
    opened_by       BIGINT NOT NULL REFERENCES core.users(user_id),
    closed_by       BIGINT REFERENCES core.users(user_id),
    status          TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','CLOSED')),
    opening_float   NUMERIC(19,4) NOT NULL DEFAULT 0 CHECK (opening_float >= 0),
    expected_cash   NUMERIC(19,4),                     -- المحسوبة من مبيعات الوردية
    counted_cash    NUMERIC(19,4),                     -- الفعلية المعدودة
    variance        NUMERIC(19,4) GENERATED ALWAYS AS (COALESCE(counted_cash,0) - COALESCE(expected_cash,0)) STORED,
    variance_reason TEXT,                              -- إلزامي عند فرق ≠ 0 (تحقق الخدمة)
    opened_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at       TIMESTAMPTZ
);
COMMENT ON TABLE sales.shifts IS 'الوردية: وردية واحدة مفتوحة لكل محطة (فهرس فريد جزئي) — الفرق محسوب عمودياً والإغلاق نهائي لا يعدل بلا إجراء موثق';

-- وردية مفتوحة واحدة لكل محطة في أي لحظة (منع الفتح المزدوج)
CREATE UNIQUE INDEX uq_shifts_open_per_station
    ON sales.shifts (station_id) WHERE status = 'OPEN';

ALTER TABLE sales.invoices
    ADD CONSTRAINT invoices_shift_fk FOREIGN KEY (shift_id) REFERENCES sales.shifts(shift_id);

-- ── 8) أحداث درج النقد (فتح بصلاحية وسبب وأزمنة صادقة — §9.9/§10.2) ──────────
CREATE TABLE sales.drawer_events (
    event_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    station_id      BIGINT NOT NULL REFERENCES core.stations(station_id),
    shift_id        BIGINT REFERENCES sales.shifts(shift_id),
    event_type      TEXT NOT NULL CHECK (event_type IN ('OPENED_BY_SALE','MANUAL_OPEN','FAILED','UNKNOWN')),
    reason          TEXT,
    requested_at    TIMESTAMPTZ NOT NULL,
    responded_at    TIMESTAMPTZ,                       -- NULL عند UNKNOWN = لا ندّعي استجابة
    result_status   TEXT NOT NULL DEFAULT 'OPENED' CHECK (result_status IN ('OPENED','DENIED','FAILED','UNKNOWN')),
    triggered_by    BIGINT NOT NULL REFERENCES core.users(user_id)
);
COMMENT ON TABLE sales.drawer_events IS 'أحداث الدرج: أزمنة الطلب/الاستجابة والنتيجة — حالة UNKNOWN تُسجَّل بلا استجابة مفترضة (صدق الحالة §0.1 ق11)';

-- ═══════════════ خطوط حماية المستندات المالية ═══════════════

-- (أ) الفاتورة المرحلة مجمدة: لا تعديل للحقول المالية ولا حذف (INV-SAL-02)
CREATE OR REPLACE FUNCTION sales.tf_invoice_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'INVOICE_IMMUTABLE: الفاتورة المرحلة لا تُحذف — الإلغاء بمستند VOID بسبب موثق (§0.1 ق6)'
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;
    -- UPDATE: قبل الترحيل حرية كاملة؛ بعده لا يتغير إلا أثر المرتجع أو الإلغاء الموثق بسبب
    IF OLD.status = 'DRAFT' AND NEW.status IN ('POSTED','VOID') THEN
        RETURN NEW;  -- الترحيل أو إلغاء المسودة مسموحان (التحقق في مشغل الترحيل)
    END IF;
    IF OLD.status = 'POSTED' AND NEW.status = 'VOID'
       AND length(trim(coalesce(NEW.void_reason,''))) >= 3
       AND NEW.gross_total = OLD.gross_total AND NEW.net_total = OLD.net_total THEN
        RETURN NEW;  -- إلغاء موثق لفاتورة مرحلة: بسبب إلزامي والأرقام لا تتغير (§0.1 ق6)
    END IF;
    IF OLD.status = 'POSTED' AND NEW.status IN ('PARTIAL_RETURN','FULL_RETURN')
       AND NEW.gross_total = OLD.gross_total AND NEW.net_total = OLD.net_total
       AND NEW.tax_total = OLD.tax_total AND NEW.invoice_discount = OLD.invoice_discount
       AND NEW.line_discount_total = OLD.line_discount_total THEN
        RETURN NEW;  -- تحديث حالة المرتجع فقط
    END IF;
    IF OLD.status <> 'DRAFT' THEN
        RAISE EXCEPTION 'INVOICE_IMMUTABLE: الفاتورة (%) في حالة (%) — الحقول المالية مجمّدة؛ التصحيح بمرتجع/إلغاء موثق', OLD.invoice_number, OLD.status
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_invoice_immutable
    BEFORE UPDATE OR DELETE ON sales.invoices
    FOR EACH ROW EXECUTE FUNCTION sales.tf_invoice_immutable();

-- (ب) تحقق الترحيل: سطور موجودة + مجاميع متسقة + لقطات تكلفة كاملة + مدفوع مطابق (INV-SAL-01)
CREATE OR REPLACE FUNCTION sales.tf_invoice_post_validate()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_lines INTEGER; v_sum_net NUMERIC(19,4); v_missing_cost INTEGER;
    v_missing_weight INTEGER; v_paid NUMERIC(19,4);
BEGIN
    IF NEW.status = 'POSTED' AND OLD.status = 'DRAFT' THEN
        SELECT COUNT(*), COALESCE(SUM(net_amount),0) INTO v_lines, v_sum_net
          FROM sales.invoice_lines WHERE invoice_id = NEW.invoice_id;
        IF v_lines = 0 THEN
            RAISE EXCEPTION 'INVOICE_POST_NO_LINES: لا تُرحّل فاتورة بلا سطور';
        END IF;
        -- الاتساق: net_total = Σ net_amount (بعد توزيع خصم الفاتورة) بتفاوت تقريب ≤ 0.005
        IF ABS(NEW.net_total + NEW.tax_total - (v_sum_net + NEW.tax_total)) > 0.005
           OR ABS(NEW.net_total - v_sum_net) > 0.005 THEN
            RAISE EXCEPTION 'INVOICE_TOTALS_MISMATCH: net_total (%) لا يطابق مجموع السطور (%)', NEW.net_total, v_sum_net;
        END IF;
        SELECT COUNT(*) INTO v_missing_cost FROM sales.invoice_lines
         WHERE invoice_id = NEW.invoice_id AND (cost_snapshot IS NULL OR cost_method_snapshot IS NULL);
        IF v_missing_cost > 0 THEN
            RAISE EXCEPTION 'INVOICE_POST_MISSING_COST: % سطر بلا لقطة تكلفة — لا ترحيل بلا تكلفة موثقة (§15.3)', v_missing_cost;
        END IF;
        SELECT COUNT(*) INTO v_missing_weight FROM sales.invoice_lines
         WHERE invoice_id = NEW.invoice_id AND sale_mode = 'WEIGHT' AND (weight_kg IS NULL OR weight_source IS NULL OR weight_kg <= 0);
        IF v_missing_weight > 0 THEN
            RAISE EXCEPTION 'INVOICE_POST_INVALID_WEIGHT: % سطر وزني بلا وزن/مصدر صالح (§5.1)', v_missing_weight;
        END IF;
        SELECT COALESCE(SUM(amount),0) INTO v_paid FROM sales.payments
         WHERE invoice_id = NEW.invoice_id AND status IN ('CAPTURED','SETTLED');
        IF NEW.paid_total <> v_paid THEN
            RAISE EXCEPTION 'INVOICE_PAID_MISMATCH: paid_total (%) لا يطابق المدفوع المرحل (%)', NEW.paid_total, v_paid;
        END IF;
        NEW.posted_at := now();
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_invoice_post_validate
    BEFORE UPDATE ON sales.invoices
    FOR EACH ROW EXECUTE FUNCTION sales.tf_invoice_post_validate();

-- (ج) سطر المرتجع لا يتجاوز الكمية المباعة في السطر الأصلي (INV-SAL-03)
CREATE OR REPLACE FUNCTION sales.tf_refund_line_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_sold NUMERIC(18,6); v_already NUMERIC(18,6);
BEGIN
    SELECT qty INTO v_sold FROM sales.invoice_lines WHERE line_id = NEW.original_line_id;
    SELECT COALESCE(SUM(rl.qty),0) INTO v_already
      FROM sales.refund_lines rl JOIN sales.refunds r ON r.refund_id = rl.refund_id
     WHERE rl.original_line_id = NEW.original_line_id AND r.status = 'POSTED' AND rl.line_id <> NEW.line_id;
    IF NEW.qty + v_already > v_sold THEN
        RAISE EXCEPTION 'REFUND_EXCEEDS_SOLD: المرتجع (%) + المرتجع سابقاً (%) يتجاوز المباع (%)', NEW.qty, v_already, v_sold
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_refund_line_guard
    BEFORE INSERT OR UPDATE ON sales.refund_lines
    FOR EACH ROW EXECUTE FUNCTION sales.tf_refund_line_guard();

COMMIT;
