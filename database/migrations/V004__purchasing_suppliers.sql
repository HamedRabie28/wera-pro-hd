-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V004 · طبقة purch: الموردون وأوامر الشراء والاستلامات
-- وفواتير الموردين والسدادات والمرتجعات
-- المرجع: GFM-CSharp 2.4.1 §12.3 (قواعد الاستلام والشراء) · §13 (الذمم)
-- قاعدة حاكمة: فاتورة المورد وحدها لا تزيد المخزون — الزيادة في حدث الاستلام المعتمد
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) الموردون (§13: منع حذف طرف له مستندات — التعطيل بدل الحذف) ───────────
CREATE TABLE purch.suppliers (
    supplier_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            TEXT NOT NULL UNIQUE,
    name_ar         TEXT NOT NULL,
    name_en         TEXT,
    phone           TEXT,
    email           TEXT,
    tax_id          TEXT,                              -- الرقم الضريبي إن وجد
    payment_terms_days INTEGER NOT NULL DEFAULT 0 CHECK (payment_terms_days >= 0),
    credit_limit    NUMERIC(19,4),                     -- حد الائتمان للمورد إن وُجد
    account_status  TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (account_status IN ('ACTIVE','SUSPENDED','ARCHIVED')),
    notes           TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE purch.suppliers IS 'الموردون: الأرشفة تعطيل لا حذف — الطرف له مستندات تاريخية تبقى مرجعية (§13)';

-- ── 2) أوامر الشراء (اختيارية بحسب النطاق — §12.2 ق4) ────────────────────────
CREATE TABLE purch.purchase_orders (
    po_id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    po_number       TEXT NOT NULL UNIQUE,
    supplier_id     BIGINT NOT NULL REFERENCES purch.suppliers(supplier_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','ORDERED','PARTIAL','RECEIVED','CLOSED','CANCELLED')),
    expected_date   DATE,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    approved_at     TIMESTAMPTZ
);
COMMENT ON TABLE purch.purchase_orders IS 'أمر الشراء مسودة قابلة للاستعادة — لا حركة مخزون ولا مال قبل الاستلام والسداد';

CREATE TABLE purch.purchase_order_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    po_id           BIGINT NOT NULL REFERENCES purch.purchase_orders(po_id) ON DELETE CASCADE,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    qty             NUMERIC(18,6) NOT NULL CHECK (qty > 0),   -- بوحدة الشراء
    uom_id          BIGINT NOT NULL REFERENCES catalog.units(uom_id),
    unit_cost       NUMERIC(19,4) NOT NULL CHECK (unit_cost >= 0),
    UNIQUE (po_id, sku_id)
);

-- ── 3) استلامات الموردين — الحدث الذي يزيد المخزون (§12.3 ق1) ────────────────
CREATE TABLE purch.purchase_receipts (
    receipt_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    receipt_number  TEXT NOT NULL UNIQUE,
    supplier_id     BIGINT NOT NULL REFERENCES purch.suppliers(supplier_id),
    po_id           BIGINT REFERENCES purch.purchase_orders(po_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    received_by     BIGINT NOT NULL REFERENCES core.users(user_id),
    received_at     TIMESTAMPTZ,
    posted_at       TIMESTAMPTZ,
    notes           TEXT
);
COMMENT ON TABLE purch.purchase_receipts IS 'الاستلام هو المستند الذي ينشئ حركة RECEIVE في دفتر المخزون — لا من فاتورة المورد وحدها';

CREATE TABLE purch.purchase_receipt_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    receipt_id      BIGINT NOT NULL REFERENCES purch.purchase_receipts(receipt_id) ON DELETE CASCADE,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    qty             NUMERIC(18,6) NOT NULL CHECK (qty > 0),   -- الكمية بوحدة الاستلام
    uom_id          BIGINT NOT NULL REFERENCES catalog.units(uom_id),
    conversion_factor NUMERIC(18,6) NOT NULL,                 -- لقطة التحويل للوحدة الأساسية
    unit_cost       NUMERIC(19,4) NOT NULL CHECK (unit_cost >= 0),  -- تكلفة الوحدة المستلمة
    lot_id          BIGINT REFERENCES inv.lots(lot_id),
    UNIQUE (receipt_id, sku_id)
);
COMMENT ON TABLE purch.purchase_receipt_lines IS 'سطر الاستلام يحمل لقطة عامل التحويل وتكلفة الوحدة — الاستلام يغذي طبقة التكلفة (finance.cost_layers)';

-- ── 4) فواتير الموردين (التزام مالي — لا حركة مخزون) ──────────────────────────
CREATE TABLE purch.supplier_invoices (
    invoice_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_number  TEXT NOT NULL,                     -- رقم المورد على الفاتورة
    supplier_id     BIGINT NOT NULL REFERENCES purch.suppliers(supplier_id),
    receipt_id      BIGINT REFERENCES purch.purchase_receipts(receipt_id),
    subtotal        NUMERIC(19,4) NOT NULL DEFAULT 0,
    tax_total       NUMERIC(19,4) NOT NULL DEFAULT 0,
    grand_total     NUMERIC(19,4) NOT NULL DEFAULT 0,
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','PARTIALLY_PAID','PAID','CANCELLED')),
    invoice_date    DATE NOT NULL,
    due_date        DATE,
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (supplier_id, invoice_number)
);
COMMENT ON TABLE purch.supplier_invoices IS 'فاتورة المورد التزام مالي مرتبط بالاستلام — السداد لا يُسجل مرة كسداد ومرة كمصروف (§12.3 ق3)';

