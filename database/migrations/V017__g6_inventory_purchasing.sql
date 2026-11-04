-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V017 · G6: المخزون والمشتريات والموردون والمخازن
-- المرجع: GFM-CSharp 2.4.1 §22 (بوابة G6) و§12.1/§12.2/§12.3 (دفتر الحركة
-- والصفحات وقواعد الاستلام) و§13 (العملاء والموردون والذمم) و§18 (الصلاحيات).
--
-- الغرض (بوابة G6 — Inventory / Purchasing / Suppliers / Warehouses):
--   1) تصنيف مستندات التسوية: عمود adjustment_kind على inv.stock_adjustments
--      (STOCKTAKE/DAMAGE/EXPIRY/QUICK) — تمييز مستند الجرد المعتمد من الهالك
--      والتالف والتسوية السريعة (§12.2 ق9/ق10).
--   2) منع تكرار الاستلام/السداد (بوابة القبول حرفياً): مفاتيح Idempotency
--      اختيارية بفهارس فريدة جزئية على purch.purchase_receipts و
--      purch.supplier_invoices وpurch.supplier_payments وpurch.supplier_returns —
--      إعادة الإرسال بمفتاح واحد لا تُنشئ أثراً مزدوجاً (§11.3).
--   3) حماية قاعدة جديدة: منع تخصيص سداد على فاتورة بأكثر من قيمتها —
--      حارس tf_invoice_allocation_guard (§13: «منع تخصيص مبلغ أكبر من
--      المتبقي دون سياسة معتمدة»).
--   4) قيود صحة إضافية: new_qty للتسوية ≥ 0 (الرصيد الصحيح المعتمد لا يكون
--      سالباً)، وقيد عدم السالب على inv.stock_balances.base_qty عند سياسة
--      BLOCK/D-03 (مشغّل V003 يفرض السياسة على مستوى النشاط).
--   5) تسلسل أرقام مستندات المشتريات: purch.g6_doc_seq لمولّد أرقام
--      موحدة البادئات (PO-/RC-/SP-/SR-) — لا أرقام حرة من العميل.
--   6) صلاحيات G6 (§18): قراءة المخزون والمشتريات لكل الأدوار (قراءة تكوين
--      عمل — نمط V014/V015/V016)، وإدارة المخازن للمدير، وتشغيل الجرد
--      للثلاثة واعتماده للمالك/المدير (فصل واجبات: المعتمد ≠ الجارّ بقيد
--      V003)، والحجز، وإدارة السلة/الاستلام/المرتجع، وفواتير الموردين
--      للمحاسب، والسداد بأثر خزينة واحد، وقراءة الأطراف، وقرار الاعتمادات
--      للمشرف (D-03 ب).
-- إضافة أمامية فقط (Forward Fix) — لا مساس بأي ترحيل مطبق.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) تصنيف مستندات التسوية (§12.2 ق9/ق10) ────────────────────────────────
ALTER TABLE inv.stock_adjustments
    ADD COLUMN IF NOT EXISTS adjustment_kind TEXT NOT NULL DEFAULT 'QUICK';

DO $$
BEGIN
    -- تصنيف رجعي آمن: ما رُبط بجلسة جرد فهو تسوية جرد (الجدول يبدأ فارغاً
    -- في القواعد الجديدة؛ العبارة تصح أيضاً لأي قاعدة قائمة أعيد بناؤها).
    EXECUTE 'UPDATE inv.stock_adjustments SET adjustment_kind = ''STOCKTAKE'' WHERE count_id IS NOT NULL';
EXCEPTION WHEN undefined_table THEN
    NULL;  -- لا يمكن أن يحدث داخل ترحيل خطي؛ حماية شكلية
END $$;

ALTER TABLE inv.stock_adjustments
    DROP CONSTRAINT IF EXISTS adjust_kind_domain;
ALTER TABLE inv.stock_adjustments
    ADD CONSTRAINT adjust_kind_domain CHECK (adjustment_kind IN ('STOCKTAKE','DAMAGE','EXPIRY','QUICK'));

ALTER TABLE inv.stock_adjustments
    DROP CONSTRAINT IF EXISTS adjust_new_qty_nonneg;
ALTER TABLE inv.stock_adjustments
    ADD CONSTRAINT adjust_new_qty_nonneg CHECK (new_qty >= 0);

COMMENT ON COLUMN inv.stock_adjustments.adjustment_kind IS 'نوع المستند: جرد معتمد / هالك / تلف صلاحية / تسوية سريعة — يغذي كرت الحركة والصفحة 23 (§12.2)';
COMMENT ON COLUMN inv.stock_adjustments.new_qty IS 'الرصيد الصحيح المعتمد بعد التسوية — لا يكون سالباً (قيد adjust_new_qty_nonneg في V017)';

