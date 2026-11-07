-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V019 · G8: الورديات والنقد والأدراج والمصروفات ورأس المال
-- المرجع: GFM-CSharp 2.4.1 §22 (بوابة G8) و§14 (الورديات والخزينة ورأس المال والمصروفات)
-- و§13 (سندات القبض/الصرف وتوزيعها على الفواتير) و§9.9 (الورديات من POS)
-- و§29.10/§31/§32 (قوالب السندات والرواتب والمصروفات 58/80 وخصوصية الرواتب §35.2)
-- قواعد حاكمة: النقد المتوقع يطابق قواعد الحركات (بوابة القبول) · فصل النقد عن البطاقة
-- والآجل · لا ترحيل مكرر · الفارق يبقى ظاهراً حتى التسوية · التحويل الداخلي لا يولد
-- إيراداً ولا مصروفاً (§14.2) · درج المحطة ≠ الخزينة ≠ البنك ≠ مزود الدفع
-- القاعدة الذهبية للبنية: إضافات أمامية فقط (Forward Fix) — جداول V005/V006 لا تُمَس
-- إلا بإضافة أعمدة nullable، وكل قيود V006 القائمة محفوظة حرفياً.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) الإغلاق التام للوردية (§14.1/§9.9 — استكمال G7 التي بنت الفتح والملخص) ──
-- (أ) رابط تسوية الفارق: الفارق يظل ظاهراً على الوردية المغلقة حتى يعالجه سند موثق
ALTER TABLE sales.shifts
    ADD COLUMN IF NOT EXISTS variance_settled_by_voucher_id BIGINT REFERENCES finance.vouchers(voucher_id);

COMMENT ON COLUMN sales.shifts.variance_settled_by_voucher_id IS
    'سند التسوية الموثق للفارق (إن وجد) — الفارق يبقى ظاهراً على الوردية حتى التسوية (بوابة قبول G8)؛ يظل NULL حتى يعالج الفارق بسند صرف/قبض مرتبط';

-- (ب) جلسات عد النقدية (SFT-002): عدّات موثقة بمن عدّ ومتى — العدة الأخيرة هي المستخدمة للإغلاق
CREATE TABLE finance.shift_cash_counts (
    count_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    shift_id        BIGINT NOT NULL REFERENCES sales.shifts(shift_id),
    counted_cash    NUMERIC(19,4) NOT NULL CHECK (counted_cash >= 0),
    counted_by      BIGINT NOT NULL REFERENCES core.users(user_id),
    counted_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    notes           TEXT
);
COMMENT ON TABLE finance.shift_cash_counts IS 'عدّات نقدية الوردية الموثقة (من عدّ ومتى وبكم) — النقد الفعلي ليس رقماً حراً (§14.1/SFT-002)';

CREATE INDEX idx_shift_cash_counts_shift ON finance.shift_cash_counts (shift_id, counted_at DESC);

-- (ج) آلة إغلاق الوردية: الإغلاق يتطلب عدّاً فعلياً وسبباً للفرق ومغلِقاً وزمناً — والوردية
--     المغلقة لا تُعدل ولا تُحذف حقولها المالية؛ يسمح فقط بربط سند تسوية الفارق لاحقاً
CREATE OR REPLACE FUNCTION sales.tf_shift_close_validate()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'SHIFT_IMMUTABLE: لا تُحذف وردية — سجل تشغيلي دائم (§14.1)'
            USING ERRCODE = 'check_violation';
    END IF;

    -- الإغلاق (OPEN→CLOSED): شروط بوابة القبول كاملة
    IF OLD.status = 'OPEN' AND NEW.status = 'CLOSED' THEN
        IF NEW.counted_cash IS NULL THEN
            RAISE EXCEPTION 'SHIFT_CLOSE_COUNT_REQUIRED: الإغلاق يتطلب عدّ النقدية الفعلية (§14.1)'
                USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.expected_cash IS NULL THEN
            RAISE EXCEPTION 'SHIFT_CLOSE_EXPECTED_REQUIRED: الإغلاق يتطلب النقدية المتوقعة المحسوبة من الدفتر (§14.1)'
                USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.closed_by IS NULL OR NEW.closed_at IS NULL THEN
            RAISE EXCEPTION 'SHIFT_CLOSE_META_REQUIRED: الإغلاق يوثّق مَن أغلق ومتى (§14.1)'
                USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.counted_cash <> NEW.expected_cash
           AND length(trim(coalesce(NEW.variance_reason,''))) < 3 THEN
            RAISE EXCEPTION 'SHIFT_VARIANCE_REASON_REQUIRED: الفارق (%) ج.م يستلزم سبباً موثقاً لا يقل عن 3 محارف — الفارق يبقى ظاهراً حتى التسوية (بوابة G8)',
                NEW.counted_cash - NEW.expected_cash
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;

    -- لا حالة أخرى مسموحة غير OPEN وCLOSED (قائمة V005)
    IF NEW.status NOT IN ('OPEN','CLOSED') THEN
        RAISE EXCEPTION 'SHIFT_STATUS_INVALID: حالات الوردية OPEN/CLOSED فقط (قائمة V005)'
            USING ERRCODE = 'check_violation';
    END IF;

    -- الوردية المفتوحة: لا تُغيَّر بياناتها الافتتاحية الموثقة (المفتاح والرصيد الافتتاحي)
    IF OLD.status = 'OPEN' AND NEW.status = 'OPEN'
       AND (NEW.opening_float <> OLD.opening_float OR NEW.opened_at <> OLD.opened_at
            OR NEW.opened_by <> OLD.opened_by OR NEW.station_id <> OLD.station_id) THEN
        RAISE EXCEPTION 'SHIFT_OPEN_FIELDS_IMMUTABLE: بيانات فتح الوردية موثقة لا تُعدل (§14.1)'
            USING ERRCODE = 'check_violation';
    END IF;

    -- الوردية المغلقة مجمّدة: لا إعادة فتح ولا تعديل مالي — الاستثناء الوحيد ربط/تحديث
    -- سند تسوية الفارق وحده (الفارق يُعالج بمستند موثق لا بتعديل الأرقام)
    IF OLD.status = 'CLOSED' THEN
        IF NEW.status <> 'CLOSED' THEN
            RAISE EXCEPTION 'SHIFT_CLOSED_IMMUTABLE: الوردية المغلقة لا تُعاد فتحاً ولا تُعدل — التصحيح بمستند موثق (§14.1)'
                USING ERRCODE = 'check_violation';
        END IF;
        IF (NEW.expected_cash, NEW.counted_cash, NEW.opening_float, NEW.variance_reason,
            NEW.closed_by, NEW.closed_at, NEW.opened_by, NEW.opened_at, NEW.station_id)
         IS NOT DISTINCT FROM
           (OLD.expected_cash, OLD.counted_cash, OLD.opening_float, OLD.variance_reason,
            OLD.closed_by, OLD.closed_at, OLD.opened_by, OLD.opened_at, OLD.station_id) THEN
            RETURN NEW;  -- لا تغيير مالي/تشغيلي (ربط سند التسوية وحده أو تحيّز لا يغير شيئاً)
        END IF;
        RAISE EXCEPTION 'SHIFT_CLOSED_IMMUTABLE: الحقول المالية للوردية المغلقة مجمّدة — التصحيح بمستند موثق لا بتعديل (§14.1)'
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER trg_shift_close_validate
    BEFORE UPDATE OR DELETE ON sales.shifts
    FOR EACH ROW EXECUTE FUNCTION sales.tf_shift_close_validate();

