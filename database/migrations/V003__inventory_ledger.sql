-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V003 · طبقة inv: دفتر حركة المخزون والأرصدة والحجز
-- والجرد والتسويات والتحويلات والدفعات والسيريال
-- المرجع: GFM-CSharp 2.4.1 §12 (المخزون) · §4.3 (أثر مخزون واحد) · D-03 (سياسة السالب)
-- القاعدة الذهبية: الرصيد نتيجة لحركات موثقة — لا رقم حر يُعدل مباشرة (§12.1)
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) المخازن والفروع (§12.2 ق1) ───────────────────────────────────────────
CREATE TABLE inv.warehouses (
    warehouse_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    branch_id       BIGINT NOT NULL REFERENCES core.branches(branch_id),
    code            TEXT NOT NULL UNIQUE,
    name_ar         TEXT NOT NULL,
    wh_type         TEXT NOT NULL DEFAULT 'BRANCH' CHECK (wh_type IN ('MAIN','BRANCH','SUB','EXTERNAL')),
    is_active       BOOLEAN NOT NULL DEFAULT TRUE
);
COMMENT ON TABLE inv.warehouses IS 'المخازن: رئيسي/فرعي/خارجي — كل حركة مخزون مرتبطة بمخزن محدد';

-- ── 2) دفتر حركة المخزون (Append-only — §12.1) ──────────────────────────────
CREATE TABLE inv.stock_movements (
    movement_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    movement_type   TEXT NOT NULL CHECK (movement_type IN (
                        'OPENING','RECEIVE','SALE','SALE_RETURN','SUPPLIER_RETURN',
                        'TRANSFER_OUT','TRANSFER_IN','ADJUSTMENT_IN','ADJUSTMENT_OUT',
                        'DAMAGE','EXPIRY')),
    direction       SMALLINT NOT NULL CHECK (direction IN (1,-1)),      -- 1 دخول / -1 خروج
    base_qty        NUMERIC(18,6) NOT NULL CHECK (base_qty > 0),        -- بالوحدة الأساسية (موجبة دائماً)
    source_uom_id   BIGINT NOT NULL REFERENCES catalog.units(uom_id),   -- وحدة المستند الأصلي
    conversion_factor NUMERIC(18,6) NOT NULL,                           -- لقطة عامل التحويل وقت الحركة
    source_doc_type TEXT NOT NULL,                    -- sales.invoice / purch.receipt / inv.transfer / inv.adjustment / inv.count
    source_doc_id   BIGINT NOT NULL,
    source_line_ref TEXT,                             -- مرجع السطر داخل المستند إن وجد
    lot_id          BIGINT,                             -- يُربط بـ inv.lots أدناه (FK يُضاف بعد إنشاء الجدول)
    approval_id     BIGINT REFERENCES core.approvals(approval_id),      -- إلزامي عند السالب بموافقة مشرف (D-03)
    reason          TEXT,                             -- سبب الحركة (إلزامي للتسويات والهالك)
    branch_id       BIGINT NOT NULL REFERENCES core.branches(branch_id),
    user_id         BIGINT NOT NULL REFERENCES core.users(user_id),
    occurred_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT mov_direction_type CHECK (  -- الاتجاه مشتق من النوع ولا يُخالفه
        (direction = 1  AND movement_type IN ('OPENING','RECEIVE','SALE_RETURN','TRANSFER_IN','ADJUSTMENT_IN'))
     OR (direction = -1 AND movement_type IN ('SALE','SUPPLIER_RETURN','TRANSFER_OUT','ADJUSTMENT_OUT','DAMAGE','EXPIRY'))),
    CONSTRAINT mov_adjustment_reason CHECK (movement_type NOT IN ('ADJUSTMENT_IN','ADJUSTMENT_OUT','DAMAGE','EXPIRY') OR length(trim(coalesce(reason,''))) >= 3)
);
COMMENT ON TABLE inv.stock_movements IS 'دفتر حركة المخزون: كل صف حقيقة موثقة بمصدرها (مستند مرجعي) — الإدخال فقط؛ التعديل والحذف ممنوعان بالمشغّل أدناه';
COMMENT ON COLUMN inv.stock_movements.base_qty IS 'الكمية بالوحدة الأساسية موجبة دائماً والاتجاه في direction — بيع علبة 12 كيساً = صف واحد بكمية 12 (أثر واحد §4.3 ق5)';
COMMENT ON COLUMN inv.stock_movements.conversion_factor IS 'لقطة عامل التحويل وقت الحركة — تعديل تعريف العبوة لاحقاً لا يعيد حساب التاريخ (§4.3 ق9)';

CREATE INDEX idx_movements_sku_wh_time ON inv.stock_movements (sku_id, warehouse_id, occurred_at DESC);
CREATE INDEX idx_movements_source ON inv.stock_movements (source_doc_type, source_doc_id);
CREATE INDEX idx_movements_lot ON inv.stock_movements (lot_id) WHERE lot_id IS NOT NULL;