-- ── 2) مفاتيح Idempotency لمستندات المشتريات (بوابة القبول: لا تكرار استلام/دفع) ──
ALTER TABLE purch.purchase_receipts  ADD COLUMN IF NOT EXISTS idempotency_key TEXT;
ALTER TABLE purch.supplier_invoices  ADD COLUMN IF NOT EXISTS idempotency_key TEXT;
ALTER TABLE purch.supplier_payments  ADD COLUMN IF NOT EXISTS idempotency_key TEXT;
ALTER TABLE purch.supplier_returns   ADD COLUMN IF NOT EXISTS idempotency_key TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS uq_receipts_idempotency
    ON purch.purchase_receipts (idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_supplier_invoices_idempotency
    ON purch.supplier_invoices (idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_supplier_payments_idempotency
    ON purch.supplier_payments (idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_supplier_returns_idempotency
    ON purch.supplier_returns (idempotency_key) WHERE idempotency_key IS NOT NULL;

COMMENT ON COLUMN purch.purchase_receipts.idempotency_key IS 'مفتاح إعادة الإرسال الاختياري لعملية الترحيل — مفتاح واحد لا يُرحّل أثراً مزدوجاً (§11.3 لا تكرار استلام)';
COMMENT ON COLUMN purch.supplier_invoices.idempotency_key IS 'مفتاح ترحيل الفاتورة — لا يُنشأ التزام مالي مرتين بمفتاح واحد';
COMMENT ON COLUMN purch.supplier_payments.idempotency_key IS 'مفتاح ترحيل السداد — أثر خزينة واحد لا يتكرر (§12.3 ق3: لا يُسجل مرة كسداد ومرة كمصروف)';
COMMENT ON COLUMN purch.supplier_returns.idempotency_key IS 'مفتاح ترحيل المرتجع — حركة SUPPLIER_RETURN واحدة لأصل واحد';

-- ── 3) حارس توزيع السداد على الفواتير (§13 — منع التخصيص الزائد لكل فاتورة) ──
-- حارس V004 يحمي: Σ التخصيصات ≤ قيمة السداد. هذا الحارس الجديد يحمي الوجه
-- الآخر: Σ ما دُفع على الفاتورة الواحدة ≤ قيمة الفاتورة — فلا يُخصم على
-- فاتورة أكثر من قيمتها مهما تعددت السدادات.
CREATE OR REPLACE FUNCTION purch.tf_invoice_allocation_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_invoice  BIGINT;
    v_total    NUMERIC(19,4);
    v_alloc    NUMERIC(19,4);
    v_posted   NUMERIC(19,4);
BEGIN
    v_invoice := COALESCE(NEW.invoice_id, OLD.invoice_id);
    SELECT grand_total INTO v_total
      FROM purch.supplier_invoices WHERE invoice_id = v_invoice;
    SELECT COALESCE(SUM(a.amount), 0) INTO v_alloc
      FROM purch.supplier_payment_allocations a
      JOIN purch.supplier_payments p ON p.payment_id = a.payment_id
     WHERE a.invoice_id = v_invoice
       AND p.status <> 'CANCELLED';      -- الملغى لا يُحسب تخصيصاً فعلياً
    IF v_alloc > v_total THEN
        RAISE EXCEPTION 'INVOICE_OVER_PAYMENT: المخصص على الفاتورة % يساوي % ويتجاوز قيمتها % — لا تخصيص بأكثر من المتبقي (§13)',
            v_invoice, v_alloc, v_total
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_invoice_allocation_guard
    AFTER INSERT OR UPDATE OR DELETE ON purch.supplier_payment_allocations
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION purch.tf_invoice_allocation_guard();

-- ── 4) تسلسل أرقام مستندات المشتريات (لا أرقام حرة) ──────────────────────────
CREATE SEQUENCE IF NOT EXISTS purch.g6_doc_seq START 1001;

COMMENT ON SEQUENCE purch.g6_doc_seq IS 'مولّد أرقام موحدة لمستندات المشتريات (PO-/RC-/SP-/SR- بادئة النوع + رقم التسلسل) — مصدر حقيقة واحد';

-- ── 4-ب) أعمدة لقطات الأسطر (خط الشراء G6): الجداول فارغة قبل G6 بلا أي كاتب ──
-- لقطة الكمية القاعدية وإجمالي السطر على سطر الاستلام (تُحسب عند الإنشاء
-- من بوابة التحويل الموحدة) ورمز الدفعة وتاريخ صلاحيتها للواجهة والترحيل؛
-- وصنف الأثر (المحتوى عند PACK) على سطر المرتجع — الأثر في الدفتر عليه.
ALTER TABLE purch.purchase_receipt_lines
    ADD COLUMN IF NOT EXISTS lot_code   TEXT,
    ADD COLUMN IF NOT EXISTS lot_expiry DATE,
    ADD COLUMN IF NOT EXISTS base_qty   NUMERIC(18,6) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS line_total NUMERIC(19,4) NOT NULL DEFAULT 0;