-- ── 2) الدرج مرتبط بالمحطة (الحقيقة الفيزيائية: درج مادي لكل محطة كاشير) ──────
ALTER TABLE finance.treasury_accounts
    ADD COLUMN IF NOT EXISTS station_id BIGINT REFERENCES core.stations(station_id);

COMMENT ON COLUMN finance.treasury_accounts.station_id IS
    'درج المحطة: الحساب النقدي الفيزيائي لكل محطة — NULL لغير الأدراج (خزينة/بنك مركزي أو فرعي)؛ التسوية الدقيقة للوردية تتطلب درجاً لكل محطة';

-- درج واحد على الأكثر لكل محطة (فهرس فريد جزئي — التوافق الخلفي مع أدراج الفرع القائمة)
CREATE UNIQUE INDEX IF NOT EXISTS uq_treasury_drawer_per_station
    ON finance.treasury_accounts (station_id) WHERE station_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_treasury_accounts_kind
    ON finance.treasury_accounts (account_kind, is_active);

-- ── 3) حركات الخزينة تنتمي لسياق وردية (النقد المتوقع = قواعد الحركات حرفياً) ──
ALTER TABLE finance.treasury_transactions
    ADD COLUMN IF NOT EXISTS shift_id BIGINT REFERENCES sales.shifts(shift_id);

COMMENT ON COLUMN finance.treasury_transactions.shift_id IS
    'سياق الوردية للحركة (إن انطبقت): كل حركة على درج المحطة أثناء وردية تحمل وردیتها — المتوقع في الدرج = افتتاحي + Σ(الحركات×اتجاهها) على درج المحطة بوردیتها (بوابة قبول G8)';

CREATE INDEX IF NOT EXISTS idx_treasury_txn_shift
    ON finance.treasury_transactions (shift_id, treasury_account_id) WHERE shift_id IS NOT NULL;

-- (ب) لا ترحيل مكرر: أثر مالي واحد لكل (مستند، خزينة) — إعادة الترحيل تُرفض على مستوى القاعدة.
--     ملاحظة: التعويض/العكس يستخدم source_doc_type مميزاً (finance.expense.reversal) فلا يتعارض.
CREATE UNIQUE INDEX IF NOT EXISTS uq_treasury_txn_source
    ON finance.treasury_transactions (source_doc_type, source_doc_id, treasury_account_id);

-- ── 3-ب) سياق الوردية للسند (§14.1 «عمليات صرف/إيداع مسموح بها» خلال الوردية) ──
ALTER TABLE finance.vouchers
    ADD COLUMN IF NOT EXISTS shift_id BIGINT REFERENCES sales.shifts(shift_id);

COMMENT ON COLUMN finance.vouchers.shift_id IS
    'وردية سياق السند عند تضمينه درج محطة — حركات الخزينة المرتبطة تحمل الوردية الفعلية وقت الترحيل (G8)';

CREATE INDEX IF NOT EXISTS idx_vouchers_shift ON finance.vouchers (shift_id) WHERE shift_id IS NOT NULL;

-- ── 4) مصروف الوردية (§14.1 «مصروفات» ضمن متابعة الوردية · §32 فصل مصروف الوردية) ──
ALTER TABLE finance.expenses
    ADD COLUMN IF NOT EXISTS shift_id BIGINT REFERENCES sales.shifts(shift_id);

COMMENT ON COLUMN finance.expenses.shift_id IS
    'ربط المصروف بالوردية عند الانطباق (§32) — مصروف مدفوع من درج المحطة أثناء الوردية يخصم من المتوقع في الدرج';

CREATE INDEX IF NOT EXISTS idx_expenses_shift ON finance.expenses (shift_id) WHERE shift_id IS NOT NULL;

-- ── 5) توزيع سند القبض على فواتير العميل ≤ المتبقي (§13 · PRT-003) ───────────
CREATE TABLE finance.voucher_allocations (
    allocation_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    voucher_id      BIGINT NOT NULL REFERENCES finance.vouchers(voucher_id) ON DELETE CASCADE,
    invoice_id      BIGINT NOT NULL REFERENCES sales.invoices(invoice_id),
    amount          NUMERIC(19,4) NOT NULL CHECK (amount > 0),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (voucher_id, invoice_id)
);
COMMENT ON TABLE finance.voucher_allocations IS 'توزيع سند قبض العميل على فواتيره المحددة — منع تخصيص مبلغ أكبر من المتبقي دون سياسة معتمدة (§13/PRT-003)';

CREATE INDEX idx_voucher_allocations_invoice ON finance.voucher_allocations (invoice_id);

-- (أ) حراسة التوزيع: سند قبض لعميل فقط · المجموع ≤ مبلغ السند · كل توزيع ≤ متبقي الفاتورة
--     (المتبقي = المستحق − Σ توزيعات السندات غير الملغاة على الفاتورة)
CREATE OR REPLACE FUNCTION finance.tf_voucher_allocation_guard()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_voucher     finance.vouchers;
    v_customer    BIGINT;
    v_allocated   NUMERIC(19,4);
    v_due         NUMERIC(19,4);
    v_already     NUMERIC(19,4);
    v_invoice     sales.invoices;
