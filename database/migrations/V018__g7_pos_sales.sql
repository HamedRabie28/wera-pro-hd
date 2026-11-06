-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V018 · G7: نقطة البيع POS — المبيعات والوزن والدفع والمرتجعات
-- المرجع: GFM-CSharp 2.4.1 §22 (بوابة G7) و§9 (POS كاملاً) و§5 (الطرق الست للوزن)
-- و§15.4/§15.5 (قواعد الأموال والسيناريوهات المرجعية) و§11.3 (التكرار) و§29.4/§29.9 (توجيه الطباعة)
-- قواعد حاكمة: جداول sales كاملة منذ V005 (لم تُمَس — إضافة أمامية فقط Forward Fix)
-- والصلاحيات pos.use/sales.* مزروعة منذ V009 بإسناداتها — هذا الترحيل يضيف فقط:
-- (1) Idempotency للمرتجعات (منع تكرار رد الأموال — §15.4) · (2) تسلسل أرقام المستندات
-- · (3) تسجيل قالبَي إيصال العميل المعتمدين من G2 في سجل القوالب (الرندر الفعلي G10/B-07)
-- · (4) تحقق داخلي من مصفوفة صلاحيات V009 كما هي — لا اختراع صلاحيات جديدة
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) Idempotency للمرتجعات: إعادة إرسال أمر المرتجع بعد timeout لا ترد المال مرتين ──
ALTER TABLE sales.refunds ADD COLUMN idempotency_key TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS uq_refunds_idempotency
    ON sales.refunds (idempotency_key) WHERE idempotency_key IS NOT NULL;

COMMENT ON COLUMN sales.refunds.idempotency_key IS
    'مفتاح Idempotency لأمر المرتجع — إعادة الإرسال تعيد المستند الأصلي ولا تنشئ ردّاً مزدوجاً (§15.4/§11.3)';

-- ── 1-ب) ملاحظة السلة المعلقة (§9.5: قائمة المعلقات بالرقم والوقت والكاشير والملاحظة) ──
ALTER TABLE sales.held_carts ADD COLUMN note_ar TEXT;

COMMENT ON COLUMN sales.held_carts.note_ar IS
    'ملاحظة التعليق كما عرضت في قائمة المعلقات (§9.5) — إضافة أمامية على V005';

-- ── 1-ج) استكمال آلة حالة الفاتورة: مرتجع إضافي على فاتورة عليها مرتجع سابق ──
-- مشغل V005 الأصلي يسمح POSTED→PARTIAL_RETURN/FULL_RETURN فقط، فتنحبس الفاتورة في
-- PARTIAL_RETURN عند مرتجع ثانٍ (جزئي بعد جزئي ثم كامل — §9.8). هذا الاستبدال
-- يضيف الفرع الناقص فقط وكل سلوك V005 الآخر كما هو حرفياً (الأرقام المالية مجمّدة).
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
    IF OLD.status = 'DRAFT' AND NEW.status IN ('POSTED','VOID') THEN
        RETURN NEW;
    END IF;
    IF OLD.status = 'POSTED' AND NEW.status = 'VOID'
       AND length(trim(coalesce(NEW.void_reason,''))) >= 3
       AND NEW.gross_total = OLD.gross_total AND NEW.net_total = OLD.net_total THEN
        RETURN NEW;
    END IF;
    IF OLD.status = 'POSTED' AND NEW.status IN ('PARTIAL_RETURN','FULL_RETURN')
       AND NEW.gross_total = OLD.gross_total AND NEW.net_total = OLD.net_total
       AND NEW.tax_total = OLD.tax_total AND NEW.invoice_discount = OLD.invoice_discount
       AND NEW.line_discount_total = OLD.line_discount_total THEN
        RETURN NEW;
    END IF;
    -- إضافة V018: مرتجع لاحق على فاتورة عليها مرتجع سابق (نفس تجميد الأرقام المالية)
    IF OLD.status = 'PARTIAL_RETURN' AND NEW.status IN ('PARTIAL_RETURN','FULL_RETURN')
       AND NEW.gross_total = OLD.gross_total AND NEW.net_total = OLD.net_total
       AND NEW.tax_total = OLD.tax_total AND NEW.invoice_discount = OLD.invoice_discount
       AND NEW.line_discount_total = OLD.line_discount_total THEN
        RETURN NEW;
    END IF;
    IF OLD.status <> 'DRAFT' THEN
        RAISE EXCEPTION 'INVOICE_IMMUTABLE: الفاتورة (%) في حالة (%) — الحقول المالية مجمّدة؛ التصحيح بمرتجع/إلغاء موثق', OLD.invoice_number, OLD.status
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