CREATE TABLE purch.supplier_invoice_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_id      BIGINT NOT NULL REFERENCES purch.supplier_invoices(invoice_id) ON DELETE CASCADE,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    qty             NUMERIC(18,6) NOT NULL CHECK (qty > 0),
    uom_id          BIGINT NOT NULL REFERENCES catalog.units(uom_id),
    unit_cost       NUMERIC(19,4) NOT NULL CHECK (unit_cost >= 0),
    line_total      NUMERIC(19,4) NOT NULL CHECK (line_total >= 0)
);

-- ── 5) سدادات الموردين مع التوزيع على الفواتير (منع التخصيص الزائد — §13) ────
CREATE TABLE purch.supplier_payments (
    payment_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    voucher_number  TEXT NOT NULL UNIQUE,              -- رقم السند المالي
    supplier_id     BIGINT NOT NULL REFERENCES purch.suppliers(supplier_id),
    treasury_account_id BIGINT NOT NULL,               -- يُربط بـ finance.treasury_accounts بعد إنشائها (FK يضاف في V006)
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    payment_method  TEXT NOT NULL CHECK (payment_method IN ('CASH','BANK_TRANSFER','CHEQUE','CARD')),
    payment_date    DATE NOT NULL,
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    notes           TEXT
);
COMMENT ON TABLE purch.supplier_payments IS 'سداد المورد: أثر مالي واحد على الخزينة/البنك مهما وُزّع على فواتير — لا ازدواج سداد/مصروف';

CREATE TABLE purch.supplier_payment_allocations (
    allocation_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payment_id      BIGINT NOT NULL REFERENCES purch.supplier_payments(payment_id) ON DELETE CASCADE,
    invoice_id      BIGINT NOT NULL REFERENCES purch.supplier_invoices(invoice_id),
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    UNIQUE (payment_id, invoice_id)
);
COMMENT ON TABLE purch.supplier_payment_allocations IS 'توزيع السداد على فواتير محددة — مشغّل الحماية يمنع تخصيص أكثر من المتبقي';

-- خط حماية: مجموع التخصيصات ≤ قيمة السداد (منع التخصيص الزائد)
CREATE OR REPLACE FUNCTION purch.tf_payment_allocation_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_paid NUMERIC(19,4); v_alloc NUMERIC(19,4); v_total NUMERIC(19,4);
BEGIN
    SELECT amount INTO v_total FROM purch.supplier_payments WHERE payment_id = COALESCE(NEW.payment_id, OLD.payment_id);
    SELECT COALESCE(SUM(amount),0) INTO v_alloc FROM purch.supplier_payment_allocations
     WHERE payment_id = COALESCE(NEW.payment_id, OLD.payment_id);
    IF v_alloc > v_total THEN
        RAISE EXCEPTION 'PAYMENT_OVER_ALLOCATION: مجموع التخصيصات % يتجاوز قيمة السداد %', v_alloc, v_total
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_payment_allocation_guard
    AFTER INSERT OR UPDATE OR DELETE ON purch.supplier_payment_allocations
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION purch.tf_payment_allocation_guard();

-- ── 6) مرتجعات الموردين (مرتبطة بالمستند الأصلي — §12.3 ق4) ───────────────────
CREATE TABLE purch.supplier_returns (
    return_id       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    return_number   TEXT NOT NULL UNIQUE,
    supplier_id     BIGINT NOT NULL REFERENCES purch.suppliers(supplier_id),
    receipt_id      BIGINT REFERENCES purch.purchase_receipts(receipt_id),   -- الأصل
    invoice_id      BIGINT REFERENCES purch.supplier_invoices(invoice_id),
    warehouse_id    BIGINT NOT NULL REFERENCES inv.warehouses(warehouse_id),
    status          TEXT NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
    refund_method   TEXT CHECK (refund_method IN ('CASH','CREDIT_NOTE','BANK_TRANSFER')),
    total           NUMERIC(19,4) NOT NULL DEFAULT 0,
    posted_at       TIMESTAMPTZ,
    created_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT sup_return_has_origin CHECK (receipt_id IS NOT NULL OR invoice_id IS NOT NULL)
);
COMMENT ON TABLE purch.supplier_returns IS 'مرتجع المورد يرتبط بالاستلام/الفاتورة الأصلية — الترحيل ينشئ حركة SUPPLIER_RETURN ولا يخصم المورد مرتين';

CREATE TABLE purch.supplier_return_lines (
    line_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    return_id       BIGINT NOT NULL REFERENCES purch.supplier_returns(return_id) ON DELETE CASCADE,
    sku_id          BIGINT NOT NULL REFERENCES catalog.skus(sku_id),
    qty             NUMERIC(18,6) NOT NULL CHECK (qty > 0),
    uom_id          BIGINT NOT NULL REFERENCES catalog.units(uom_id),
    conversion_factor NUMERIC(18,6) NOT NULL,
    unit_cost       NUMERIC(19,4) NOT NULL CHECK (unit_cost >= 0),
    line_total      NUMERIC(19,4) NOT NULL DEFAULT 0,
    UNIQUE (return_id, sku_id)
);

COMMIT;