BEGIN
    SELECT * INTO v_voucher FROM finance.vouchers WHERE voucher_id = COALESCE(NEW.voucher_id, OLD.voucher_id);
    IF v_voucher.voucher_type <> 'RECEIPT' OR v_voucher.counterparty_type <> 'CUSTOMER' THEN
        RAISE EXCEPTION 'VOUCHER_ALLOCATION_NOT_ALLOWED: التوزيع على الفواتير لسند قبض من عميل فقط (§13)'
            USING ERRCODE = 'check_violation';
    END IF;

    IF TG_OP IN ('INSERT','UPDATE') THEN
        -- الفاتورة للعميل نفسه وآجلة مستحقة
        SELECT * INTO v_invoice FROM sales.invoices WHERE invoice_id = NEW.invoice_id;
        IF v_invoice.customer_id IS NULL OR v_invoice.customer_id <> v_voucher.counterparty_id THEN
            RAISE EXCEPTION 'ALLOCATION_INVOICE_MISMATCH: الفاتورة ليست لهذا العميل (§13)'
                USING ERRCODE = 'check_violation';
        END IF;
        IF v_invoice.status NOT IN ('POSTED','PARTIAL_RETURN','FULL_RETURN') THEN
            RAISE EXCEPTION 'ALLOCATION_INVOICE_NOT_POSTED: التوزيع على فاتورة مرحّلة فقط (§13)'
                USING ERRCODE = 'check_violation';
        END IF;

        -- مجموع توزيعات السند ≤ مبلغ السند
        SELECT COALESCE(SUM(amount),0) INTO v_allocated FROM finance.voucher_allocations
         WHERE voucher_id = NEW.voucher_id AND allocation_id <> COALESCE(NEW.allocation_id, -1);
        IF v_allocated + NEW.amount > v_voucher.amount + 0.00005 THEN
            RAISE EXCEPTION 'VOUCHER_OVER_ALLOCATED: مجموع التوزيعات (%) يتجاوز مبلغ السند (%) (§13)',
                v_allocated + NEW.amount, v_voucher.amount
                USING ERRCODE = 'check_violation';
        END IF;

        -- التوزيع ≤ متبقي الفاتورة: المستحق (إجمالي − خصوم V005) − المرتجعات − توزيعات سندات غير ملغاة
        SELECT (v_invoice.gross_total - v_invoice.line_discount_total - v_invoice.invoice_discount - v_invoice.returned_total)
          INTO v_due;
        SELECT COALESCE(SUM(va.amount),0) INTO v_already
          FROM finance.voucher_allocations va
          JOIN finance.vouchers v ON v.voucher_id = va.voucher_id
         WHERE va.invoice_id = NEW.invoice_id
           AND v.status <> 'CANCELLED'
           AND va.voucher_id <> NEW.voucher_id;
        IF NEW.amount > v_due - v_already + 0.00005 THEN
            RAISE EXCEPTION 'ALLOCATION_EXCEEDS_REMAINING: التوزيع (%) يتجاوز متبقي الفاتورة (%) — منع تخصيص أكبر من المتبقي (§13)',
                NEW.amount, v_due - v_already
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

CREATE TRIGGER trg_voucher_allocation_guard
    BEFORE INSERT OR UPDATE OR DELETE ON finance.voucher_allocations
    FOR EACH ROW EXECUTE FUNCTION finance.tf_voucher_allocation_guard();

-- (ب) توزيعات السند المرحَّل مجمّدة — التحرير قبل الترحيل فقط
CREATE OR REPLACE FUNCTION finance.tf_voucher_allocations_frozen()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_status TEXT;
BEGIN
    SELECT status INTO v_status FROM finance.vouchers
     WHERE voucher_id = COALESCE(NEW.voucher_id, OLD.voucher_id);
    IF v_status IN ('POSTED','CANCELLED') THEN
        RAISE EXCEPTION 'VOUCHER_ALLOCATIONS_FROZEN: توزيعات السند المرحّل/الملغى مجمّدة — التصحيح بسند معاكس (§13/§14.2)'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

CREATE TRIGGER trg_voucher_allocations_frozen
    BEFORE INSERT OR UPDATE OR DELETE ON finance.voucher_allocations
    FOR EACH ROW EXECUTE FUNCTION finance.tf_voucher_allocations_frozen();

-- ── 6) آلة حياة السند: DRAFT→POSTED/CANCELLED فقط — المرحَّل مجمّد والملغى نهائي ──
CREATE OR REPLACE FUNCTION finance.tf_voucher_lifecycle()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'VOUCHER_IMMUTABLE: السند غير المسودة لا يُحذف — الإلغاء بمستند معاكس موثق (§14.2/§0.1 ق6)'
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;

    IF OLD.status = 'DRAFT' AND NEW.status IN ('POSTED','CANCELLED') THEN
        RETURN NEW;   -- الترحيل أو إلغاء المسودة — كلاهما موثق بالتدقيق
    END IF;

    IF OLD.status = 'POSTED' THEN
        RAISE EXCEPTION 'VOUCHER_IMMUTABLE: السند المرحّل نهائي — لا تعديل ولا حذف؛ التصحيح بسند معاكس مرتبط (§14.2)'
            USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'CANCELLED' THEN
        RAISE EXCEPTION 'VOUCHER_CANCELLED_FINAL: السند الملغى نهائي لا يُستأنف (§14.2)'
            USING ERRCODE = 'check_violation';
    END IF;

    -- المسودة: تعديل حر بشرط عدم كسر التوزيعات القائمة (المبلغ ≥ مجموع التوزيعات)
    IF OLD.status = 'DRAFT' AND NEW.status = 'DRAFT' AND NEW.amount < OLD.amount THEN
        IF (SELECT COALESCE(SUM(amount),0) FROM finance.voucher_allocations WHERE voucher_id = NEW.voucher_id) > NEW.amount + 0.00005 THEN
            RAISE EXCEPTION 'VOUCHER_UNDER_AMOUNT: لا يقلّص مبلغ المسودة دون مجموع توزيعاتها (§13)'
                USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_voucher_lifecycle
    BEFORE UPDATE OR DELETE ON finance.vouchers
    FOR EACH ROW EXECUTE FUNCTION finance.tf_voucher_lifecycle();

-- (ب) أثر الترحيل صحيح عند الالتزام (قيد مؤجل): سند القبض/الصرف = حركة واحدة،
--     والتحويل = حركتان متقابلتان على الخزينتين — لا يُلتزم الترحيل بأثر ناقص أو زائد
CREATE OR REPLACE FUNCTION finance.tf_voucher_posted_legs()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_legs   INT;
    v_in     NUMERIC(19,4);
    v_out    NUMERIC(19,4);
    v_on_src NUMERIC(19,4);
    v_on_dst NUMERIC(19,4);
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.status = 'POSTED' AND OLD.status = 'DRAFT' THEN
        IF NEW.voucher_type IN ('RECEIPT','DISBURSEMENT') THEN
            SELECT COUNT(*), COALESCE(SUM(amount),0) INTO v_legs, v_in
              FROM finance.treasury_transactions
             WHERE source_doc_type = 'finance.voucher' AND source_doc_id = NEW.voucher_id
               AND treasury_account_id = NEW.treasury_account_id;
            IF v_legs <> 1 OR v_in <> NEW.amount
               OR NOT EXISTS (SELECT 1 FROM finance.treasury_transactions t
                               WHERE t.source_doc_type='finance.voucher' AND t.source_doc_id=NEW.voucher_id
                                 AND t.treasury_account_id=NEW.treasury_account_id
                                 AND t.direction = CASE WHEN NEW.voucher_type='RECEIPT' THEN 1 ELSE -1 END
                                 AND t.amount = NEW.amount) THEN
                RAISE EXCEPTION 'VOUCHER_POST_LEG_INVALID: ترحيل السند يتطلب حركة خزينة واحدة مطابقة الاتجاه والمبلغ (§14.2 — لا ترحيل بأثر ناقص)';
            END IF;
        ELSIF NEW.voucher_type = 'TRANSFER' THEN
            SELECT COUNT(*), COALESCE(SUM(amount),0) INTO v_legs, v_on_src
              FROM finance.treasury_transactions
             WHERE source_doc_type='finance.voucher' AND source_doc_id=NEW.voucher_id
               AND treasury_account_id = NEW.treasury_account_id;
            SELECT COALESCE(SUM(amount),0) INTO v_on_dst
              FROM finance.treasury_transactions
             WHERE source_doc_type='finance.voucher' AND source_doc_id=NEW.voucher_id
               AND treasury_account_id = NEW.counter_treasury_account_id;
            IF v_legs <> 1 OR v_on_src <> NEW.amount OR v_on_dst <> NEW.amount
               OR NOT EXISTS (SELECT 1 FROM finance.treasury_transactions t
                               WHERE t.source_doc_type='finance.voucher' AND t.source_doc_id=NEW.voucher_id
                                 AND t.treasury_account_id=NEW.treasury_account_id AND t.direction=-1 AND t.amount=NEW.amount)
               OR NOT EXISTS (SELECT 1 FROM finance.treasury_transactions t
                               WHERE t.source_doc_type='finance.voucher' AND t.source_doc_id=NEW.voucher_id
                                 AND t.treasury_account_id=NEW.counter_treasury_account_id AND t.direction=1 AND t.amount=NEW.amount) THEN
                RAISE EXCEPTION 'VOUCHER_TRANSFER_LEGS_INVALID: التحويل يرحَّل بحركتين متقابلتين (صرف من المصدر + قبض في الوجهة) بالمبلغ نفسه (§14.2 — التحويل لا يولد إيراداً ولا مصروفاً)';
            END IF;
        END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_voucher_posted_legs
    AFTER UPDATE ON finance.vouchers
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finance.tf_voucher_posted_legs();