-- ── 2) تسلسل أرقام المستندات (نمط purch.g6_doc_seq من V017) ───────────────────
CREATE SEQUENCE IF NOT EXISTS sales.invoice_number_seq;
CREATE SEQUENCE IF NOT EXISTS sales.refund_number_seq;

COMMENT ON SEQUENCE sales.invoice_number_seq IS
    'أرقام فواتير البيع: SI-<seq> — التفرد مكفول بـ UNIQUE(branch_id, invoice_number) في V005';
COMMENT ON SEQUENCE sales.refund_number_seq IS
    'أرقام مستندات المرتجع: SR-<seq> — التفرد مكفون بـ UNIQUE(refund_number) في V005';

-- ── 3) سجل قالبَي إيصال العميل (G2 المعتمد — §29.3/§33) ───────────────────────
-- البنية الهيكلية للقالبين المعتمدين بصرياً في G2 (TPL_CUSTOMER_INVOICE_ROLL80/58)
-- بنسختها 1 — الرندر الفعلي والوسيط الفيزيائي في G10 (مانع B-07 المعلن);
-- مهمة الطباعة (print.print_jobs) تحفظ template_id + version لقطةً فلا يتغير
-- مستند مرحل بتغيير القالب لاحقاً (V007).
INSERT INTO print.document_templates (template_code, document_type, media_class, version, body_definition) VALUES
    ('ROLL_MEDIUM_80', 'CUSTOMER_RECEIPT', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §33 · POS_VISUAL_SPEC §4 — قالب G2 المعتمد TPL_CUSTOMER_INVOICE_ROLL80",
      "preview_asset": "docs/design/templates/TPL_CUSTOMER_INVOICE_ROLL80.png",
      "printable_width_mm": 72.0,
      "direction": "rtl",
      "blocks": [
        {"kind": "header", "fields": ["store_name", "branch_name", "station_code", "vat_number"]},
        {"kind": "invoice_meta", "fields": ["invoice_number", "posted_at_local", "shift_number", "cashier_name", "customer_name_or_cash"]},
        {"kind": "lines", "columns": ["line_no", "item_name", "sale_format", "qty_or_weight_with_source", "unit_price", "line_discount", "line_total"], "weight_source_column": true},
        {"kind": "totals", "fields": ["subtotal_gross", "line_discounts", "invoice_discount", "tax_included_extracted", "net_due", "paid_total", "change_due"], "emphasis": "net_due"},
        {"kind": "payments", "fields": ["method_ar", "amount", "provider_reference_masked"]},
        {"kind": "footer", "fields": ["thank_you", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('ROLL_SMALL_58', 'CUSTOMER_RECEIPT', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §33 · POS_VISUAL_SPEC §4 — قالب G2 المعتمد TPL_CUSTOMER_INVOICE_ROLL58 (تخطيط مستقل لا نسخة مكبرة من 80 — §29.6 ق3)",
      "preview_asset": "docs/design/templates/TPL_CUSTOMER_INVOICE_ROLL58.png",
      "printable_width_mm": 48.0,
      "direction": "rtl",
      "blocks": [
        {"kind": "header", "fields": ["store_name", "branch_name", "station_code"]},
        {"kind": "invoice_meta", "fields": ["invoice_number", "posted_at_local", "cashier_name", "customer_name_or_cash"]},
        {"kind": "lines", "columns": ["line_no", "item_name", "qty_or_weight_with_source", "line_total"], "weight_source_column": true, "multi_line_item_blocks": true},
        {"kind": "totals", "fields": ["subtotal_gross", "all_discounts", "tax_included_extracted", "net_due", "paid_total", "change_due"], "emphasis": "net_due"},
        {"kind": "payments", "fields": ["method_ar", "amount"]},
        {"kind": "footer", "fields": ["thank_you", "reprint_badge_optional"]}
      ]
    }$json$::jsonb)
ON CONFLICT (template_code, document_type, media_class, version) DO NOTHING;

-- ── 4) تحقق داخلي إلزامي (نمط V017) — لا نجاح صامت ──────────────────────────
DO $$
DECLARE
    v_expected_grants CONSTANT text[] := ARRAY[
        -- (permission_code|role_code) — مصفوفة V009 كما هي: لا إضافة ولا حذف
        'pos.use|OWNER', 'pos.use|MANAGER', 'pos.use|SUPERVISOR', 'pos.use|CASHIER',
        'sales.held_cart.use|OWNER', 'sales.held_cart.use|MANAGER', 'sales.held_cart.use|SUPERVISOR', 'sales.held_cart.use|CASHIER',
        'sales.invoice.void|OWNER', 'sales.invoice.void|MANAGER',
        'sales.discount.upto_5|OWNER', 'sales.discount.upto_5|MANAGER', 'sales.discount.upto_5|CASHIER',
        'sales.discount.upto_15|OWNER', 'sales.discount.upto_15|SUPERVISOR',
        'sales.discount.over_15|OWNER',
        'sales.price.override|OWNER',
        'sales.return.approve|OWNER', 'sales.return.approve|MANAGER', 'sales.return.approve|SUPERVISOR',
        'sales.refund.cash_for_card|OWNER',
        'sales.credit.sell|OWNER',
        'sales.drawer.open|OWNER', 'sales.drawer.open|MANAGER', 'sales.drawer.open|SUPERVISOR', 'sales.drawer.open|CASHIER',
        'printing.reprint|OWNER', 'printing.reprint|MANAGER', 'printing.reprint|SUPERVISOR', 'printing.reprint|CASHIER'
    ];
    v_missing integer := 0;
    v_extra_cashier integer := 0;
    v_tpl80 integer;
    v_tpl58 integer;
    v_idem_index integer;
    v_refund_col integer;
BEGIN
    -- (أ) كل إسناد متوقع موجود فعلاً
    SELECT COUNT(*) INTO v_missing
      FROM (SELECT unnest(v_expected_grants) AS g) x
     WHERE NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.roles r ON r.role_id = rp.role_id
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE r.code = split_part(g, '|', 2) AND p.code = split_part(g, '|', 1));
    IF v_missing > 0 THEN
        RAISE EXCEPTION 'V018 check failed: % إسناداً متوقعاً من V009 مفقود (pos.use/sales.*)', v_missing;
    END IF;

    -- (ب) الكاشير لا يملك صلاحيات تجاوز (حد 5% فقط — مصفوفة V009)
    SELECT COUNT(*) INTO v_extra_cashier
      FROM core.role_permissions rp
      JOIN core.roles r ON r.role_id = rp.role_id
      JOIN core.permissions p ON p.permission_id = rp.permission_id
     WHERE r.code = 'CASHIER'
       AND p.code IN ('sales.discount.upto_15','sales.discount.over_15','sales.price.override',
                      'sales.credit.sell','sales.return.approve','sales.invoice.void','sales.refund.cash_for_card');
    IF v_extra_cashier > 0 THEN
        RAISE EXCEPTION 'V018 check failed: الكاشير يملك صلاحيات تجاوز لم تُسند له في V009 (%)', v_extra_cashier;
    END IF;

    -- (ج) قالبا إيصال العميل مسجلان
    SELECT COUNT(*) INTO v_tpl80 FROM print.document_templates
     WHERE template_code = 'ROLL_MEDIUM_80' AND document_type = 'CUSTOMER_RECEIPT' AND media_class = 'ROLL_80' AND version = 1 AND is_active;
    SELECT COUNT(*) INTO v_tpl58 FROM print.document_templates
     WHERE template_code = 'ROLL_SMALL_58' AND document_type = 'CUSTOMER_RECEIPT' AND media_class = 'ROLL_58' AND version = 1 AND is_active;
    IF v_tpl80 <> 1 OR v_tpl58 <> 1 THEN
        RAISE EXCEPTION 'V018 check failed: قالبا إيصال العميل (80/58) غير مسجلين كما يجب (80=%, 58=%)', v_tpl80, v_tpl58;
    END IF;

    -- (د) ملاحظة السلال المعلقة (V018) موجودة
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'sales' AND table_name = 'held_carts' AND column_name = 'note_ar') THEN
        RAISE EXCEPTION 'V018 check failed: عمود ملاحظة السلة المعلقة (note_ar) غير منشأ';
    END IF;

    -- (هـ) فهرس Idempotency للمرتجعات قائم والعمود موجود
    SELECT COUNT(*) INTO v_refund_col FROM information_schema.columns
     WHERE table_schema = 'sales' AND table_name = 'refunds' AND column_name = 'idempotency_key';
    SELECT COUNT(*) INTO v_idem_index FROM pg_indexes
     WHERE schemaname = 'sales' AND tablename = 'refunds' AND indexname = 'uq_refunds_idempotency';
    IF v_refund_col <> 1 OR v_idem_index <> 1 THEN
        RAISE EXCEPTION 'V018 check failed: عمود/فهرس Idempotency للمرتجعات غير منشأ (col=%, idx=%)', v_refund_col, v_idem_index;
    END IF;
END $$;

COMMIT;