-- ── 3) أرصدة المخزون (محفوظة ومُصالَحة مع الدفتر — §4.1 «المخزن مؤقتاً») ──────
CREATE TABLE inv.stock_balances (
    balance_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    base_qty        NUMERIC(18,6) NOT NULL DEFAULT 0,      -- قد تكون سالبة إذا سمحت السياسة
    reserved_qty    NUMERIC(18,6) NOT NULL DEFAULT 0 CHECK (reserved_qty >= 0),
    last_movement_at TIMESTAMPTZ,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (sku_id, warehouse_id)
);
COMMENT ON TABLE inv.stock_balances IS 'الرصيد المخزن للاستعلام السريع — مصدر الحقيقة هو الدفتر، والمطابقة بينهما تُختبر (T-INV-002)';
COMMENT ON COLUMN inv.stock_balances.reserved_qty IS 'المحجوز لطلبات/سلال مفتوحة — المتاح = base_qty - reserved_qty';

CREATE INDEX idx_balances_sku ON inv.stock_balances (sku_id);
CREATE INDEX idx_balances_wh  ON inv.stock_balances (warehouse_id);

-- ── 4) الدفعات/التشغيلات (تتبع الصلاحية — §4.1 عند الحاجة فقط) ───────────────
CREATE TABLE inv.lots (
    lot_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id      BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    lot_code    TEXT NOT NULL,
    expiry_date DATE,
    received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (sku_id, lot_code),
    CONSTRAINT lot_expiry_sane CHECK (expiry_date IS NULL OR expiry_date > DATE '2000-01-01')
);
COMMENT ON TABLE inv.lots IS 'الدفعات: تُستخدم فقط للأصناف المفعّل لديها track_lots — الدفعات الأقدم تباع أولًا عند اعتماد FIFO لاحقاً (D-02)';

-- ربط lot_id في الدفتر بالدفعات بعد إنشاء inv.lots
ALTER TABLE inv.stock_movements
    ADD CONSTRAINT stock_movements_lot_fk FOREIGN KEY (lot_id) REFERENCES inv.lots(lot_id);

-- ── 5) السيريال (صيغة SERIALIZED — §4.2) ────────────────────────────────────
CREATE TABLE inv.serials (
    serial_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    serial_no       TEXT NOT NULL,
    status          TEXT NOT NULL DEFAULT 'IN_STOCK' CHECK (status IN ('IN_STOCK','SOLD','RETURNED','SCRAPPED','LOST')),
    current_warehouse_id BIGINT REFERENCES inv.warehouses(warehouse_id),
    last_movement_id BIGINT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (sku_id, serial_no)
);
COMMENT ON TABLE inv.serials IS 'أرقام تسلسلية للأصناف SERIALIZED — كل وحدة سجل مستقل بحالة دورة حياتها';

-- ── 6) حجز المخزون (المتاح = الرصيد - المحجوز) ───────────────────────────────
CREATE TABLE inv.stock_reservations (
    reservation_id  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    qty_base        NUMERIC(18,6) NOT NULL CHECK (qty_base > 0),
    source_doc_type TEXT NOT NULL,                    -- sales.held_cart / purchasing.po
    source_doc_id   BIGINT NOT NULL,
    reserved_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    released_at     TIMESTAMPTZ
);
COMMENT ON TABLE inv.stock_reservations IS 'حجز كميات لمستندات مفتوحة — يُدفع المحجوز عند الترحيل أو يُحرَّر عند الإلغاء';

CREATE INDEX idx_reservations_doc ON inv.stock_reservations (source_doc_type, source_doc_id) WHERE released_at IS NULL;

-- ── 7) الجرد الفعلي والتسويات (§12.2 ق9) ────────────────────────────────────
CREATE TABLE inv.count_sessions (
    count_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    status          TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','COUNTED','RECONCILED','APPROVED','CANCELLED')),
    counted_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    approved_by     BIGINT REFERENCES core.users(user_id),
    opened_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at       TIMESTAMPTZ,
    notes           TEXT,
    CONSTRAINT count_approval_distinct CHECK (approved_by IS NULL OR approved_by <> counted_by)
);
COMMENT ON TABLE inv.count_sessions IS 'جرد فعلي بمقارنة الرصيد الفعلي والمتوقع — الاعتماد من غير الجارّ (فصل واجبات)';

CREATE TABLE inv.count_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    count_id        BIGINT NOT NULL REFERENCES inv.count_sessions(count_id) ON DELETE CASCADE,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    system_qty      NUMERIC(18,6) NOT NULL,           -- الرصيد الدفتري وقت الجرد
    counted_qty     NUMERIC(18,6) NOT NULL,           -- الرصيد الفعلي المعدود
    variance_qty    NUMERIC(18,6) GENERATED ALWAYS AS (counted_qty - system_qty) STORED,
    UNIQUE (count_id, sku_id)
);
COMMENT ON TABLE inv.count_lines IS 'أسطر الجرد: الفرق محسوب عمودياً — لا يُدخل يدوياً';