-- ── 7) آلة حياة المصروف: مسودة → اعتماد → ترحيل · والإلغاء المرحَّل بعكس موثق ──
CREATE OR REPLACE FUNCTION finance.tf_expense_lifecycle()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'EXPENSE_IMMUTABLE: المصروف غير المسودة لا يُحذف تاريخياً — إلغاء المرحَّل بعكس موثق (§14.4)'
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;

    -- المسارات المشروعة: DRAFT→APPROVED · DRAFT→POSTED (مصروف وردية مدفوع من الدرج — القاعدة الموثقة G8)
    -- · APPROVED→POSTED · DRAFT/APPROVED→CANCELLED · POSTED→CANCELLED (عكس موثق)
    IF (OLD.status = 'DRAFT'   AND NEW.status IN ('APPROVED','POSTED','CANCELLED'))
    OR (OLD.status = 'APPROVED' AND NEW.status IN ('POSTED','CANCELLED'))
    OR (OLD.status = 'POSTED'  AND NEW.status = 'CANCELLED') THEN
        IF OLD.status = 'POSTED' AND length(trim(coalesce(NEW.notes,''))) < 3 THEN
            RAISE EXCEPTION 'EXPENSE_REVERSAL_REASON_REQUIRED: إلغاء المصروف المرحّل يتطلب سبباً موثقاً (يُلحق بملاحظات المستند §14.4)'
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END IF;

    -- تحرير غير مالي لنفس الحالة (المسودة حرة؛ المُعتمد ماليته مجمّدة بمشغل آخر)
    IF OLD.status = NEW.status AND OLD.status IN ('DRAFT','APPROVED') THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'EXPENSE_STATUS_INVALID: انتقال حالة غير مشروع (%) → (%) — المصروف: مسودة→اعتماد→ترحيل والإلغاء موثق (§14.4)',
        OLD.status, NEW.status
        USING ERRCODE = 'check_violation';
END $$;

CREATE TRIGGER trg_expense_lifecycle
    BEFORE UPDATE OR DELETE ON finance.expenses
    FOR EACH ROW EXECUTE FUNCTION finance.tf_expense_lifecycle();

-- (ب) تجميد الحقول المالية بعد الاعتماد (المسودة وحدها قابلة للتحرير المالي)
CREATE OR REPLACE FUNCTION finance.tf_expense_financials_frozen()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status IN ('APPROVED','POSTED','CANCELLED') THEN
        IF (NEW.amount, NEW.tax_amount, NEW.category_id, NEW.treasury_account_id, NEW.payment_method)
         IS NOT DISTINCT FROM (OLD.amount, OLD.tax_amount, OLD.category_id, OLD.treasury_account_id, OLD.payment_method) THEN
            RETURN COALESCE(NEW, OLD);
        END IF;
        RAISE EXCEPTION 'EXPENSE_FINANCIALS_FROZEN: الحقول المالية للمصروف المعتمد/المرحّل مجمّدة — التصحيح بالإلغاء الموثق (§14.4)'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

CREATE TRIGGER trg_expense_financials_frozen
    BEFORE UPDATE ON finance.expenses
    FOR EACH ROW EXECUTE FUNCTION finance.tf_expense_financials_frozen();

-- (ج) أثر الترحيل عند الالتزام: حركة صرف واحدة بمبلغ (المبلغ+الضريبة) عند السداد النقدي/البنكي،
--     وإلغاء المرحَّل يترك أثراً صافياً صفراً (عكس موثق بمصدر finance.expense.reversal)
CREATE OR REPLACE FUNCTION finance.tf_expense_posted_leg()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_net NUMERIC(19,4); v_legs INT;
BEGIN
    v_net := COALESCE(NEW.amount,0) + COALESCE(NEW.tax_amount,0);
    IF TG_OP = 'UPDATE' AND NEW.status = 'POSTED' AND OLD.status IN ('DRAFT','APPROVED') THEN
        IF NEW.treasury_account_id IS NOT NULL THEN
            SELECT COUNT(*) INTO v_legs FROM finance.treasury_transactions
             WHERE source_doc_type='finance.expense' AND source_doc_id=NEW.expense_id
               AND treasury_account_id=NEW.treasury_account_id AND direction=-1 AND amount=v_net;
            IF v_legs <> 1 THEN
                RAISE EXCEPTION 'EXPENSE_POST_LEG_INVALID: ترحيل المصروف المسدد يتطلب حركة صرف واحدة بمبلغ المستند+الضريبة (%) (§14.4)', v_net;
            END IF;
        END IF;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status = 'CANCELLED' AND OLD.status = 'POSTED' THEN
        IF COALESCE((
            SELECT SUM(t.amount * t.direction) FROM finance.treasury_transactions t
             WHERE (t.source_doc_type='finance.expense' AND t.source_doc_id=NEW.expense_id)
                OR (t.source_doc_type='finance.expense.reversal' AND t.source_doc_id=NEW.expense_id)), 0) <> 0 THEN
            RAISE EXCEPTION 'EXPENSE_REVERSAL_LEG_INVALID: إلغاء المصروف المرحّل يترك أثراً صافياً صفراً — حركة عكسية موثقة إلزامية (§14.4)';
        END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_expense_posted_leg
    AFTER UPDATE ON finance.expenses
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finance.tf_expense_posted_leg();