ALTER TABLE purch.supplier_return_lines
    ADD COLUMN IF NOT EXISTS target_sku_id BIGINT NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS base_qty      NUMERIC(18,6) NOT NULL DEFAULT 0;

COMMENT ON COLUMN purch.purchase_receipt_lines.base_qty IS 'لقطة الكمية بالوحدة الأساسية عند إنشاء السطر (qty × conversion_factor) — تعديل تعريف العبوة لاحقاً لا يعيد حساب التاريخ (§4.3 ق9)';
COMMENT ON COLUMN purch.purchase_receipt_lines.lot_code IS 'رمز دفعة الاستلام (اختياري) — يُدرج في inv.lots عند الترحيل ويُربط بالحركة';
COMMENT ON COLUMN purch.supplier_return_lines.target_sku_id IS 'صنف الأثر في الدفتر: المحتوى عند PACK — حركة SUPPLIER_RETURN تقع عليه';
COMMENT ON COLUMN purch.supplier_return_lines.base_qty IS 'لقطة الكمية القاعدية للمرتجع عند الإنشاء';

-- ── 4-ج) طابع زمني لاعتماد الجرد (تقرير PAGE-20 يعتمد عليه) ─────────────────
ALTER TABLE inv.count_sessions
    ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ;
COMMENT ON COLUMN inv.count_sessions.approved_at IS 'وقت اعتماد التسوية (المعتمد ≠ الجارّ) — يوثق لحظة ترحيل حركات التسوية';

-- ── 5) صلاحيات G6 (§18) ─────────────────────────────────────────────────────
INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('inventory.read',             'inventory', 'قراءة الأرصدة وكرت الحركة والنواقص والدفعات — تكوين عمل لكل المحطات'),
    ('inventory.warehouse.manage', 'inventory', 'إدارة المخازن والفروع الفرعية والخارجية (تعريف/تعطيل — لا حذف)'),
    ('inventory.stocktake.run',    'inventory', 'فتح جلسة جرد وتسجيل العد وإغلاقها'),
    ('inventory.stocktake.approve','inventory', 'اعتماد تسوية الجرد وترحيلها — المعتمد غير الجارّ (قيد V003)'),
    ('inventory.reservation.manage','inventory','حجز المخزون وتحرير الحجز لمستندات مفتوحة (المتاح = الرصيد - المحجوز)'),
    ('purchasing.read',            'purchasing', 'قراءة أوامر الشراء والاستلامات وفواتير الموردين ومرتجعاتهم'),
    ('purchasing.manage',          'purchasing', 'سلة المشتريات والمسودات القابلة للاستعادة وأوامر الشراء'),
    ('purchasing.receive',         'purchasing', 'ترحيل استلام المورد — الحدث الذي يزيد المخزون (لا من الفاتورة وحدها)'),
    ('purchasing.invoice.manage',  'purchasing', 'فواتير الموردين: إنشاء وترحيل الالتزام المالي وإلغاء المسودة'),
    ('purchasing.payment.post',    'purchasing', 'سداد المورد بأثر خزينة واحد وتوزيعه على الفواتير'),
    ('purchasing.return.post',     'purchasing', 'مرتجع مورد مرتبط بالمستند الأصلي وترحيله'),
    ('suppliers.manage',           'parties',   'سجل الموردين: إنشاء وتعديل وتعطيل (لا حذف لطرف له مستندات)'),
    ('customers.manage',           'parties',   'سجل العملاء: إنشاء وتعديل وتعطيل (لا حذف لطرف له مستندات)'),
    ('parties.read',               'parties',   'قراءة سجلات الأطراف وكشوف الحساب'),
    ('security.approval.decide',   'security',  'البت في طلبات الاعتماد (موافقة/رفض) — قرار من غير طالب العملية (D-03 ب)'),
    ('finance.treasury.read',       'finance',   'قراءة حسابات الخزينة/البنك لاختيار وجهة السداد (إنشاؤها وإدارتها في G8)')
ON CONFLICT (code) DO NOTHING;

-- 5-أ) قراءة المخزون والمشتريات لكل الأدوار السبعة (قراءة تكوين عمل — نمط V014..V016)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'inventory.read'
ON CONFLICT DO NOTHING;
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'purchasing.read'
ON CONFLICT DO NOTHING;

-- 5-ب) إدارة المخازن: المالك والمدير (رصد PAGE-28: م)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'inventory.warehouse.manage'
 WHERE r.code IN ('OWNER', 'MANAGER')
ON CONFLICT DO NOTHING;