CREATE TABLE inv.stock_adjustments (
    adjustment_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    count_id        BIGINT REFERENCES inv.count_sessions(count_id),   -- NULL = تسوية سريعة محمية من POS
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    new_qty         NUMERIC(18,6) NOT NULL,           -- الرصيد الصحيح المعتمد
    reason          TEXT NOT NULL CHECK (length(trim(reason)) >= 3),
    approval_id     BIGINT REFERENCES core.approvals(approval_id),    -- اعتماد إلزامي للتسوية
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT adjust_approval_required CHECK (approval_id IS NOT NULL)
);
COMMENT ON TABLE inv.stock_adjustments IS 'التسويات: اعتماد ثنائي إلزامي وسبب مكتوب — الترحيل ينشئ حركتي ADJUSTMENT في الدفتر ويطابق الرصيد';

-- ── 8) التحويلات بين المخازن (إرسال/استلام بحالتين مستقلتين — §12.2 ق7) ─────
CREATE TABLE inv.transfer_orders (
    transfer_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    from_warehouse_id BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    to_warehouse_id   BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','SENT','RECEIVED','CANCELLED')),
    sent_at         TIMESTAMPTZ,
    received_at     TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT transfer_wh_distinct CHECK (from_warehouse_id <> to_warehouse_id)
);
COMMENT ON TABLE inv.transfer_orders IS 'التحويل بين مخزنين بمرحلتين منفصلتين: خروج SENT ثم دخول RECEIVED — كل مرحلة حركة مستقلة في الدفتر';

CREATE TABLE inv.transfer_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    transfer_id     BIGINT NOT NULL REFERENCES inv.transfer_orders(transfer_id) ON DELETE CASCADE,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    qty_base        NUMERIC(18,6) NOT NULL CHECK (qty_base > 0),
    UNIQUE (transfer_id, sku_id)
);

-- ═══════════════ المشغّلات: خطوط حماية قاعدة البيانات (§0.1 ق4) ═══════════════

-- (أ) الدفتر للقراءة فقط بعد الإدخال — لا تعديل ولا حذف (INV-INV-01)
CREATE OR REPLACE FUNCTION inv.tf_stock_movement_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'STOCK_LEDGER_IMMUTABLE: حركات المخزون لا تُعدل ولا تُحذف — التصحيح بحركة معاكسة مرتبطة بالمصدر (§0.1 ق6)'
        USING ERRCODE = 'check_violation';
END $$;

CREATE TRIGGER trg_stock_movement_immutable
    BEFORE UPDATE OR DELETE ON inv.stock_movements
    FOR EACH ROW EXECUTE FUNCTION inv.tf_stock_movement_immutable();

-- (ب) تحديث الرصيد المخزن + فرض سياسة المخزون السالب القابلة للتهيئة (D-03)
CREATE OR REPLACE FUNCTION inv.fn_negative_stock_policy()
RETURNS TEXT LANGUAGE sql STABLE AS $$
    SELECT value_text::TEXT FROM core.business_settings
    WHERE setting_key = 'inventory.negative_stock_policy' AND scope_type = 'BUSINESS'
    LIMIT 1
$$;

CREATE OR REPLACE FUNCTION inv.tf_stock_balance_apply()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_policy   TEXT;
    v_new_qty  NUMERIC(18,6);
    v_approved BOOLEAN;
BEGIN
    -- upsert الرصيد المخزن
    INSERT INTO inv.stock_balances (sku_id, warehouse_id, base_qty, last_movement_at)
    VALUES (NEW.sku_id, NEW.warehouse_id, NEW.direction * NEW.base_qty, NEW.occurred_at)
    ON CONFLICT (sku_id, warehouse_id)
    DO UPDATE SET base_qty = inv.stock_balances.base_qty + EXCLUDED.base_qty,
                  last_movement_at = EXCLUDED.last_movement_at,
                  updated_at = now()
    RETURNING base_qty INTO v_new_qty;

    -- سياسة السالب: BLOCK / SUPERVISOR_APPROVAL / ALLOW (إعداد نشاط — لا ثابت كود)
    IF v_new_qty < 0 THEN
        v_policy := inv.fn_negative_stock_policy();
        IF v_policy = 'BLOCK' THEN
            RAISE EXCEPTION 'NEGATIVE_STOCK_BLOCKED: السياسة تمنع الرصيد السالب (D-03=BLOCK) — SKU % في المخزن %', NEW.sku_id, NEW.warehouse_id
                USING ERRCODE = 'check_violation';
        ELSIF v_policy = 'SUPERVISOR_APPROVAL' THEN
            SELECT (decision = 'APPROVED') INTO v_approved
              FROM core.approvals WHERE approval_id = NEW.approval_id;
            IF v_approved IS DISTINCT FROM TRUE THEN
                RAISE EXCEPTION 'NEGATIVE_STOCK_NEEDS_APPROVAL: الرصيد السالب يتطلب اعتماد مشرف معتمداً (D-03=SUPERVISOR_APPROVAL) — movement_id=%', NEW.movement_id
                    USING ERRCODE = 'check_violation';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_stock_balance_apply
    AFTER INSERT ON inv.stock_movements
    FOR EACH ROW EXECUTE FUNCTION inv.tf_stock_balance_apply();

COMMIT;