-- ── 8) آلة حياة حدث رأس المال: DRAFT→POSTED/CANCELLED — المرحَّل مجمّد ────────
--     (قيد V006 القائم: OPENING_BALANCE يستلزم approval_id عند الإنشاء — D-14 سارٍ)
CREATE OR REPLACE FUNCTION finance.tf_capital_lifecycle()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'CAPITAL_EVENT_IMMUTABLE: حدث رأس المال غير المسودة لا يُحذف — سجل ملكية دائم (§14.3)'
                USING ERRCODE = 'check_violation';
        END IF;
        RETURN OLD;
    END IF;
    IF (OLD.status = 'DRAFT' AND NEW.status IN ('POSTED','CANCELLED')) THEN
        RETURN NEW;
    END IF;
    IF OLD.status = 'POSTED' THEN
        RAISE EXCEPTION 'CAPITAL_EVENT_IMMUTABLE: حدث رأس المال المرحّل موثق نهائياً — لا تعديل (§14.3)'
            USING ERRCODE = 'check_violation';
    END IF;
    IF OLD.status = 'CANCELLED' THEN
        RAISE EXCEPTION 'CAPITAL_EVENT_CANCELLED_FINAL: الحدث الملغى نهائي (§14.3)'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_capital_lifecycle
    BEFORE UPDATE OR DELETE ON finance.capital_events
    FOR EACH ROW EXECUTE FUNCTION finance.tf_capital_lifecycle();

-- (ب) أثر الترحيل عند الالتزام: حركة واحدة بالاتجاه الصحيح إذا حُددت خزينة
CREATE OR REPLACE FUNCTION finance.tf_capital_posted_leg()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_dir INT;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.status = 'POSTED' AND OLD.status = 'DRAFT' AND NEW.treasury_account_id IS NOT NULL THEN
        v_dir := CASE WHEN NEW.event_type IN ('OWNER_CONTRIBUTION','LOAN_RECEIVED','OPENING_BALANCE') THEN 1 ELSE -1 END;
        IF NOT EXISTS (SELECT 1 FROM finance.treasury_transactions t
                        WHERE t.source_doc_type='finance.capital_event' AND t.source_doc_id=NEW.capital_id
                          AND t.treasury_account_id=NEW.treasury_account_id
                          AND t.direction=v_dir AND t.amount=NEW.amount) THEN
            RAISE EXCEPTION 'CAPITAL_POST_LEG_INVALID: ترحيل حدث رأس المال النقدي يتطلب حركة خزينة واحدة مطابقة النوع والاتجاه والمبلغ (§14.3)';
        END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_capital_posted_leg
    AFTER UPDATE ON finance.capital_events
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finance.tf_capital_posted_leg();

-- ── 9) تسلسلات أرقام المستندات المالية (نمط V017/V018) ───────────────────────
CREATE SEQUENCE IF NOT EXISTS finance.voucher_number_seq;
CREATE SEQUENCE IF NOT EXISTS finance.expense_number_seq;
CREATE SEQUENCE IF NOT EXISTS finance.capital_event_number_seq;

COMMENT ON SEQUENCE finance.voucher_number_seq  IS 'أرقام السندات: RV-/DV-/TV-<seq> (قبض/صرف/تحويل) — التفرد مكفول بـ UNIQUE(voucher_number) في V006';
COMMENT ON SEQUENCE finance.expense_number_seq  IS 'أرقام المصروفات: EX-<seq>';
COMMENT ON SEQUENCE finance.capital_event_number_seq IS 'أرقام أحداث رأس المال: CE-<seq>';

-- ── 10) صلاحيات G8 (إضافات أمامية على مصفوفة V009 — لا تعديل على القائم) ─────
INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('sales.shift.close',          'sales',   'إغلاق الوردية بعد عدّ النقدية (G8 · §14.1)'),
    ('sales.shift.approve_variance','sales',  'اعتماد إغلاق وردية بفارق موثق السبب (G8 · §14.1)'),
    ('finance.treasury.manage',    'finance', 'إدارة حسابات الخزينة والأدراج والبنوك (G8 · §14.2)'),
    ('finance.expense.create',     'finance', 'تسجيل مصروف (ومصروف وردية مدفوع من درج المحطة) (G8 · §14.4)')
ON CONFLICT (code) DO NOTHING;

-- إسنادات G8 (أقل صلاحية تفصل بين الأدوار):
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('sales.shift.close','sales.shift.approve_variance','finance.treasury.manage','finance.expense.create')
 WHERE r.code = 'OWNER'
ON CONFLICT DO NOTHING;

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('sales.shift.close','sales.shift.approve_variance','finance.treasury.manage','finance.expense.create')
 WHERE r.code = 'MANAGER'
ON CONFLICT DO NOTHING;

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('sales.shift.close','sales.shift.approve_variance','finance.expense.create')
 WHERE r.code = 'SUPERVISOR'
ON CONFLICT DO NOTHING;

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('sales.shift.close','finance.expense.create')
 WHERE r.code = 'CASHIER'
ON CONFLICT DO NOTHING;

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('finance.expense.create')
 WHERE r.code = 'ACCOUNTANT'
ON CONFLICT DO NOTHING;

-- ── 11) بذور مرجعية: خزائن + فئات مصروفات + درج لكل محطة قائمة ──────────────
-- (أ) الخزينة الرئيسية والبنك (مرجعيان مركزيان — القيمة من أحداث رأس المال لا من هنا)
INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, currency, is_active)
SELECT 'MAIN_SAFE', 'الخزينة الرئيسية', 'SAFE', NULL, 'EGP', true
 WHERE NOT EXISTS (SELECT 1 FROM finance.treasury_accounts WHERE account_kind='SAFE' AND branch_id IS NULL);

INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, currency, is_active)
SELECT 'MAIN_BANK', 'البنك الرئيسي', 'BANK', NULL, 'EGP', true
 WHERE NOT EXISTS (SELECT 1 FROM finance.treasury_accounts WHERE account_kind='BANK' AND branch_id IS NULL);

-- (ب) درج لكل محطة قائمة بلا درج (بذر أمامي — المحطات الجديدة تنشئ درجها عند إنشائها بقرار إداري)
INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id, station_id, currency, is_active)
SELECT 'DRAWER-ST-' || s.code, 'درج محطة ' || s.name_ar, 'CASH_DRAWER', s.branch_id, s.station_id, 'EGP', true
  FROM core.stations s
 WHERE NOT EXISTS (SELECT 1 FROM finance.treasury_accounts t WHERE t.station_id = s.station_id)
ON CONFLICT DO NOTHING;

-- (ج) فئات مصروفات مرجعية (قابلة للتهيئة — §14.4 «فئات قابلة للتهيئة»)
INSERT INTO finance.expense_categories (code, name_ar) VALUES
    ('RENT',        'إيجار'),
    ('UTILITIES',   'مرافق (كهرباء ومياه واتصالات)'),
    ('MAINTENANCE', 'صيانة وإصلاحات'),
    ('CLEANING',    'نظافة وتعقيم'),
    ('TRANSPORT',   'نقل وشحن'),
    ('MISC_OPS',    'مصاريف تشغيلية متفرقة')