-- 5-ج) تشغيل الجرد: المالك والمدير وأمين المخزن (رصد PAGE-20: م، مش، خ)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'inventory.stocktake.run'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- 5-د) اعتماد تسوية الجرد: المالك والمدير فقط — المعتمد ≠ الجارّ (قيد count_approval_distinct من V003)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'inventory.stocktake.approve'
 WHERE r.code IN ('OWNER', 'MANAGER')
ON CONFLICT DO NOTHING;

-- 5-هـ) الحجز والتحرير: المالك والمدير وأمين المخزن (§12.1 الحجز والمتاح)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'inventory.reservation.manage'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- 5-و) سلة المشتريات وأوامر الشراء: المالك والمدير وأمين المخزن (رصد PAGE-24: خ، م)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'purchasing.manage'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- 5-ز) الاستلام: المالك والمدير وأمين المخزن (رصد PAGE-25: خ، مش)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'purchasing.receive'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- 5-ح) فواتير الموردين: المالك والمدير والمحاسب (رصد PAGE-26: خ، ح، م)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'purchasing.invoice.manage'
 WHERE r.code IN ('OWNER', 'MANAGER', 'ACCOUNTANT')
ON CONFLICT DO NOTHING;

-- 5-ط) السداد: المالك والمحاسب — أثر خزينة واحد لا يُسجل مصروفاً (§12.3 ق3)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'purchasing.payment.post'
 WHERE r.code IN ('OWNER', 'ACCOUNTANT')
ON CONFLICT DO NOTHING;

-- 5-ي) مرتجع المورد: المالك والمدير وأمين المخزن (رصد PAGE-27: خ، م)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'purchasing.return.post'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- 5-ك) سجل الموردين والعملاء (إدارة): المالك والمدير (رصد PAGE-31/29: م)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'suppliers.manage'
 WHERE r.code IN ('OWNER', 'MANAGER')
ON CONFLICT DO NOTHING;
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'customers.manage'
 WHERE r.code IN ('OWNER', 'MANAGER')
ON CONFLICT DO NOTHING;

-- 5-ل) قراءة الأطراف: المالك والمدير وأمين المخزن والمحاسب والمراجع (رصد PAGE-31/32: م، خ، ح)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'parties.read'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER', 'ACCOUNTANT', 'AUDITOR')
ON CONFLICT DO NOTHING;

-- 5-م) البت في الاعتمادات: المالك والمدير والمشرف (D-03 ب: بموافقة مشرف)
--     قيد V003 approvals_no_self يمنع البت من طالب العملية نفسه.
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'security.approval.decide'
 WHERE r.code IN ('OWNER', 'MANAGER', 'SUPERVISOR')
ON CONFLICT DO NOTHING;

-- 5-ن) قراءة حسابات الخزينة لاختيار وجهة السداد: المالك والمدير والمحاسب
--     (الإنشاء والإدارة الكاملة للخزينة في G8 — هنا قراءة فقط صادقة)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'finance.treasury.read'
 WHERE r.code IN ('OWNER', 'MANAGER', 'ACCOUNTANT')
ON CONFLICT DO NOTHING;

-- ── 6) تحقق داخلي ──────────────────────────────────────────────────────────
DO $$
DECLARE
    v_count INTEGER;
    v_missing TEXT;
BEGIN
    -- كل صلاحية جديدة يجب أن تكون ممنوحة لجهة واحدة على الأقل
    SELECT string_agg(p.code, ', ') INTO v_missing
      FROM core.permissions p
     WHERE p.code IN (
        'inventory.read','inventory.warehouse.manage','inventory.stocktake.run','inventory.stocktake.approve',
        'inventory.reservation.manage','purchasing.read','purchasing.manage','purchasing.receive',
        'purchasing.invoice.manage','purchasing.payment.post','purchasing.return.post',
        'suppliers.manage','customers.manage','parties.read','security.approval.decide',
        'finance.treasury.read')
       AND NOT EXISTS (SELECT 1 FROM core.role_permissions rp WHERE rp.permission_id = p.permission_id);
    IF v_missing IS NOT NULL THEN
        RAISE EXCEPTION 'V017 check failed: صلاحيات بلا إسناد: %', v_missing;
    END IF;

    -- الأدوار السبعة كلها تقرأ المخزون والمشتريات
    SELECT COUNT(DISTINCT r.role_id) INTO v_count
      FROM core.roles r
      JOIN core.role_permissions rp ON rp.role_id = r.role_id
      JOIN core.permissions p ON p.permission_id = rp.permission_id AND p.code = 'inventory.read';
    IF v_count <> 7 THEN
        RAISE EXCEPTION 'V017 check failed: inventory.read يجب أن تُمنح للأدوار السبعة (حصلنا %)', v_count;
    END IF;
END $$;

COMMIT;