ON CONFLICT (code) DO NOTHING;

-- ── 12) قوالب مستندات G8 المستقلة 58/80 (بوابة القبول: «قوالب 58/80 للسندات
--          والرواتب») — قالب مستقل لكل مستند × وسيط، لا نسخة مكبرة (§29.2/§29.6 ق3) ──
INSERT INTO print.document_templates (template_code, document_type, media_class, version, body_definition) VALUES
    ('FIN_RV_ROLL80', 'FINANCE_RECEIPT_VOUCHER', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §14.2 · §33 — سند قبض مالي رول 80 (قالب مستقل لا نسخة مكبرة من 58)",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "branch_name", "doc_title_receipt_voucher", "voucher_number", "posted_at_local"]},
        {"kind": "counterparty", "fields": ["counterparty_label", "counterparty_name", "treasury_code_name"]},
        {"kind": "amount", "fields": ["amount", "amount_in_words_ar"], "emphasis": true},
        {"kind": "reason", "fields": ["reason_ar", "shift_ref_optional"]},
        {"kind": "allocations", "fields": ["invoice_number", "allocated_amount", "invoice_remaining_after"], "when": "customer_receipt_with_allocations"},
        {"kind": "footer", "fields": ["received_by", "signature_line", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('FIN_RV_ROLL58', 'FINANCE_RECEIPT_VOUCHER', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §14.2 · §33 — سند قبض مالي رول 58 (تخطيط مستقل مضغوط)",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "doc_title_receipt_voucher", "voucher_number"]},
        {"kind": "counterparty", "fields": ["counterparty_name", "treasury_code"]},
        {"kind": "amount", "fields": ["amount"], "emphasis": true},
        {"kind": "reason", "fields": ["reason_ar"]},
        {"kind": "footer", "fields": ["received_by", "signature_line", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('FIN_DV_ROLL80', 'FINANCE_DISBURSEMENT_VOUCHER', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §14.2 · §33 — سند صرف مالي رول 80",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "branch_name", "doc_title_disbursement_voucher", "voucher_number", "posted_at_local"]},
        {"kind": "counterparty", "fields": ["counterparty_label", "counterparty_name", "treasury_code_name"]},
        {"kind": "amount", "fields": ["amount", "amount_in_words_ar"], "emphasis": true},
        {"kind": "reason", "fields": ["reason_ar", "shift_ref_optional"]},
        {"kind": "footer", "fields": ["disbursed_by", "recipient_signature_line", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('FIN_DV_ROLL58', 'FINANCE_DISBURSEMENT_VOUCHER', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §14.2 · §33 — سند صرف مالي رول 58",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "doc_title_disbursement_voucher", "voucher_number"]},
        {"kind": "counterparty", "fields": ["counterparty_name", "treasury_code"]},
        {"kind": "amount", "fields": ["amount"], "emphasis": true},
        {"kind": "reason", "fields": ["reason_ar"]},
        {"kind": "footer", "fields": ["disbursed_by", "recipient_signature_line", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('FIN_TV_ROLL80', 'FINANCE_TRANSFER_VOUCHER', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §14.2 · §33 — سند تحويل بين خزينتين رول 80 (التحويل الداخلي لا يولد إيراداً ولا مصروفاً)",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "branch_name", "doc_title_transfer_voucher", "voucher_number", "posted_at_local"]},
        {"kind": "legs", "fields": ["from_treasury_code_name", "to_treasury_code_name", "amount"], "emphasis": "amount"},
        {"kind": "reason", "fields": ["reason_ar", "internal_no_income_no_expense_note"]},
        {"kind": "footer", "fields": ["executed_by", "signature_line", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('FIN_TV_ROLL58', 'FINANCE_TRANSFER_VOUCHER', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.3 · §14.2 · §33 — سند تحويل بين خزينتين رول 58",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "doc_title_transfer_voucher", "voucher_number"]},
        {"kind": "legs", "fields": ["from_treasury_code", "to_treasury_code", "amount"], "emphasis": "amount"},
        {"kind": "reason", "fields": ["reason_ar"]},
        {"kind": "footer", "fields": ["executed_by", "signature_line", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('EXP_V_ROLL80', 'EXPENSE_VOUCHER', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §32 · §29.3 · §33 — سند مصروف تشغيلي رول 80",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "branch_name", "doc_title_expense_voucher", "expense_number", "expense_date", "approval_status_ar"]},
        {"kind": "body", "fields": ["beneficiary_or_employee", "category_name_ar", "reason_ar", "requested_amount", "approved_amount", "paid_amount", "remaining_amount", "payment_method_ar", "cost_center_optional", "shift_ref_optional"]},
        {"kind": "footer", "fields": ["created_by", "approved_by_optional", "signature_line", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('EXP_V_ROLL58', 'EXPENSE_VOUCHER', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §32 · §29.3 · §33 — سند مصروف تشغيلي رول 58",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": false,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "doc_title_expense_voucher", "expense_number"]},
        {"kind": "body", "fields": ["beneficiary_or_employee", "category_name_ar", "paid_amount", "payment_method_ar"]},
        {"kind": "reason", "fields": ["reason_ar"]},
        {"kind": "footer", "fields": ["created_by", "signature_line", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('PAYROLL_SLIP_ROLL80', 'PAYROLL_SLIP', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §31.1 · §35.2 — كشف راتب (ملخص سري) رول 80 — طابعة مؤمنة فقط، لا يطبع على طابعة عامة أبداً",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": true, "privacy": "CONFIDENTIAL_PAYROLL_ONLY",
      "blocks": [
        {"kind": "confidential_banner", "fields": ["confidential_badge_ar"]},
        {"kind": "header", "fields": ["store_name", "doc_title_payroll_slip", "period_label", "employee_name", "generated_at_local"]},
        {"kind": "summary", "fields": ["attendance_summary", "shifts_count", "late_absence_overtime_indicators", "cashier_sales_indicators"], "summary_badge": true},
        {"kind": "amounts", "fields": ["approved_entitlements", "approved_deductions", "separate_compensations", "net_salary"], "emphasis": "net_salary"},
        {"kind": "footer", "fields": ["authorized_by", "secure_channel_note", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('PAYROLL_SLIP_ROLL58', 'PAYROLL_SLIP', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §31.1 · §35.2 — كشف راتب (ملخص سري) رول 58 — طابعة مؤمنة فقط",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": true, "privacy": "CONFIDENTIAL_PAYROLL_ONLY",
      "blocks": [
        {"kind": "confidential_banner", "fields": ["confidential_badge_ar"]},
        {"kind": "header", "fields": ["doc_title_payroll_slip", "period_label", "employee_name"]},
        {"kind": "summary", "fields": ["attendance_summary"], "summary_badge": true},
        {"kind": "amounts", "fields": ["approved_entitlements", "approved_deductions", "net_salary"], "emphasis": "net_salary"},
        {"kind": "footer", "fields": ["authorized_by", "secure_channel_note", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('PAYROLL_PAYV_ROLL80', 'PAYROLL_PAYMENT_VOUCHER', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.10 (سند سداد الراتب) · §31.1 · §35.2 — رول 80 — طابعة مؤمنة فقط",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": true, "privacy": "CONFIDENTIAL_PAYROLL_ONLY",
      "blocks": [
        {"kind": "confidential_banner", "fields": ["confidential_badge_ar"]},
        {"kind": "header", "fields": ["store_name", "doc_title_payroll_payment_voucher", "voucher_number", "posted_at_local"]},
        {"kind": "body", "fields": ["employee_name", "period_label", "net_paid", "payment_method_ar", "reference_no"]},
        {"kind": "footer", "fields": ["disbursed_by", "employee_signature_line", "secure_channel_note", "reprint_badge_optional", "queue_note_on_hold"]}
      ]
    }$json$::jsonb),
    ('PAYROLL_PAYV_ROLL58', 'PAYROLL_PAYMENT_VOUCHER', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §29.10 · §31.1 · §35.2 — سند سداد راتب رول 58 — طابعة مؤمنة فقط",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": true, "privacy": "CONFIDENTIAL_PAYROLL_ONLY",
      "blocks": [
        {"kind": "confidential_banner", "fields": ["confidential_badge_ar"]},
        {"kind": "header", "fields": ["doc_title_payroll_payment_voucher", "voucher_number"]},
        {"kind": "body", "fields": ["employee_name", "period_label", "net_paid"]},
        {"kind": "footer", "fields": ["disbursed_by", "employee_signature_line", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('CASHIER_APPX_ROLL80', 'CASHIER_SALES_APPENDIX', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §31.2 — ملحق مبيعات الكاشير (ملخص رول عند الطلب) — وسم «ملخص» إلزامي (PMR-016)",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": false, "summary_badge": true,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "doc_title_cashier_appendix", "cashier_name", "period_label", "summary_badge_ar"]},
        {"kind": "performance", "fields": ["invoice_count", "gross_before_discounts", "discounts_total", "returns_total", "net_sales", "collected_cash_card_credit_split", "credit_sales"], "emphasis": "net_sales"},
        {"kind": "profit_note", "fields": ["profit_when_cost_complete_only"], "honest_note": "unavailable_without_complete_cost"},
        {"kind": "footer", "fields": ["generated_by", "a4_detailed_available_by_explicit_request_only", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('CASHIER_APPX_ROLL58', 'CASHIER_SALES_APPENDIX', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §31.2 — ملخص مبيعات الكاشير رول 58",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": false, "summary_badge": true,
      "blocks": [
        {"kind": "header", "fields": ["doc_title_cashier_appendix", "cashier_name", "period_label", "summary_badge_ar"]},
        {"kind": "performance", "fields": ["invoice_count", "net_sales", "collected_total"], "emphasis": "net_sales"},
        {"kind": "footer", "fields": ["generated_by", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('SHIFT_CLOSE_ROLL80', 'SHIFT_CLOSE_SUMMARY', 'ROLL_80', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §14.1 — ملخص إقفال الوردية رول 80 (عند الطلب) — وسم «ملخص» (PMR-016)",
      "printable_width_mm": 72.0, "direction": "rtl", "confidential": false, "summary_badge": true,
      "blocks": [
        {"kind": "header", "fields": ["store_name", "station_code", "doc_title_shift_close", "shift_number", "opened_by", "opened_at_local", "closed_by", "closed_at_local", "summary_badge_ar"]},
        {"kind": "expected", "fields": ["opening_float", "cash_sales", "cash_refunds", "drawer_expenses", "voucher_deposits", "voucher_withdrawals", "transfer_in", "transfer_out", "expected_cash"]},
        {"kind": "counted", "fields": ["counted_cash", "variance", "variance_reason", "settlement_status_ar"], "emphasis": "variance"},
        {"kind": "separation", "fields": ["cash_total", "card_total", "credit_total", "card_not_in_drawer_note"]},
        {"kind": "footer", "fields": ["counted_by", "closed_by_signature_line", "a4_detailed_available_by_explicit_request_only", "reprint_badge_optional"]}
      ]
    }$json$::jsonb),
    ('SHIFT_CLOSE_ROLL58', 'SHIFT_CLOSE_SUMMARY', 'ROLL_58', 1, $json$
    {
      "design_ref": "GFM-CSharp 2.4.1 §14.1 — ملخص إقفال الوردية رول 58",
      "printable_width_mm": 48.0, "direction": "rtl", "confidential": false, "summary_badge": true,
      "blocks": [
        {"kind": "header", "fields": ["doc_title_shift_close", "shift_number", "summary_badge_ar"]},
        {"kind": "expected", "fields": ["opening_float", "expected_cash"]},
        {"kind": "counted", "fields": ["counted_cash", "variance", "variance_reason"], "emphasis": "variance"},
        {"kind": "footer", "fields": ["counted_by", "reprint_badge_optional"]}
      ]
    }$json$::jsonb)
ON CONFLICT (template_code, document_type, media_class, version) DO NOTHING;

-- ── 13) قواعد طباعة G8 (§29.10 — سياسة مستقلة لكل نوع مستند) ─────────────────
--     السندات المالية: طباعة صريحة عند الطلب على طابعة المالية (لا تلقائية)
--     كشوف الرواتب: صريحة مخولة على الطابعة المؤمنة فقط (seed V009 قائم — لا نغيره)
INSERT INTO print.auto_print_rules (document_type, policy, printer_role, notes) VALUES
    ('FINANCE_RECEIPT_VOUCHER',      'EXPLICIT_USER', 'FINANCE_VOUCHERS', 'سند قبض: طباعة صريحة عند الطلب على طابعة المالية — لا طباعة تلقائية (§29.10/G8)'),
    ('FINANCE_DISBURSEMENT_VOUCHER', 'EXPLICIT_USER', 'FINANCE_VOUCHERS', 'سند صرف: طباعة صريحة عند الطلب على طابعة المالية (§29.10/G8)'),
    ('FINANCE_TRANSFER_VOUCHER',     'EXPLICIT_USER', 'FINANCE_VOUCHERS', 'سند تحويل بين خزينتين: طباعة صريحة عند الطلب (§29.10/G8)'),
    ('SHIFT_CLOSE_SUMMARY',          'EXPLICIT_USER', 'FINANCE_VOUCHERS', 'ملخص إقفال الوردية: رول عند الطلب — وسم «ملخص» دائماً (§14.1/§31.3)')
ON CONFLICT (document_type) DO NOTHING;

-- ── 14) تحقق داخلي إلزامي (نمط V017/V018) — لا نجاح صامت ────────────────────
DO $$
DECLARE
    v_expected_grants CONSTANT text[] := ARRAY[
        'sales.shift.close|OWNER', 'sales.shift.close|MANAGER', 'sales.shift.close|SUPERVISOR', 'sales.shift.close|CASHIER',
        'sales.shift.approve_variance|OWNER', 'sales.shift.approve_variance|MANAGER', 'sales.shift.approve_variance|SUPERVISOR',
        'finance.treasury.manage|OWNER', 'finance.treasury.manage|MANAGER',
        'finance.expense.create|OWNER', 'finance.expense.create|MANAGER', 'finance.expense.create|SUPERVISOR',
        'finance.expense.create|CASHIER', 'finance.expense.create|ACCOUNTANT'
    ];
    v_missing       integer := 0;
    v_cashier_extra integer := 0;
    v_tables        integer;
    v_cols          integer;
    v_idx           integer;
    v_triggers      integer;
    v_templates     integer;
    v_rules         integer;
    v_accounts      integer;
    v_categories    integer;
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
        RAISE EXCEPTION 'V019 check failed: % إسناداً متوقعاً لصلاحيات G8 مفقود', v_missing;
    END IF;

    -- (ب) الكاشير بلا صلاحيات تجاوز مالية (لا سندات ولا إدارة خزائن ولا اعتماد فوارق ولا ترحيل مصروف عام)
    SELECT COUNT(*) INTO v_cashier_extra
      FROM core.role_permissions rp
      JOIN core.roles r ON r.role_id = rp.role_id
      JOIN core.permissions p ON p.permission_id = rp.permission_id
     WHERE r.code = 'CASHIER'
       AND p.code IN ('finance.voucher.create','finance.voucher.post','finance.treasury.manage',
                      'finance.expense.approve','finance.expense.post','finance.capital.manage',
                      'sales.shift.approve_variance');
    IF v_cashier_extra > 0 THEN
        RAISE EXCEPTION 'V019 check failed: الكاشير يملك صلاحيات مالية لم تُسند له (%)', v_cashier_extra;
    END IF;

    -- (ج) جداول G8 منشأة
    SELECT COUNT(*) INTO v_tables FROM information_schema.tables
     WHERE table_schema='finance' AND table_name IN ('shift_cash_counts','voucher_allocations');
    IF v_tables <> 2 THEN
        RAISE EXCEPTION 'V019 check failed: جدولا G8 (shift_cash_counts/voucher_allocations) غير منشأين (%)', v_tables;
    END IF;

    -- (د) الأعمدة الأمامية على الجداول القائمة
    SELECT COUNT(*) INTO v_cols FROM information_schema.columns
     WHERE (table_schema='sales'    AND table_name='shifts'              AND column_name='variance_settled_by_voucher_id')
        OR (table_schema='finance'  AND table_name='treasury_transactions' AND column_name='shift_id')
        OR (table_schema='finance'  AND table_name='treasury_accounts'     AND column_name='station_id')
        OR (table_schema='finance'  AND table_name='expenses'              AND column_name='shift_id')
        OR (table_schema='finance'  AND table_name='vouchers'              AND column_name='shift_id');
    IF v_cols <> 5 THEN
        RAISE EXCEPTION 'V019 check failed: أعمدة G8 الأمامية غير منشأة كلها (%)', v_cols;
    END IF;

    -- (هـ) فهارس منع التكرار والدرج/المحطة
    SELECT COUNT(*) INTO v_idx FROM pg_indexes
     WHERE (schemaname='finance' AND indexname='uq_treasury_txn_source')
        OR (schemaname='finance' AND indexname='uq_treasury_drawer_per_station');
    IF v_idx <> 2 THEN
        RAISE EXCEPTION 'V019 check failed: فهارس التفرد (أثر مالي/درج المحطة) غير منشأة (%)', v_idx;
    END IF;

    -- (و) مشغلات G8 الحرجة قائمة
    SELECT COUNT(*) INTO v_triggers FROM information_schema.triggers
     WHERE event_object_schema IN ('sales','finance')
       AND trigger_name IN ('trg_shift_close_validate','trg_voucher_lifecycle','trg_voucher_posted_legs',
                            'trg_voucher_allocation_guard','trg_voucher_allocations_frozen',
                            'trg_expense_lifecycle','trg_expense_financials_frozen','trg_expense_posted_leg',
                            'trg_capital_lifecycle','trg_capital_posted_leg');
    IF v_triggers < 10 THEN
        RAISE EXCEPTION 'V019 check failed: مشغلات G8 الحرجة ناقصة (%)', v_triggers;
    END IF;

    -- (ز) قوالب G8 الستة عشر (8 مستندات × 80/58) مسجلة ونشطة
    SELECT COUNT(*) INTO v_templates FROM print.document_templates
     WHERE document_type IN ('FINANCE_RECEIPT_VOUCHER','FINANCE_DISBURSEMENT_VOUCHER','FINANCE_TRANSFER_VOUCHER',
                             'EXPENSE_VOUCHER','PAYROLL_SLIP','PAYROLL_PAYMENT_VOUCHER',
                             'CASHIER_SALES_APPENDIX','SHIFT_CLOSE_SUMMARY')
       AND media_class IN ('ROLL_80','ROLL_58') AND version = 1 AND is_active;
    IF v_templates <> 16 THEN
        RAISE EXCEPTION 'V019 check failed: قوالب G8 58/80 المستقلة غير مسجلة كاملة (16 متوقعة، موجود %)', v_templates;
    END IF;

    -- (ح) قواعد الطباعة الأربع الجديدة قائمة
    SELECT COUNT(*) INTO v_rules FROM print.auto_print_rules
     WHERE document_type IN ('FINANCE_RECEIPT_VOUCHER','FINANCE_DISBURSEMENT_VOUCHER',
                             'FINANCE_TRANSFER_VOUCHER','SHIFT_CLOSE_SUMMARY');
    IF v_rules <> 4 THEN
        RAISE EXCEPTION 'V019 check failed: قواعد طباعة G8 غير مسجلة (4 متوقعة، موجود %)', v_rules;
    END IF;

    -- (ط) الخزائن المرجعية وفئات المصروفات مزروعة
    SELECT COUNT(*) INTO v_accounts FROM finance.treasury_accounts
     WHERE code IN ('MAIN_SAFE','MAIN_BANK') AND is_active;
    IF v_accounts <> 2 THEN
        RAISE EXCEPTION 'V019 check failed: الخزينة/البنك المرجعيان غير مزروعين (%)', v_accounts;
    END IF;
    SELECT COUNT(*) INTO v_categories FROM finance.expense_categories
     WHERE code IN ('RENT','UTILITIES','MAINTENANCE','CLEANING','TRANSPORT','MISC_OPS');
    IF v_categories <> 6 THEN
        RAISE EXCEPTION 'V019 check failed: فئات المصروفات المرجعية غير مزروعة (6 متوقعة، موجود %)', v_categories;
    END IF;
END $$;

COMMIT;
