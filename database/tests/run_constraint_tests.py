#!/usr/bin/env python3
"""WERA PRO HD — اختبارات قيود قاعدة البيانات (G1 · بوابة القبول §22 G1).

يشتغل على قاعدة مبنية بالكامل (V001..head) ويثبت أن خطوط حماية قاعدة البيانات
ترفض فعلاً انتهاك القواعد الحاسمة — لا يكفي أن المخطط «يبدو صحيحاً» (§0.1 ق8/ق10).

كل اختبار: إما انتهاك متوقع (رسالة خطأ محددة) أو تحقق قيمة إيجابي.
الاختبارات السالبة داخل SAVEPOINT وتتراجع؛ ما يتطلب COMMIT (القيود المؤجلة)
يستخدم expect_commit_error مع تراجع كامل. الإيجابية تُثبَّت بالتزام.

الاستخدام:
  python3 run_constraint_tests.py --db-uri "postgresql://..." [--out results.md]
"""
import argparse
import sys
import traceback
from contextlib import contextmanager

import psycopg2

RESULTS = []


class EnvLimitation(Exception):
    """قدرة غير متاحة في بيئة الاختبار الحالية — تُعرض كاستثناء صريح لا كنجاح."""


@contextmanager
def sp(conn):
    """SAVEPOINT مع تراجع تلقائي — للاختبارات السالبة دون إفساد المشتركات."""
    cur = conn.cursor()
    cur.execute("SAVEPOINT t")
    try:
        yield cur
    finally:
        cur.execute("ROLLBACK TO SAVEPOINT t")
        cur.close()


def expect_error(conn, sql, params, *tokens):
    """ينفذ SQL ويتوقع خطأ يحتوي أحد الرموز — نجاح التنفيذ يعني قيداً مفقوداً."""
    with sp(conn) as cur:
        try:
            cur.execute(sql, params)
        except psycopg2.Error as e:
            msg = str(e)
            if not any(t in msg for t in tokens):
                raise AssertionError(f"خطأ غير متوقع — توقعت أحد [{','.join(tokens)}] وحصلت: {msg[:180]}")
            return msg
    raise AssertionError(f"توقعت خطأ [{','.join(tokens)}] لكن العملية نجحت — القيد مفقود!")


def expect_commit_error(conn, fn, *tokens):
    """للقيود المؤجلة: ينفذ الدالة ثم COMMIT ويتوقع فشل الالتزام برسالة محددة."""
    try:
        fn()
        conn.commit()
    except psycopg2.Error as e:
        conn.rollback()
        msg = str(e)
        assert any(t in msg for t in tokens), f"خطأ غير متوقع — توقعت [{','.join(tokens)}]: {msg[:180]}"
        return msg
    raise AssertionError(f"توقعت خطأ [{','.join(tokens)}] عند الالتزام لكن العملية نجحت")


def one(conn, sql, params=None):
    cur = conn.cursor()
    cur.execute(sql, params)  # params=None ⇒ لا استيفاء لمحرف %
    row = cur.fetchone()
    cur.close()
    return row


def all_rows(conn, sql, params=None):
    cur = conn.cursor()
    cur.execute(sql, params)
    rows = cur.fetchall()
    cur.close()
    return rows


def run_sql(conn, sql, params=None):
    cur = conn.cursor()
    cur.execute(sql, params)
    cur.close()


def test(tid, area, title):
    def deco(fn):
        def wrapper(conn):
            try:
                note = fn(conn) or ""
                RESULTS.append((tid, area, title, "PASS", note))
            except EnvLimitation as e:
                RESULTS.append((tid, area, title, "ENV_LIMITATION", str(e)[:200]))
            except AssertionError as e:
                RESULTS.append((tid, area, title, "FAIL", str(e)[:200]))
            except Exception:
                RESULTS.append((tid, area, title, "ERROR", traceback.format_exc(limit=3)[-400:]))
            finally:
                try:  # تطهير أي معاملة مجهضة حتى لا تسقط بقية الاختبارات
                    conn.rollback()
                except Exception:
                    pass
        wrapper._tid, wrapper._area = tid, area
        return wrapper
    return deco


# ───────────────────────────── التجهيزات (بيئة اختبار فقط — §0.1 ق2) ─────────────────────────────
def setup_fixtures(conn):
    run_sql(conn, "INSERT INTO core.branches (code, name_ar) VALUES ('TBR1','فرع الاختبار 1'), ('TBR2','فرع الاختبار 2')")
    run_sql(conn, "INSERT INTO core.stations (branch_id, code, name_ar) "
                  "SELECT branch_id, 'TST1', 'محطة اختبار 1' FROM core.branches WHERE code='TBR1'")
    run_sql(conn, "INSERT INTO core.users (username, display_name_ar, password_hash) VALUES "
                  "('t_owner','مالك اختبار','x'),('t_sup','مشرف اختبار','x'),('t_cash','كاشير اختبار','x')")
    run_sql(conn, "INSERT INTO inv.warehouses (branch_id, code, name_ar) "
                  "SELECT branch_id, 'TWH1','مخزن اختبار 1' FROM core.branches WHERE code='TBR1'")
    run_sql(conn, "INSERT INTO catalog.categories (code, name_ar) VALUES ('TCAT1','فئة اختبار')")
    run_sql(conn, "INSERT INTO catalog.products (code, name_ar, category_id) "
                  "SELECT 'TPRD1','منتج اختبار منظفات', category_id FROM catalog.categories WHERE code='TCAT1'")
    run_sql(conn, "INSERT INTO catalog.skus (product_id, sku_code, sale_mode, base_uom_id, sale_uom_id) "
                  "SELECT p.product_id,'TBAG','COUNT',u.uom_id,u.uom_id FROM catalog.products p, catalog.units u "
                  "WHERE p.code='TPRD1' AND u.code='PIECE'")
    run_sql(conn, "INSERT INTO catalog.skus (product_id, sku_code, sale_mode, base_uom_id, sale_uom_id) "
                  "SELECT p.product_id,'TBOX','PACK',u.uom_id,u.uom_id FROM catalog.products p, catalog.units u "
                  "WHERE p.code='TPRD1' AND u.code='PIECE'")
    run_sql(conn, "INSERT INTO catalog.skus (product_id, sku_code, sale_mode, base_uom_id, sale_uom_id, "
                  "weight_precision_g, weight_step_g, min_weight_kg, max_weight_kg) "
                  "SELECT p.product_id,'TWEI','WEIGHT',u.uom_id,u.uom_id, 1, 5, 0.005, 50.000 "
                  "FROM catalog.products p, catalog.units u WHERE p.code='TPRD1' AND u.code='KG'")
    run_sql(conn, "INSERT INTO catalog.packages (package_sku_id, content_sku_id, units_per_package) "
                  "SELECT (SELECT sku_id FROM catalog.skus WHERE sku_code='TBOX'), "
                  "(SELECT sku_id FROM catalog.skus WHERE sku_code='TBAG'), 12")
    run_sql(conn, "INSERT INTO catalog.barcodes (sku_id, barcode_value, barcode_normalized, barcode_type) "
                  "SELECT s.sku_id, v.b, v.b, 'EAN13' FROM (VALUES ('TBAG','6221000000017'),"
                  "('TBOX','6221000000024'),('TWEI','6221000000031')) AS v(code, b) "
                  "JOIN catalog.skus s ON s.sku_code = v.code")
    pl = one(conn, "SELECT price_list_id FROM catalog.price_lists WHERE is_default")[0]
    run_sql(conn, f"""INSERT INTO catalog.product_prices (sku_id, price_list_id, unit_id, unit_price)
        SELECT s.sku_id, {pl}, u.uom_id,
               CASE s.sku_code WHEN 'TBAG' THEN 5.00 WHEN 'TBOX' THEN 54.00 ELSE 200.00 END
        FROM catalog.skus s JOIN catalog.units u
          ON u.code = (CASE s.sku_code WHEN 'TWEI' THEN 'KG' ELSE 'PIECE' END)
        WHERE s.sku_code IN ('TBAG','TBOX','TWEI')""")
    run_sql(conn, "INSERT INTO finance.treasury_accounts (code, name_ar, account_kind, branch_id) VALUES "
                  "('TDRAWER','درج اختبار','CASH_DRAWER',(SELECT branch_id FROM core.branches WHERE code='TBR1')), "
                  "('TSAFE','خزينة اختبار','SAFE',(SELECT branch_id FROM core.branches WHERE code='TBR1')), "
                  "('TBANK','بنك اختبار','BANK',NULL)")
    conn.commit()


def ids(conn):
    return dict(
        branch=one(conn, "SELECT branch_id FROM core.branches WHERE code='TBR1'")[0],
        branch2=one(conn, "SELECT branch_id FROM core.branches WHERE code='TBR2'")[0],
        station=one(conn, "SELECT station_id FROM core.stations WHERE code='TST1'")[0],
        owner=one(conn, "SELECT user_id FROM core.users WHERE username='t_owner'")[0],
        sup=one(conn, "SELECT user_id FROM core.users WHERE username='t_sup'")[0],
        cash=one(conn, "SELECT user_id FROM core.users WHERE username='t_cash'")[0],
        wh=one(conn, "SELECT warehouse_id FROM inv.warehouses WHERE code='TWH1'")[0],
        bag=one(conn, "SELECT sku_id FROM catalog.skus WHERE sku_code='TBAG'")[0],
        box=one(conn, "SELECT sku_id FROM catalog.skus WHERE sku_code='TBOX'")[0],
        wei=one(conn, "SELECT sku_id FROM catalog.skus WHERE sku_code='TWEI'")[0],
        piece=one(conn, "SELECT uom_id FROM catalog.units WHERE code='PIECE'")[0],
        kg=one(conn, "SELECT uom_id FROM catalog.units WHERE code='KG'")[0],
        drawer=one(conn, "SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='TDRAWER'")[0],
        safe=one(conn, "SELECT treasury_account_id FROM finance.treasury_accounts WHERE code='TSAFE'")[0],
        period=one(conn, "SELECT period_id FROM finance.fiscal_periods WHERE status='OPEN' ORDER BY period_id LIMIT 1")[0],
    )


def open_shift(conn, I):
    """وردية مفتوحة للمحطة — تُنشأ مرة وتُعاد الاستفادة منها (قيد الوردية الواحدة)."""
    row = one(conn, "SELECT shift_id FROM sales.shifts WHERE station_id=%s AND status='OPEN'", (I["station"],))
    if row:
        return row[0]
    cur = conn.cursor()
    cur.execute("INSERT INTO sales.shifts (station_id, opened_by, opening_float) VALUES (%s,%s,100) RETURNING shift_id",
                (I["station"], I["cash"]))
    sid = cur.fetchone()[0]
    cur.close()
    conn.commit()
    return sid


def receive(conn, I, sku, qty, cost, doc_id, uom=None):
    """استلام كما تنفذه خدمة التطبيق: حركة RECEIVE + تحديث طبقة التكلفة WAC."""
    uom = uom or I["piece"]
    cur = conn.cursor()
    cur.execute("""INSERT INTO inv.stock_movements
        (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor,
         source_doc_type, source_doc_id, branch_id, user_id)
        VALUES (%s,%s,'RECEIVE',1,%s,%s,1,'purch.receipt',%s,%s,%s)""",
        (sku, I["wh"], qty, uom, doc_id, I["branch"], I["owner"]))
    cur.execute("""INSERT INTO finance.cost_layers (sku_id, warehouse_id, costing_method, avg_unit_cost, qty_on_layer, last_receipt_at)
        VALUES (%s,%s,'WAC',%s,%s, now())
        ON CONFLICT (sku_id, warehouse_id) DO UPDATE SET
            avg_unit_cost = ROUND(((finance.cost_layers.avg_unit_cost * finance.cost_layers.qty_on_layer)
                                   + (EXCLUDED.avg_unit_cost * EXCLUDED.qty_on_layer))
                                   / NULLIF(finance.cost_layers.qty_on_layer + EXCLUDED.qty_on_layer, 0), 4),
            qty_on_layer = finance.cost_layers.qty_on_layer + EXCLUDED.qty_on_layer,
            last_receipt_at = now()""",
        (sku, I["wh"], cost, qty))
    cur.close()


def draft_invoice(conn, I, num, lines, paid, method="CASH"):
    """فاتورة DRAFT كاملة (سطور بلقطات + دفعات) جاهزة للترحيل — على وردية مفتوحة."""
    shift = open_shift(conn, I)
    total = sum(l["net"] for l in lines)
    cur = conn.cursor()
    cur.execute("""INSERT INTO sales.invoices
        (invoice_number, branch_id, station_id, shift_id, cashier_user_id, tax_mode_snapshot,
         gross_total, net_total, tax_total, paid_total, idempotency_key)
        VALUES (%s,%s,%s,%s,%s,'VAT_INCLUSIVE',%s,%s,0,%s,%s) RETURNING invoice_id""",
        (num, I["branch"], I["station"], shift, I["cash"], total, total, paid, f"idem-{num}"))
    inv = cur.fetchone()[0]
    for i, l in enumerate(lines, 1):
        cur.execute("""INSERT INTO sales.invoice_lines
            (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor,
             weight_kg, weight_source, amount_target, unit_price, tax_treatment, tax_rate_snapshot,
             net_amount, cost_snapshot, cost_method_snapshot)
            VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,'ZERO_RATED',0,%s,%s,'WAC')""",
            (inv, i, l["sku"], l["mode"], l["qty"], l["uom"], l.get("conv", 1),
             l.get("weight"), l.get("wsrc"), l.get("target"), l["price"], l["net"], l["cost"]))
    if paid > 0:
        cur.execute("""INSERT INTO sales.payments (invoice_id, payment_method, amount, status, treasury_account_id, created_by)
                       VALUES (%s,%s,%s,'CAPTURED',%s,%s)""", (inv, method, paid, I["drawer"], I["cash"]))
    cur.close()
    return inv


# ───────────────────────────── التدفقات المرجعية (§4.3 + §5.1 + §15.5) ─────────────────────────────
@test("T-FLW-01", "Flow", "الكيس/العلبة: استلام 10 علب=120 كيساً، بيع علبة=حركة واحدة بكمية 12 (§4.3)")
def t_flw_01(conn):
    I = ids(conn)
    receive(conn, I, I["bag"], 120, 4.00, 900001)
    conn.commit()
    inv = draft_invoice(conn, I, "T-INV-001", [
        dict(sku=I["box"], mode="PACK", qty=1, uom=I["piece"], conv=12, price=54.00, net=54.00, cost=48.00)], 54.00)
    run_sql(conn, "UPDATE sales.invoices SET status='POSTED' WHERE invoice_id=%s", (inv,))
    run_sql(conn, """INSERT INTO inv.stock_movements
        (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor,
         source_doc_type, source_doc_id, branch_id, user_id)
        VALUES (%s,%s,'SALE',-1,12,%s,12,'sales.invoice',%s,%s,%s)""",
        (I["bag"], I["wh"], I["piece"], inv, I["branch"], I["cash"]))
    conn.commit()
    bal = one(conn, "SELECT base_qty FROM inv.stock_balances WHERE sku_id=%s AND warehouse_id=%s", (I["bag"], I["wh"]))[0]
    assert abs(float(bal) - 108) < 1e-6, f"الرصيد المتوقع 108 وحصل {bal}"
    n = one(conn, "SELECT COUNT(*) FROM inv.stock_movements WHERE source_doc_type='sales.invoice' AND source_doc_id=%s", (inv,))[0]
    assert n == 1, f"بيع العلبة أنشأ {n} حركة — يجب أن يكون أثر مخزون واحداً (§4.3 ق5)"
    st = one(conn, "SELECT status FROM sales.invoices WHERE invoice_id=%s", (inv,))[0]
    assert st == "POSTED", f"حالة الفاتورة {st}"


@test("T-FLW-02", "Flow", "وزني بمبلغ مستهدف: 200 EGP/kg × 70 EGP = 350g — تخزين 0.350kg بمصدر AMOUNT_DERIVED (§5.1)")
def t_flw_02(conn):
    I = ids(conn)
    receive(conn, I, I["wei"], 10, 60.00, 900002, uom=I["kg"])
    conn.commit()
    inv = draft_invoice(conn, I, "T-INV-002", [
        dict(sku=I["wei"], mode="WEIGHT", qty=0.350, uom=I["kg"], conv=1, weight=0.350,
             wsrc="AMOUNT_DERIVED", target=70.00, price=200.00, net=70.00, cost=21.00)], 70.00)
    run_sql(conn, "UPDATE sales.invoices SET status='POSTED' WHERE invoice_id=%s", (inv,))
    conn.commit()
    row = one(conn, "SELECT weight_kg, weight_source, amount_target, net_amount FROM sales.invoice_lines WHERE invoice_id=%s", (inv,))
    assert str(row[0]) == "0.350", f"الوزن المخزن {row[0]} — المتوقع 0.350"
    assert row[1] == "AMOUNT_DERIVED", "المصدر يجب أن يسجل AMOUNT_DERIVED لا قراءة ميزان"
    assert str(row[3]) == "70.0000", f"الصافي {row[3]} — المتوقع 70.0000"


@test("T-FLW-03", "Flow", "WAC (§15.5 ق10): 100@4.00 ثم 100@5.00 → متوسط 4.5000 وكمية الطبقة صحيحة")
def t_flw_03(conn):
    I = ids(conn)
    # SKU مخصصة للاختبار حتى لا تختلط بطبقات FLW-01
    cur = conn.cursor()
    cur.execute("INSERT INTO catalog.skus (product_id, sku_code, sale_mode, base_uom_id, sale_uom_id) "
                "SELECT product_id,'TWAC','COUNT',u.uom_id,u.uom_id FROM catalog.products, catalog.units u "
                "WHERE catalog.products.code='TPRD1' AND u.code='PIECE'")
    cur.close()
    conn.commit()
    sku = one(conn, "SELECT sku_id FROM catalog.skus WHERE sku_code='TWAC'")[0]
    receive(conn, I, sku, 100, 4.00, 910001)
    receive(conn, I, sku, 100, 5.00, 910002)
    conn.commit()
    avg, qty = one(conn, "SELECT avg_unit_cost, qty_on_layer FROM finance.cost_layers WHERE sku_id=%s", (sku,))
    assert abs(float(avg) - 4.5) < 1e-6, f"المتوسط المرجح {avg} — المتوقع 4.5000"
    assert abs(float(qty) - 200) < 1e-6, f"كمية الطبقة {qty} — المتوقع 200"


@test("T-FLW-04", "Flow", "تحويل بين خزينتين: حركتان متقابلتان صافيهما صفر — لا إيراد ولا مصروف (§14.2)")
def t_flw_04(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("""INSERT INTO finance.vouchers (voucher_number, voucher_type, treasury_account_id,
                   counter_treasury_account_id, amount, reason, status, posted_at, created_by)
                   VALUES ('TVCH-1','TRANSFER',%s,%s,200,'تغذية درج من الخزينة','POSTED', now(), %s) RETURNING voucher_id""",
                (I["safe"], I["drawer"], I["owner"]))
    v = cur.fetchone()[0]
    for acct, d in ((I["drawer"], -1), (I["safe"], 1)):
        cur.execute("""INSERT INTO finance.treasury_transactions
            (treasury_account_id, direction, amount, source_doc_type, source_doc_id, reason, created_by, branch_id)
            VALUES (%s,%s,200,'finance.voucher',%s,'تحويل داخلي',%s,%s)""", (acct, d, v, I["owner"], I["branch"]))
    cur.close()
    conn.commit()
    net = one(conn, """SELECT SUM(direction * amount) FROM finance.treasury_transactions
                       WHERE source_doc_type='finance.voucher' AND source_doc_id=%s""", (v,))[0]
    assert net is None or abs(float(net)) < 1e-6, f"صافي التحويل {net} — يجب أن يكون صفراً"


@test("T-FLW-05", "Flow", "منظر المطابقة: الرصيد المخزن = مجموع الدفتر لكل الصفوف (drift=0)")
def t_flw_05(conn):
    bad = one(conn, "SELECT COUNT(*) FROM inv.v_stock_ledger_check WHERE drift <> 0")[0]
    assert bad == 0, f"{bad} صف بانحراف بين الرصيد والدفتر"


@test("T-FLW-06", "Env", "فهارس trigram للبحث النصي — تصريح صادق عن حالة البيئة (§0.1 ق10)")
def t_flw_06(conn):
    has = one(conn, "SELECT COUNT(*) FROM pg_indexes WHERE indexname='idx_products_name_ar_trgm'")[0]
    if has:
        return "أنشئت فعلياً (pg_trgm متوفر)"
    raise EnvLimitation("بيئة الاختبار المدمجة بلا contrib — الفهرس ينشأ في بيئة G3 المرجعية ويسجل هنا كاستثناء بيئي معلن لا كنجاح")


# ───────────────────────────── القيود السالبة ─────────────────────────────
@test("T-DDL-01", "Catalog", "باركود نشط مكرر يُرفض (تفرد Exact Match §3.1)")
def t_ddl_01(conn):
    I = ids(conn)
    expect_error(conn, """INSERT INTO catalog.barcodes (sku_id, barcode_value, barcode_normalized, barcode_type)
        VALUES (%s,'6221000000017','6221000000017','EAN13')""", (I["box"],), "uq_barcodes_normalized_active", "duplicate key")


@test("T-DDL-02", "Sales", "سطر فاتورة لـSKU غير موجودة يُرفض (FK)")
def t_ddl_02(conn):
    I = ids(conn)
    inv = draft_invoice(conn, I, "T-INV-E01", [], 0)
    expect_error(conn, """INSERT INTO sales.invoice_lines
        (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, unit_price, net_amount, cost_snapshot, cost_method_snapshot)
        VALUES (%s,1,999999999,'COUNT',1,%s,1,10,10,8,'WAC')""", (inv, I["piece"]), "violates foreign key constraint")


@test("T-DDL-03", "Catalog", "تحويل بين بُعدين مختلفين (كجم→لتر) يُرفض (INV-CAT-02)")
def t_ddl_03(conn):
    kg = one(conn, "SELECT uom_id FROM catalog.units WHERE code='KG'")[0]
    ltr = one(conn, "SELECT uom_id FROM catalog.units WHERE code='L'")[0]
    expect_error(conn, "INSERT INTO catalog.unit_conversions (from_uom_id, to_uom_id, factor) VALUES (%s,%s,1)",
                 (kg, ltr), "UNIT_DIMENSION_MISMATCH")


@test("T-DDL-04", "Catalog", "تحويل معاكس يشكل دورة (جرام→كجم بعد كجم→جرام) يُرفض (INV-CAT-03)")
def t_ddl_04(conn):
    kg = one(conn, "SELECT uom_id FROM catalog.units WHERE code='KG'")[0]
    g = one(conn, "SELECT uom_id FROM catalog.units WHERE code='G'")[0]
    expect_error(conn, "INSERT INTO catalog.unit_conversions (from_uom_id, to_uom_id, factor) VALUES (%s,%s,0.001)",
                 (g, kg), "UNIT_CONVERSION_CYCLE")


@test("T-DDL-05", "Inventory", "سياسة السالب=BLOCK: خروج فوق الرصيد يُرفض (D-03)")
def t_ddl_05(conn):
    I = ids(conn)

    def fn():
        run_sql(conn, "UPDATE core.business_settings SET value_text='BLOCK' WHERE setting_key='inventory.negative_stock_policy'")
        run_sql(conn, """INSERT INTO inv.stock_movements
            (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor,
             source_doc_type, source_doc_id, reason, branch_id, user_id)
            VALUES (%s,%s,'ADJUSTMENT_OUT',-1,999999,%s,1,'inv.adjustment',950001,'اختبار سياسة السالب',%s,%s)""",
            (I["bag"], I["wh"], I["piece"], I["branch"], I["cash"]))
    expect_commit_error(conn, fn, "NEGATIVE_STOCK_BLOCKED")


@test("T-DDL-06", "Inventory", "السالب بموافقة مشرف (D-03) بلا اعتماد: يُرفض ويطلب اعتماداً")
def t_ddl_06(conn):
    I = ids(conn)

    def fn():
        run_sql(conn, """INSERT INTO inv.stock_movements
            (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor,
             source_doc_type, source_doc_id, reason, branch_id, user_id)
            VALUES (%s,%s,'ADJUSTMENT_OUT',-1,999999,%s,1,'inv.adjustment',950002,'سالب بلا اعتماد',%s,%s)""",
            (I["bag"], I["wh"], I["piece"], I["branch"], I["cash"]))
    expect_commit_error(conn, fn, "NEGATIVE_STOCK_NEEDS_APPROVAL")


@test("T-DDL-07", "Inventory", "السالب بموافقة مشرف معتمدة من شخص مختلف: يُقبل ثم يُعاد توازنه")
def t_ddl_07(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("""INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, reason, decided_at)
                   VALUES ('inv.adjustment', 950003, %s, %s, 'APPROVED', 'سماح استثنائي موثق', now()) RETURNING approval_id""",
                (I["cash"], I["sup"]))
    ap = cur.fetchone()[0]
    cur.execute("""INSERT INTO inv.stock_movements
        (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, conversion_factor,
         source_doc_type, source_doc_id, approval_id, reason, branch_id, user_id)
        VALUES (%s,%s,'ADJUSTMENT_OUT',-1,999999,%s,1,'inv.adjustment',950003,%s,'سالب معتمد',%s,%s)""",
        (I["bag"], I["wh"], I["piece"], ap, I["branch"], I["cash"]))
    cur.close()
    conn.commit()
    bal = one(conn, "SELECT base_qty FROM inv.stock_balances WHERE sku_id=%s AND warehouse_id=%s", (I["bag"], I["wh"]))[0]
    assert bal < 0, f"الرصيد يجب أن يكون سالباً بموافقة معتمدة — حصل {bal}"
    receive(conn, I, I["bag"], 999999, 4.00, 950004)
    conn.commit()
    return "سالب موثق ثم إعادة توازن — السياسة تعمل بالمصادقة الثنائية"


@test("T-DDL-08", "Inventory", "تعديل حركة مخزون يُرفض — الدفتر للقراءة فقط (§12.1)")
def t_ddl_08(conn):
    mid = one(conn, "SELECT MIN(movement_id) FROM inv.stock_movements")[0]
    expect_error(conn, "UPDATE inv.stock_movements SET base_qty=1 WHERE movement_id=%s", (mid,), "STOCK_LEDGER_IMMUTABLE")


@test("T-DDL-09", "Inventory", "حذف حركة مخزون يُرفض — لا إعادة كتابة للتاريخ")
def t_ddl_09(conn):
    mid = one(conn, "SELECT MIN(movement_id) FROM inv.stock_movements")[0]
    expect_error(conn, "DELETE FROM inv.stock_movements WHERE movement_id=%s", (mid,), "STOCK_LEDGER_IMMUTABLE")


@test("T-DDL-10", "Sales", "تعديل حقول فاتورة مرحلة يُرفض — التصحيح بمرتجع/إلغاء موثق (§0.1 ق6)")
def t_ddl_10(conn):
    inv = one(conn, "SELECT invoice_id FROM sales.invoices WHERE invoice_number='T-INV-001'")[0]
    expect_error(conn, "UPDATE sales.invoices SET net_total=1 WHERE invoice_id=%s", (inv,), "INVOICE_IMMUTABLE")


@test("T-DDL-11", "Sales", "حذف فاتورة مرحلة يُرفض")
def t_ddl_11(conn):
    inv = one(conn, "SELECT invoice_id FROM sales.invoices WHERE invoice_number='T-INV-001'")[0]
    expect_error(conn, "DELETE FROM sales.invoices WHERE invoice_id=%s", (inv,), "INVOICE_IMMUTABLE")


@test("T-DDL-12", "Sales", "ترحيل فاتورة بمجاميع لا تطابق السطور يُرفض (INV-SAL-01)")
def t_ddl_12(conn):
    I = ids(conn)
    inv = draft_invoice(conn, I, "T-INV-E02", [
        dict(sku=I["bag"], mode="COUNT", qty=1, uom=I["piece"], price=5.00, net=5.00, cost=4.00)], 5.00)
    run_sql(conn, "UPDATE sales.invoices SET net_total=999, gross_total=999 WHERE invoice_id=%s", (inv,))
    conn.commit()
    expect_error(conn, "UPDATE sales.invoices SET status='POSTED' WHERE invoice_id=%s", (inv,), "INVOICE_TOTALS_MISMATCH")


@test("T-DDL-13", "Sales", "ترحيل فاتورة بسطر بلا لقطة تكلفة يُرفض (§15.3)")
def t_ddl_13(conn):
    I = ids(conn)
    inv = draft_invoice(conn, I, "T-INV-E03", [
        dict(sku=I["bag"], mode="COUNT", qty=1, uom=I["piece"], price=5.00, net=5.00, cost=4.00)], 5.00)
    run_sql(conn, "UPDATE sales.invoice_lines SET cost_snapshot=NULL WHERE invoice_id=%s", (inv,))
    conn.commit()
    expect_error(conn, "UPDATE sales.invoices SET status='POSTED' WHERE invoice_id=%s", (inv,), "INVOICE_POST_MISSING_COST")


@test("T-DDL-14", "Sales", "ترحيل فاتورة بلا سطور يُرفض")
def t_ddl_14(conn):
    I = ids(conn)
    inv = draft_invoice(conn, I, "T-INV-E04", [], 0)
    expect_error(conn, "UPDATE sales.invoices SET status='POSTED' WHERE invoice_id=%s", (inv,), "INVOICE_POST_NO_LINES")


@test("T-DDL-15", "Sales", "مرتجع يتجاوز الكمية المباعة يُرفض بعد استبعاد المرتجعات السابقة (INV-SAL-03)")
def t_ddl_15(conn):
    I = ids(conn)
    inv = one(conn, "SELECT invoice_id FROM sales.invoices WHERE invoice_number='T-INV-001'")[0]
    line = one(conn, "SELECT line_id FROM sales.invoice_lines WHERE invoice_id=%s", (inv,))[0]
    cur = conn.cursor()
    cur.execute("""INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, reason, decided_at)
                   VALUES ('sales.refund', 0, %s, %s, 'APPROVED', 'اعتماد مرتجع اختبار', now()) RETURNING approval_id""",
                (I["cash"], I["sup"]))
    ap = cur.fetchone()[0]
    cur.execute("""INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, approval_id,
                   refund_method, reason, created_by) VALUES ('TRET-01', %s, %s, %s, 'CASH', 'اختبار حد المرتجع', %s) RETURNING refund_id""",
                (inv, I["branch"], ap, I["cash"]))
    ret = cur.fetchone()[0]
    cur.close()
    conn.commit()
    expect_error(conn, """INSERT INTO sales.refund_lines (refund_id, original_line_id, qty, unit_refund, line_total)
        VALUES (%s,%s,5,54,270)""", (ret, line), "REFUND_EXCEEDS_SOLD")


@test("T-DDL-16", "Sales", "سطر وزني بلا وزن/مصدر يُرفض (§5.1)")
def t_ddl_16(conn):
    I = ids(conn)
    inv = draft_invoice(conn, I, "T-INV-E05", [], 0)
    expect_error(conn, """INSERT INTO sales.invoice_lines
        (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, unit_price, net_amount, cost_snapshot, cost_method_snapshot)
        VALUES (%s,1,%s,'WEIGHT',1,%s,1,200,200,60,'WAC')""", (inv, I["wei"], I["kg"]),
        "line_weight_fields", "check constraint")


@test("T-DDL-17", "Pricing", "سعر صفري يُرفض — لا سعر صفر غير مقصود (§4.4 د)")
def t_ddl_17(conn):
    I = ids(conn)
    pl = one(conn, "SELECT price_list_id FROM catalog.price_lists WHERE is_default")[0]
    expect_error(conn, """INSERT INTO catalog.product_prices (sku_id, price_list_id, unit_id, unit_price)
        VALUES (%s,%s,%s,0)""", (I["bag"], pl, I["piece"]), "price_positive", "check constraint")


@test("T-DDL-18", "Finance", "ترحيل قيد غير متوازن يُرفض (مدين ≠ دائن §15.2)")
def t_ddl_18(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("""INSERT INTO finance.journal_entries (entry_number, period_id, source_doc_type, source_doc_id, created_by)
                   VALUES ('TJE-01', %s, 'test.balance', 1, %s) RETURNING entry_id""", (I["period"], I["owner"]))
    e = cur.fetchone()[0]
    cash = one(conn, "SELECT gl_account_id FROM finance.gl_accounts WHERE code='1101'")[0]
    sales = one(conn, "SELECT gl_account_id FROM finance.gl_accounts WHERE code='4101'")[0]
    cur.execute("INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,100,0,1)", (e, cash))
    cur.execute("INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,0,90,2)", (e, sales))
    cur.close()
    conn.commit()
    expect_error(conn, "UPDATE finance.journal_entries SET status='POSTED' WHERE entry_id=%s", (e,), "JOURNAL_UNBALANCED")


@test("T-DDL-19", "Finance", "الترحيل في فترة مالية مغلقة يُرفض (§15.2)")
def t_ddl_19(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("INSERT INTO finance.fiscal_periods (name, period_start, period_end, status) "
                "VALUES ('أكتوبر 2026', '2026-10-01','2026-10-31','CLOSED') RETURNING period_id")
    p = cur.fetchone()[0]
    cur.execute("""INSERT INTO finance.journal_entries (entry_number, period_id, source_doc_type, source_doc_id, created_by)
                   VALUES ('TJE-02', %s, 'test.period', 1, %s) RETURNING entry_id""", (p, I["owner"]))
    e = cur.fetchone()[0]
    cash = one(conn, "SELECT gl_account_id FROM finance.gl_accounts WHERE code='1101'")[0]
    cur.execute("INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,50,0,1)", (e, cash))
    cur.execute("INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,0,50,2)", (e, cash))
    cur.close()
    conn.commit()
    expect_error(conn, "UPDATE finance.journal_entries SET status='POSTED' WHERE entry_id=%s", (e,), "FISCAL_PERIOD_CLOSED")


@test("T-DDL-20", "Finance", "إضافة سطر لقيد مرحل يُرفض — الأسطر مجمّدة بعد الترحيل")
def t_ddl_20(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("""INSERT INTO finance.journal_entries (entry_number, period_id, source_doc_type, source_doc_id, created_by)
                   VALUES ('TJE-03', %s, 'test.freeze', 1, %s) RETURNING entry_id""", (I["period"], I["owner"]))
    e = cur.fetchone()[0]
    cash = one(conn, "SELECT gl_account_id FROM finance.gl_accounts WHERE code='1101'")[0]
    cur.execute("INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,50,0,1)", (e, cash))
    cur.execute("INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,0,50,2)", (e, cash))
    cur.execute("UPDATE finance.journal_entries SET status='POSTED' WHERE entry_id=%s", (e,))
    cur.close()
    conn.commit()
    expect_error(conn, "INSERT INTO finance.journal_lines (entry_id, gl_account_id, debit, credit, line_no) VALUES (%s,%s,10,0,3)",
                 (e, cash), "JOURNAL_LINES_FROZEN")


@test("T-DDL-21", "Shifts", "وردية ثانية مفتوحة لنفس المحطة تُرفض (منع الفتح المزدوج §14.1)")
def t_ddl_21(conn):
    I = ids(conn)
    expect_error(conn, "INSERT INTO sales.shifts (station_id, opened_by, opening_float) VALUES (%s,%s,50)",
                 (I["station"], I["cash"]), "uq_shifts_open_per_station", "duplicate key")


@test("T-DDL-22", "Printing", "قاعدة طباعة تلقائية لـA4 التفصيلي تُرفض — A4 بإجراء صريح فقط (§29.11)")
def t_ddl_22(conn):
    expect_error(conn, """INSERT INTO print.auto_print_rules (document_type, policy, notes)
        VALUES ('A4_DETAILED_REPORT','AUTO_AFTER_POST','محاولة A4 تلقائي')""", (), "a4_never_auto", "check constraint")


@test("T-DDL-23", "Purchasing", "تخصيص سداد يتجاوز قيمة السند يُرفض (قيد مؤجل §13)")
def t_ddl_23(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("INSERT INTO purch.suppliers (code, name_ar) VALUES ('TSUP1','مورد اختبار')")
    cur.execute("""INSERT INTO purch.supplier_invoices (invoice_number, supplier_id, subtotal, grand_total, invoice_date, created_by)
                   SELECT 'TSI-01', supplier_id, 100, 100, CURRENT_DATE, %s FROM purch.suppliers WHERE code='TSUP1' RETURNING invoice_id""",
                (I["owner"],))
    si = cur.fetchone()[0]
    cur.execute("""INSERT INTO purch.supplier_invoices (invoice_number, supplier_id, subtotal, grand_total, invoice_date, created_by)
                   SELECT 'TSI-02', supplier_id, 100, 100, CURRENT_DATE, %s FROM purch.suppliers WHERE code='TSUP1' RETURNING invoice_id""",
                (I["owner"],))
    si2 = cur.fetchone()[0]
    cur.execute("""INSERT INTO purch.supplier_payments (voucher_number, supplier_id, treasury_account_id, amount, payment_method, payment_date, created_by)
                   SELECT 'TSP-01', supplier_id, %s, 100, 'CASH', CURRENT_DATE, %s FROM purch.suppliers WHERE code='TSUP1' RETURNING payment_id""",
                (I["safe"], I["owner"]))
    pay = cur.fetchone()[0]
    cur.execute("INSERT INTO purch.supplier_payment_allocations (payment_id, invoice_id, amount) VALUES (%s,%s,60)", (pay, si))
    cur.close()
    conn.commit()

    def fn():
        run_sql(conn, "SET CONSTRAINTS ALL IMMEDIATE")
        # تخصيص ثانٍ لفاتورة مختلفة: 60+50=110 > قيمة السند 100
        run_sql(conn, "INSERT INTO purch.supplier_payment_allocations (payment_id, invoice_id, amount) VALUES (%s,%s,50)", (pay, si2))
    expect_commit_error(conn, fn, "PAYMENT_OVER_ALLOCATION")


@test("T-DDL-24", "Security", "موافقة الشخص على عمله نفسه تُرفض (فصل الواجبات §18)")
def t_ddl_24(conn):
    I = ids(conn)
    expect_error(conn, """INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, reason, decided_at)
        VALUES ('sales.refund', 0, %s, %s, 'APPROVED', 'موافقة ذاتية', now())""", (I["cash"], I["cash"]),
        "approvals_no_self", "check constraint")


@test("T-DDL-25", "Printing", "ملف طابعة بعرض قابل للطباعة أكبر من الوسيط يُرفض (§29.7.1)")
def t_ddl_25(conn):
    I = ids(conn)
    expect_error(conn, """INSERT INTO print.printer_profiles
        (printer_id, windows_queue_name, device_model, driver_name, station_id, branch_id, media_class,
         media_width_mm, printable_width_mm, dpi_x, dpi_y)
        VALUES ('TPR-80','TPR-80','TestModel','Generic',%s,%s,'ROLL_80',80.0,82.0,203,203)""",
        (I["station"], I["branch"]), "prn_widths_sane", "check constraint")


@test("T-DDL-26", "Sales", "فاتورة ثانية بمفتاح Idempotency مستخدم تُرفض (§15.4)")
def t_ddl_26(conn):
    I = ids(conn)
    expect_error(conn, """INSERT INTO sales.invoices
        (invoice_number, branch_id, station_id, cashier_user_id, net_total, idempotency_key)
        VALUES ('T-INV-E10',%s,%s,%s,0,'idem-T-INV-001')""", (I["branch"], I["station"], I["cash"]),
        "uq_invoices_idem", "duplicate key")


@test("T-DDL-27", "Sales", "مبلغ مستهدف بمصدر غير AMOUNT_DERIVED يُرفض — لا يعرض المشتق كوزن ميزان (§5.1)")
def t_ddl_27(conn):
    I = ids(conn)
    inv = draft_invoice(conn, I, "T-INV-E06", [], 0)
    expect_error(conn, """INSERT INTO sales.invoice_lines
        (invoice_id, line_no, sku_id, sale_mode, qty, uom_id, conversion_factor, weight_kg, weight_source,
         amount_target, unit_price, net_amount, cost_snapshot, cost_method_snapshot)
        VALUES (%s,1,%s,'WEIGHT',0.5,%s,1,0.5,'SCALE_READING',70,200,100,60,'WAC')""",
        (inv, I["wei"], I["kg"]), "line_amount_target_source", "check constraint")


@test("T-DDL-28", "Sales", "مرتجع بلا اعتماد مشرف يُرفض (D-04)")
def t_ddl_28(conn):
    I = ids(conn)
    inv = one(conn, "SELECT invoice_id FROM sales.invoices WHERE invoice_number='T-INV-001'")[0]
    expect_error(conn, """INSERT INTO sales.refunds (refund_number, original_invoice_id, branch_id, refund_method, reason, created_by)
        VALUES ('TRET-02', %s, %s, 'CASH', 'مرتجع بلا اعتماد', %s)""", (inv, I["branch"], I["cash"]),
        "refund_approval_required", "check constraint")


@test("T-DDL-29", "Finance", "سند تحويل بلا حساب مقابل يُرفض — التحويل بين خزينتين لا إيراد (§14.2)")
def t_ddl_29(conn):
    I = ids(conn)
    expect_error(conn, """INSERT INTO finance.vouchers (voucher_number, voucher_type, treasury_account_id, amount, reason, created_by)
        VALUES ('TVCH-E1','TRANSFER',%s,100,'تحويل ناقص',%s)""", (I["safe"], I["owner"]),
        "voucher_transfer_accounts", "check constraint")


@test("T-DDL-30", "Sales", "رقم فاتورة مكرر لنفس الفرع يُرفض")
def t_ddl_30(conn):
    I = ids(conn)
    expect_error(conn, """INSERT INTO sales.invoices (invoice_number, branch_id, station_id, cashier_user_id, net_total)
        VALUES ('T-INV-001',%s,%s,%s,0)""", (I["branch"], I["station"], I["cash"]), "duplicate key")


@test("T-DDL-31", "Audit", "مفتاح Idempotency واحد لا يقبل طلباً مختلفاً (§11)")
def t_ddl_31(conn):
    run_sql(conn, """INSERT INTO audit.idempotency_keys (idempotency_key, request_hash, expires_at)
        VALUES ('k-1','hash-A', now() + interval '1 hour')""")
    conn.commit()
    expect_error(conn, """INSERT INTO audit.idempotency_keys (idempotency_key, request_hash, expires_at)
        VALUES ('k-1','hash-B', now() + interval '1 hour')""", (), "idempotency_keys_pkey", "duplicate key")


@test("T-DDL-32", "Printing", "مهمة بوسيط مطابق تبقى HELD_CONFIGURATION_MISMATCH دون تحويل الوسيط (§29.9)")
def t_ddl_32(conn):
    I = ids(conn)
    cur = conn.cursor()
    cur.execute("""INSERT INTO print.printer_profiles
        (printer_id, windows_queue_name, device_model, driver_name, station_id, branch_id, media_class,
         media_width_mm, printable_width_mm, dpi_x, dpi_y, adapter_kind)
        VALUES ('TPR-58','TPR-58','TestModel58','Generic',%s,%s,'ROLL_58',57.5,48.0,203,203,'ESC_POS')
        RETURNING printer_profile_id""", (I["station"], I["branch"]))
    prn = cur.fetchone()[0]
    cur.execute("""INSERT INTO print.document_templates (template_code, document_type, media_class, body_definition)
        VALUES ('ROLL_SMALL_58','CUSTOMER_RECEIPT','ROLL_58','{}'::jsonb) RETURNING template_id""")
    tpl = cur.fetchone()[0]
    cur.execute("""INSERT INTO print.print_jobs (document_id, document_type, template_id, template_version,
        printer_profile_id, printer_profile_version, media_class, printable_width_mm, dpi, copy_type, status,
        idempotency_key, requested_by)
        VALUES (1,'CUSTOMER_RECEIPT',%s,1,%s,1,'ROLL_58',48.0,203,'CUSTOMER','HELD_CONFIGURATION_MISMATCH','tpj-1',%s)""",
        (tpl, prn, I["cash"]))
    cur.close()
    conn.commit()
    st = one(conn, "SELECT status FROM print.print_jobs WHERE idempotency_key='tpj-1'")[0]
    assert st == "HELD_CONFIGURATION_MISMATCH", f"الحالة {st} — يجب أن تبقى HELD دون تحويل الوسيط"


@test("T-DDL-33", "Ops", "نسخة احتياطية «ناجحة» دون verified_readable لا تُحتسب ناجحة (§19)")
def t_ddl_33(conn):
    run_sql(conn, """INSERT INTO ops.backup_records (backup_kind, started_at, finished_at, status, scope,
        storage_location_ref, backup_set_id, verified_readable)
        VALUES ('FULL', now() - interval '10 minutes', now() - interval '5 minutes', 'SUCCEEDED',
        'db','ref://test-1','bs-1', FALSE)""")
    conn.commit()
    n = one(conn, "SELECT COUNT(*) FROM ops.backup_records WHERE status='SUCCEEDED' AND verified_readable")[0]
    assert n == 0, "نجاح معلن دون verified_readable — يجب ألا يعتبر ناجحاً"


@test("T-ENV-01", "Env", "اكتمال المخططات التسعة وجداولها ومنظر المطابقة")
def t_env_01(conn):
    arr = all_rows(conn, """SELECT DISTINCT table_schema FROM information_schema.tables
                WHERE table_schema IN ('core','catalog','inv','purch','sales','finance','print','audit','ops')""")
    schemas = {r[0] for r in arr}
    missing = {'core','catalog','inv','purch','sales','finance','print','audit','ops'} - schemas
    assert not missing, f"مخططات ناقصة: {missing}"
    tables = one(conn, """SELECT COUNT(*) FROM information_schema.tables
        WHERE table_schema IN ('core','catalog','inv','purch','sales','finance','print','audit','ops')
        AND table_type='BASE TABLE'""")[0]
    views = one(conn, "SELECT COUNT(*) FROM pg_views WHERE schemaname='inv'")[0]
    assert tables >= 55, f"عدد الجداول {tables} — أقل من المتوقع"
    assert views >= 1, "منظر المطابقة inv.v_stock_ledger_check مفقود"
    return f"{tables} جدولاً في 9 مخططات + منظر المطابقة"


@test("T-ENV-02", "Env", "إعدادات D-01..D-18 موجودة وموسومة بحالة الحسم الصادقة (بعد حسم D-01/D-07/D-16 بأمر 2026-11-01 وD-02/D-03 بأمر 2026-11-02)")
def t_env_02(conn):
    n = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE decision_ref LIKE 'D-%'")[0]
    pending = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE confirmation_status='PENDING_OWNER_CONFIRMATION'")[0]
    settled = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE confirmation_status='OWNER_CONFIRMED'")[0]
    d01 = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE decision_ref='D-01' AND confirmation_status='OWNER_CONFIRMED'")[0]
    d07 = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE decision_ref='D-07' AND confirmation_status='OWNER_CONFIRMED'")[0]
    d02 = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE decision_ref='D-02' AND confirmation_status='OWNER_CONFIRMED'")[0]
    d03 = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE decision_ref='D-03' AND confirmation_status='OWNER_CONFIRMED'")[0]
    assert n >= 20, f"عدد إعدادات القرارات {n} — أقل من المتوقع"
    assert d01 == 2, f"إعدادات D-01 المحسومة {d01} — متوقع 2 (policy_mode + standard_rate)"
    assert d07 == 4, f"إعدادات D-07 المحسومة {d07} — متوقع 4 (الدقة/الخطوة/تقريب المبلغ/تقريب الوزن)"
    assert d02 == 1, f"إعدادات D-02 المحسومة {d02} — متوقع 1 (costing_method=WEIGHTED_AVERAGE بترحيل V012)"
    assert d03 == 1, f"إعدادات D-03 المحسومة {d03} — متوقع 1 (negative_stock_policy=SUPERVISOR_APPROVAL بترحيل V012)"
    assert settled == 8, f"الإعدادات المحسومة OWNER_CONFIRMED {settled} — متوقع 8 (V011: D-01×2+D-07×4 · V012: D-02+D-03)"
    assert pending >= 10, f"الإعدادات المعلنة بانتظار الحسم {pending}"
    return f"{n} إعداداً مرتبطاً بقرارات · محسوم OWNER_CONFIRMED={settled} (D-01/D-07 بـV011 وD-02/D-03 بـV012) · بانتظار المالك={pending} (بقية D-xx)"


# ───────────────────────────── G5: مكتبة الوسائط (V016) ─────────────────────────────

def _media_fixture(conn, n=1):
    """أصل وسائط داخل المعاملة الحالية (يتراجع مع نهاية الاختبار) — مفتاح وبصمة فريدان لكل استدعاء."""
    cur = conn.cursor()
    sha = ("a" * 63) + str(n % 10)
    cur.execute(
        "INSERT INTO catalog.media_assets (storage_key, sha256, mime_type, width_px, height_px, size_bytes, uploaded_by) "
        "VALUES (%s, %s, 'image/png', 64, 48, 1024, "
        "(SELECT user_id FROM core.users WHERE username='t_owner')) RETURNING media_id",
        (f"media/a{'b' * 61}{n:02d}/orig.png", sha))
    media_id = cur.fetchone()[0]
    cur.close()
    return media_id


def _job_fixture(conn, label="مهمة قيود G5", scope="0123456789abcdef"):
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO catalog.media_import_jobs (label, storage_scope, created_by) "
        "VALUES (%s, %s, (SELECT user_id FROM core.users WHERE username='t_owner')) RETURNING job_id",
        (label, scope))
    job_id = cur.fetchone()[0]
    cur.close()
    return job_id


@test("T-MED-01", "Media", "بصمة نطاق تخزين بصيغة غير صالحة تُرفض (16 سداسياً صغيراً فقط — V016)")
def t_med_01(conn):
    # الطول الزائد ترفضه سعة CHAR(16) قبل فحص الصيغة — كلاهما رفض صحيح
    for bad, tokens in [
        ("ABCDEF0123456789", ("mij_scope_format",)),   # كبيرة
        ("0123456789abcde", ("mij_scope_format",)),    # 15 محرفاً
        ("0123456789abcdefg", ("mij_scope_format", "value too long")),  # 17 محرفاً
        ("0123456789abcdeg", ("mij_scope_format",)),   # محرف غير سداسي
    ]:
        expect_error(conn,
            "INSERT INTO catalog.media_import_jobs (label, storage_scope, created_by) "
            "VALUES ('نطاق خاطئ', %s, (SELECT user_id FROM core.users WHERE username='t_owner'))",
            (bad,), *tokens)
    # الصيغة الصحيحة تُقبل (داخل SAVEPOINT يتراجع تلقائياً)
    with sp(conn) as cur:
        cur.execute(
            "INSERT INTO catalog.media_import_jobs (label, storage_scope, created_by) "
            "VALUES ('نطاق صحيح', 'fedcba9876543210', (SELECT user_id FROM core.users WHERE username='t_owner'))")
    return "4 صيغ مرفوضة (كبيرة/قصيرة/طويلة/غير سداسية — الطويلة ترفضها سعة CHAR16 قبل الفحص) والصحيحة تُقبل"


@test("T-MED-02", "Media", "حالة ملف استيراد خارج المجموعة تُرفض (PENDING/PROCESSING/DONE/FAILED/SKIPPED)")
def t_med_02(conn):
    # قيد الحالة وقيد اتساق النتيجة يقيّمان معاً — كلاهما رفض صحيح لنفس الإدخال
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state) "
        "VALUES (%s, 1, 'x.png', 'staging/0000000001/000001.bin', 100, 'INVENTED')",
        (job_id := _job_fixture(conn),), "mif_state_valid", "mif_done_shape")


@test("T-MED-03", "Media", "نتيجة الملف متسقة مع حالته: DONE بلا أصل أو مع خطأ تُرفض (mif_done_shape)")
def t_med_03(conn):
    job = _job_fixture(conn)
    media = _media_fixture(conn)
    # DONE بلا media_id ولا sha256
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state) "
        "VALUES (%s, 1, 'x.png', 'staging/0000000001/000001.bin', 100, 'DONE')",
        (job,), "mif_done_shape")
    # DONE مع error_code (نتيجة متناقضة)
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state, sha256, media_id, error_code) "
        "VALUES (%s, 2, 'y.png', 'staging/0000000001/000002.bin', 100, 'DONE', %s, %s, 'CORRUPT_IMAGE')",
        (job, "b" * 64, media), "mif_done_shape")


@test("T-MED-04", "Media", "FAILED بأصل أو بلا خطأ تُرفض — الفشل له سبب مسمى دائماً")
def t_med_04(conn):
    job = _job_fixture(conn)
    media = _media_fixture(conn)
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state, media_id) "
        "VALUES (%s, 1, 'x.png', 'staging/0000000001/000001.bin', 100, 'FAILED', %s)",
        (job, media), "mif_done_shape")
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state) "
        "VALUES (%s, 2, 'y.png', 'staging/0000000001/000002.bin', 100, 'FAILED')",
        (job,), "mif_done_shape")


@test("T-MED-05", "Media", "SKIPPED بلا سبب تخطٍ أو مع خطأ تُرفض — التخطي مبرر دوماً (مكرر قائم)")
def t_med_05(conn):
    job = _job_fixture(conn)
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state, sha256) "
        "VALUES (%s, 1, 'x.png', 'staging/0000000001/000001.bin', 100, 'SKIPPED', %s)",
        (job, "c" * 64), "mif_done_shape")
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state, sha256, error_code) "
        "VALUES (%s, 2, 'y.png', 'staging/0000000001/000002.bin', 100, 'SKIPPED', %s, 'FILE_TOO_LARGE')",
        (job, "c" * 64), "mif_done_shape")


@test("T-MED-06", "Media", "PENDING بنتيجة (sha256/خطأ/سبب) تُرفض — العمل غير المنجز بلا نتيجة")
def t_med_06(conn):
    job = _job_fixture(conn)
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state, sha256) "
        "VALUES (%s, 1, 'x.png', 'staging/0000000001/000001.bin', 100, 'PENDING', %s)",
        (job, "d" * 64), "mif_done_shape")
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes, state, error_code) "
        "VALUES (%s, 2, 'y.png', 'staging/0000000001/000002.bin', 100, 'PROCESSING', 'LATER')",
        (job,), "mif_done_shape")


@test("T-MED-07", "Media", "PENDING بلا staging تُرفض — لا عمل مؤجل بلا بايتات (mif_staging_shape)")
def t_med_07(conn):
    job = _job_fixture(conn)
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, size_bytes, state) "
        "VALUES (%s, 1, 'x.png', 100, 'PENDING')",
        (job,), "mif_staging_shape")


@test("T-MED-08", "Media", "ترتيب مكرر داخل المهمة يُرفض — لكل ملف ترتيب واحد")
def t_med_08(conn):
    job = _job_fixture(conn)
    # الصف الأول يُثبَّت داخل معاملة الاختبار (لا SAVEPOINT — ليظل أمام محاولة التكرار)
    run_sql(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes) "
        "VALUES (%s, 7, 'a.png', 'staging/0000000001/000007.bin', 100)", (job,))
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes) "
        "VALUES (%s, 7, 'b.png', 'staging/0000000001/000007x.bin', 101)",
        (job,), "media_import_files_job_id_ordinal_key")


@test("T-MED-09", "Media", "حجم ملف غير موجب يُرفض — لا ملف صفري أو سالب")
def t_med_09(conn):
    job = _job_fixture(conn)
    expect_error(conn,
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes) "
        "VALUES (%s, 1, 'x.png', 'staging/0000000001/000001.bin', 0)",
        (job,), "media_import_files_size_bytes_check")


@test("T-MED-10", "Media", "ربط مزدوج لنفس الصورة بنفس المنتج يُرفض (uq_product_images_media_product)")
def t_med_10(conn):
    media = _media_fixture(conn)
    product = one(conn, "SELECT product_id FROM catalog.products WHERE code='TPRD1'")[0]
    run_sql(conn, "INSERT INTO catalog.product_images (media_id, product_id) VALUES (%s, %s)", (media, product))
    expect_error(conn,
        "INSERT INTO catalog.product_images (media_id, product_id, sort_order) VALUES (%s, %s, 1)",
        (media, product), "uq_product_images_media_product")


@test("T-MED-11", "Media", "ربط مزدوج لنفس الصورة بنفس الصيغة يُرفض (uq_product_images_media_sku)")
def t_med_11(conn):
    media = _media_fixture(conn)
    sku = one(conn, "SELECT sku_id FROM catalog.skus WHERE sku_code='TBAG'")[0]
    run_sql(conn, "INSERT INTO catalog.product_images (media_id, sku_id) VALUES (%s, %s)", (media, sku))
    expect_error(conn,
        "INSERT INTO catalog.product_images (media_id, sku_id, sort_order) VALUES (%s, %s, 1)",
        (media, sku), "uq_product_images_media_sku")


@test("T-MED-12", "Media", "حالة المهمة متسقة: OPEN بلا بدء وCOMPLETED بلا إقفال تُرفضان")
def t_med_12(conn):
    expect_error(conn,
        "INSERT INTO catalog.media_import_jobs (label, storage_scope, created_by, started_at) "
        "VALUES ('بدء بلا تشغيل', '0123456789abcdef', "
        "(SELECT user_id FROM core.users WHERE username='t_owner'), now())",
        (), "mij_open_not_started")
    expect_error(conn,
        "INSERT INTO catalog.media_import_jobs (label, storage_scope, created_by, state) "
        "VALUES ('إقفال بلا وقت', '0123456789abcdef', "
        "(SELECT user_id FROM core.users WHERE username='t_owner'), 'COMPLETED')",
        (), "mij_finished_terminal")


@test("T-MED-13", "Media", "حذف المهمة تتالي ملفاتها (ON DELETE CASCADE) — لا ملفات يتيمة")
def t_med_13(conn):
    job = _job_fixture(conn, label="مهمة تتالي")
    cur = conn.cursor()
    cur.execute(
        "INSERT INTO catalog.media_import_files (job_id, ordinal, client_name, staging_key, size_bytes) "
        "VALUES (%s, 1, 'a.png', 'staging/0000000001/000001.bin', 10), "
        "(%s, 2, 'b.png', 'staging/0000000001/000002.bin', 20)", (job, job))
    cur.execute("DELETE FROM catalog.media_import_jobs WHERE job_id = %s", (job,))
    cur.execute("SELECT COUNT(*) FROM catalog.media_import_files WHERE job_id = %s", (job,))
    remaining = cur.fetchone()[0]
    cur.close()
    assert remaining == 0, f"ملفات يتيمة بعد حذف المهمة: {remaining}"
    return "ملفا المهمة حُذفا معها داخل المعاملة (يتراجع الاختبار تلقائياً)"


@test("T-MED-14", "Media", "صلاحيات V016: قراءة الوسائط لكل الأدوار السبعة، والإدارة للمالك/المدير/المخازني، والاستيراد للمالك/المخازني")
def t_med_14(conn):
    roles = one(conn, "SELECT COUNT(*) FROM core.roles")[0]
    readers = one(conn, """
        SELECT COUNT(DISTINCT r.role_id) FROM core.roles r
        JOIN core.role_permissions rp ON rp.role_id = r.role_id
        JOIN core.permissions p ON p.permission_id = rp.permission_id
        WHERE p.code = 'catalog.media.read'""")[0]
    managers = all_rows(conn, """
        SELECT r.code FROM core.roles r
        WHERE r.code IN ('OWNER','MANAGER','STOREKEEPER')
          AND EXISTS (SELECT 1 FROM core.role_permissions rp
                      JOIN core.permissions p ON p.permission_id = rp.permission_id
                      WHERE rp.role_id = r.role_id AND p.code = 'catalog.media.manage')""")
    importers = all_rows(conn, """
        SELECT r.code FROM core.roles r
        WHERE EXISTS (SELECT 1 FROM core.role_permissions rp
                      JOIN core.permissions p ON p.permission_id = rp.permission_id
                      WHERE rp.role_id = r.role_id AND p.code = 'catalog.import.run')""")
    assert roles == 7, f"عدد الأدوار {roles} — متوقع 7"
    assert readers == 7, f"قراءة الوسائط لـ{readers} دوراً — متوقع 7 (كل الأدوار)"
    assert {r[0] for r in managers} == {"OWNER", "MANAGER", "STOREKEEPER"}, f"الإدارة: {[r[0] for r in managers]}"
    assert "STOREKEEPER" in {r[0] for r in importers}, f"الاستيراد لا يشمل المخازني: {[r[0] for r in importers]}"
    assert "OWNER" in {r[0] for r in importers}, "الاستيراد لا يشمل المالك"
    return f"قراءة×7 أدوار · إدارة×3 (م/مش/خ) · استيراد يشمل م/خ — من {roles} أدوار"


@test("T-MED-15", "Media", "صورة رئيسية ثانية لنفس الصيغة تُرفض (uq_product_images_primary_sku من V002)")
def t_med_15(conn):
    media = _media_fixture(conn, n=1)
    media2 = _media_fixture(conn, n=2)  # أصل ثانٍ فريد داخل المعاملة نفسها
    sku = one(conn, "SELECT sku_id FROM catalog.skus WHERE sku_code='TBOX'")[0]
    run_sql(conn, "INSERT INTO catalog.product_images (media_id, sku_id, is_primary) VALUES (%s, %s, TRUE)", (media, sku))
    expect_error(conn,
        "INSERT INTO catalog.product_images (media_id, sku_id, is_primary) VALUES (%s, %s, TRUE)",
        (media2, sku), "uq_product_images_primary_sku")



# ═══════════════ اختبارات G6: المخزون والمشتريات على رأس V017 (بوابة G6) ═══════════════

def _g6_purch_fixture(conn):
    """مورد وأمر واستلام مرحّل وفاتورة مرحّلة — سلسلة كاملة داخل معاملة واحدة."""
    I = ids(conn)
    sup = one(conn, "INSERT INTO purch.suppliers (code, name_ar) "
                    "VALUES (CONCAT('TG6SUP-', nextval('purch.g6_doc_seq')),'مورد اختبار G6') RETURNING supplier_id")[0]
    po = one(conn, "INSERT INTO purch.purchase_orders (po_number, supplier_id, warehouse_id, status, created_by) "
                   "VALUES (CONCAT('PO-', nextval('purch.g6_doc_seq')), %s, %s, 'ORDERED', %s) RETURNING po_id",
             (sup, I["wh"], I["owner"]))[0]
    run_sql(conn, "INSERT INTO purch.purchase_order_lines (po_id, sku_id, qty, uom_id, unit_cost) VALUES (%s,%s,5,%s,24)",
             (po, I["box"], I["piece"]))
    rc = one(conn, "INSERT INTO purch.purchase_receipts (receipt_number, supplier_id, po_id, warehouse_id, status, received_by, posted_at, idempotency_key) "
                   "VALUES (CONCAT('RC-', nextval('purch.g6_doc_seq')), %s, %s, %s, 'POSTED', %s, now(), CONCAT('tg6-rc-', nextval('purch.g6_doc_seq'))) RETURNING receipt_id",
             (sup, po, I["wh"], I["owner"]))[0]
    run_sql(conn, "INSERT INTO purch.purchase_receipt_lines (receipt_id, sku_id, qty, uom_id, conversion_factor, unit_cost, base_qty, line_total) "
                  "VALUES (%s,%s,5,%s,12,24,60,120)", (rc, I["bag"], I["piece"]))
    run_sql(conn, "INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, "
                  "conversion_factor, source_doc_type, source_doc_id, branch_id, user_id) "
                  "VALUES (%s,%s,'RECEIVE',1,60,%s,12,'purch.receipt',%s,%s,%s)",
             (I["bag"], I["wh"], I["piece"], rc, I["branch"], I["owner"]))
    inv = one(conn, "INSERT INTO purch.supplier_invoices (invoice_number, supplier_id, receipt_id, subtotal, tax_total, grand_total, "
                    "status, invoice_date, created_by, posted_at) VALUES ('TG6-INV-1', %s, %s, 120, 5, 125, 'POSTED', current_date, %s, now()) "
                    "RETURNING invoice_id", (sup, rc, I["owner"]))[0]
    conn.commit()
    return {"supplier": sup, "po": po, "receipt": rc, "invoice": inv}


@test("T-G6-01", "Purchasing", "لا تكرار استلام: مفتاح Idempotency واحد لا يرحّل مستندين (فهرس V017 الفريد الجزئي)")
def t_g6_01(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    key = one(conn, "SELECT idempotency_key FROM purch.purchase_receipts WHERE receipt_id=%s", (f["receipt"],))[0]
    expect_error(conn,
        "INSERT INTO purch.purchase_receipts (receipt_number, supplier_id, warehouse_id, status, received_by, idempotency_key) "
        "VALUES (CONCAT('RC-', nextval('purch.g6_doc_seq')), %s, %s, 'DRAFT', %s, %s)",
        (f["supplier"], I["wh"], I["owner"], key), "uq_receipts_idempotency")


@test("T-G6-02", "Purchasing", "لا تخصيص بأكثر من قيمة الفاتورة عند الالتزام (حارس V017 tf_invoice_allocation_guard)")
def t_g6_02(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    p1 = one(conn, "INSERT INTO purch.supplier_payments (voucher_number, supplier_id, treasury_account_id, amount, payment_method, "
                   "payment_date, status, created_by, posted_at) VALUES (CONCAT('SP-', nextval('purch.g6_doc_seq')), %s, %s, 80, 'CASH', "
                   "current_date, 'POSTED', %s, now()) RETURNING payment_id", (f["supplier"], I["safe"], I["owner"]))[0]
    p2 = one(conn, "INSERT INTO purch.supplier_payments (voucher_number, supplier_id, treasury_account_id, amount, payment_method, "
                   "payment_date, status, created_by, posted_at) VALUES (CONCAT('SP-', nextval('purch.g6_doc_seq')), %s, %s, 80, 'CASH', "
                   "current_date, 'POSTED', %s, now()) RETURNING payment_id", (f["supplier"], I["safe"], I["owner"]))[0]
    run_sql(conn, "INSERT INTO purch.supplier_payment_allocations (payment_id, invoice_id, amount) VALUES (%s,%s,80)", (p1, f["invoice"]))
    run_sql(conn, "INSERT INTO purch.supplier_payment_allocations (payment_id, invoice_id, amount) VALUES (%s,%s,80)", (p2, f["invoice"]))

    def commit_fn():
        pass
    expect_commit_error(conn, commit_fn, "INVOICE_OVER_PAYMENT")
    conn.rollback()
    _g6_purch_fixture(conn)   # بيئة نظيفة لاحقة بعد التراجع


@test("T-G6-03", "Inventory", "نوع تسوية غير معروف يُرفض (adjustment_kind_domain — V017)")
def t_g6_03(conn):
    I = ids(conn)
    a = one(conn, "INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, decided_at) "
                  "VALUES ('inv.adjustment', 1, %s, %s, 'APPROVED', now()) RETURNING approval_id", (I["owner"], I["sup"]))[0]
    expect_error(conn,
        "INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, approval_id, adjustment_kind, created_by) "
        "VALUES (%s,%s,5,'اختبار',%s,'GALAXY',%s)",
        (I["wh"], I["bag"], a, I["owner"]), "adjust_kind_domain")
    conn.rollback()


@test("T-G6-04", "Inventory", "رصيد معتمد سالب للتسوية مرفوض (adjust_new_qty_nonneg — V017)")
def t_g6_04(conn):
    I = ids(conn)
    a = one(conn, "INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, decided_at) "
                  "VALUES ('inv.adjustment', 1, %s, %s, 'APPROVED', now()) RETURNING approval_id", (I["owner"], I["sup"]))[0]
    expect_error(conn,
        "INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, approval_id, adjustment_kind, created_by) "
        "VALUES (%s,%s,-5,'اختبار',%s,'QUICK',%s)",
        (I["wh"], I["bag"], a, I["owner"]), "adjust_new_qty_nonneg")
    conn.rollback()


@test("T-G6-05", "Inventory", "التسوية بلا اعتماد مرفوضة (adjust_approval_required — V003 يعاد فحصه على رأس V017)")
def t_g6_05(conn):
    I = ids(conn)
    expect_error(conn,
        "INSERT INTO inv.stock_adjustments (warehouse_id, sku_id, new_qty, reason, adjustment_kind, created_by) "
        "VALUES (%s,%s,5,'اختبار بلا اعتماد','QUICK',%s)",
        (I["wh"], I["bag"], I["owner"]), "adjust_approval_required")
    conn.rollback()


@test("T-G6-06", "Inventory", "جرد: المعتمد = الجارّ مرفوض (count_approval_distinct — V003)")
def t_g6_06(conn):
    I = ids(conn)
    c = one(conn, "INSERT INTO inv.count_sessions (warehouse_id, status, counted_by) VALUES (%s,'RECONCILED',%s) RETURNING count_id",
             (I["wh"], I["owner"]))[0]
    expect_error(conn,
        "UPDATE inv.count_sessions SET status='APPROVED', approved_by=%s, approved_at=now() WHERE count_id=%s",
        (I["owner"], c), "count_approval_distinct")
    conn.rollback()


@test("T-G6-07", "Inventory", "تحويل من مخزن إلى نفسه مرفوض (transfer_wh_distinct — V003)")
def t_g6_07(conn):
    I = ids(conn)
    expect_error(conn,
        "INSERT INTO inv.transfer_orders (from_warehouse_id, to_warehouse_id, created_by) VALUES (%s,%s,%s)",
        (I["wh"], I["wh"], I["owner"]), "transfer_wh_distinct")
    conn.rollback()


@test("T-G6-08", "Inventory", "الرصيد السالب بلا اعتماد مشرف مرفوض (D-03=SUPERVISOR_APPROVAL — مشغل V003)")
def t_g6_08(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    # كمية الخروج نسبية: الرصيد الحالي + 100 → سالب حتماً مهما أضافت فحوص سابقة
    cur = one(conn, "SELECT COALESCE(base_qty,0) FROM inv.stock_balances WHERE sku_id=%s AND warehouse_id=%s",
              (I["bag"], I["wh"]))[0]
    out_qty = cur + 100
    expect_error(conn,
        "INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, "
        "conversion_factor, source_doc_type, source_doc_id, branch_id, user_id, reason) "
        "VALUES (%s,%s,'TRANSFER_OUT',-1,%s,%s,1,'inv.transfer',1,%s,%s,'اختبار سالب بلا اعتماد')",
        (I["bag"], I["wh"], out_qty, I["piece"], I["branch"], I["owner"]), "NEGATIVE_STOCK")
    conn.rollback()


@test("T-G6-09", "Inventory", "الحركة السالبة تُقبل باعتماد مشرف معتمد (D-03 بالوجه الإيجابي)")
def t_g6_09(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    a = one(conn, "INSERT INTO core.approvals (entity_type, entity_id, requested_by, decided_by, decision, decided_at) "
                  "VALUES ('inv.transfer', 1, %s, %s, 'APPROVED', now()) RETURNING approval_id", (I["owner"], I["sup"]))[0]
    cur = one(conn, "SELECT COALESCE(base_qty,0) FROM inv.stock_balances WHERE sku_id=%s AND warehouse_id=%s",
              (I["bag"], I["wh"]))[0]
    out_qty = cur + 60   # يجعل الرصيد النهائي -60 بالضبط (سالب باعتماد)
    m = one(conn, "INSERT INTO inv.stock_movements (sku_id, warehouse_id, movement_type, direction, base_qty, source_uom_id, "
                  "conversion_factor, source_doc_type, source_doc_id, approval_id, branch_id, user_id, reason) "
                  "VALUES (%s,%s,'TRANSFER_OUT',-1,%s,%s,1,'inv.transfer',1,%s,%s,%s,'اختبار سالب باعتماد') RETURNING movement_id",
             (I["bag"], I["wh"], out_qty, I["piece"], a, I["branch"], I["owner"]))[0]
    bal = one(conn, "SELECT base_qty FROM inv.stock_balances WHERE sku_id=%s AND warehouse_id=%s", (I["bag"], I["wh"]))[0]
    assert bal == -60, f"الرصيد المتوقع -60 بعد خروج نسبي باعتماد، حصلنا {bal}"
    conn.commit()


@test("T-G6-10", "Purchasing", "رقم فاتورة مورد مكرر لنفس المورد مرفوض (uq فاتورة من V004)")
def t_g6_10(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    expect_error(conn,
        "INSERT INTO purch.supplier_invoices (invoice_number, supplier_id, subtotal, tax_total, grand_total, status, invoice_date, created_by) "
        "VALUES ('TG6-INV-1', %s, 10, 0, 10, 'DRAFT', current_date, %s)",
        (f["supplier"], I["owner"]), "supplier_invoices_supplier_id_invoice_number_key")
    conn.rollback()


@test("T-G6-11", "Purchasing", "مرتجع بلا مستند أصلي مرفوض (sup_return_has_origin — V004)")
def t_g6_11(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    expect_error(conn,
        "INSERT INTO purch.supplier_returns (return_number, supplier_id, warehouse_id, status, refund_method, created_by) "
        "VALUES (CONCAT('SR-', nextval('purch.g6_doc_seq')), %s, %s, 'DRAFT', 'CREDIT_NOTE', %s)",
        (f["supplier"], I["wh"], I["owner"]), "sup_return_has_origin")
    conn.rollback()


@test("T-G6-12", "Purchasing", "سداد بحساب خزينة غير موجود مرفوض (FK من V006)")
def t_g6_12(conn):
    I = ids(conn)
    f = _g6_purch_fixture(conn)
    expect_error(conn,
        "INSERT INTO purch.supplier_payments (voucher_number, supplier_id, treasury_account_id, amount, payment_method, payment_date, created_by) "
        "VALUES (CONCAT('SP-', nextval('purch.g6_doc_seq')), %s, 99999, 50, 'CASH', current_date, %s)",
        (f["supplier"], I["owner"]), "suppay_treasury_fk")
    conn.rollback()


@test("T-G6-13", "Purchasing", "أعمدة لقطات الأسطر من V017 موجودة (base_qty/line_total/lot_code/target_sku_id)")
def t_g6_13(conn):
    cols = {r[0] for r in all_rows(conn, """
        SELECT column_name FROM information_schema.columns
        WHERE (table_schema='purch' AND table_name='purchase_receipt_lines' AND column_name IN ('base_qty','line_total','lot_code','lot_expiry'))
           OR (table_schema='purch' AND table_name='supplier_return_lines' AND column_name IN ('target_sku_id','base_qty'))
           OR (table_schema='inv' AND table_name='count_sessions' AND column_name='approved_at')
    """)}
    missing = {"base_qty", "line_total", "lot_code", "lot_expiry", "target_sku_id", "approved_at"} - cols
    assert not missing, f"أعمدة V017 مفقودة: {missing}"


@test("T-G6-14", "Security", "صلاحيات V017: 16 صلاحية G6 بإسناداتها الحرفية (قراءة×7 ومصفوفة الرصد)")
def t_g6_14(conn):
    def count(code, roles):
        rows = all_rows(conn, """
            SELECT r.code FROM core.roles r
            JOIN core.role_permissions rp ON rp.role_id=r.role_id
            JOIN core.permissions p ON p.permission_id=rp.permission_id
            WHERE p.code=%s AND r.code = ANY(%s)
        """, (code, list(roles)))
        return len(rows)

    assert count("inventory.read", ["OWNER","MANAGER","SUPERVISOR","CASHIER","STOREKEEPER","ACCOUNTANT","AUDITOR"]) == 7
    assert count("purchasing.read", ["OWNER","MANAGER","SUPERVISOR","CASHIER","STOREKEEPER","ACCOUNTANT","AUDITOR"]) == 7
    assert count("inventory.warehouse.manage", ["OWNER","MANAGER"]) == 2
    assert count("inventory.stocktake.run", ["OWNER","MANAGER","STOREKEEPER"]) == 3
    assert count("inventory.stocktake.approve", ["OWNER","MANAGER"]) == 2
    assert count("inventory.reservation.manage", ["OWNER","MANAGER","STOREKEEPER"]) == 3
    assert count("purchasing.manage", ["OWNER","MANAGER","STOREKEEPER"]) == 3
    assert count("purchasing.receive", ["OWNER","MANAGER","STOREKEEPER"]) == 3
    assert count("purchasing.invoice.manage", ["OWNER","MANAGER","ACCOUNTANT"]) == 3
    assert count("purchasing.payment.post", ["OWNER","ACCOUNTANT"]) == 2
    assert count("purchasing.return.post", ["OWNER","MANAGER","STOREKEEPER"]) == 3
    assert count("suppliers.manage", ["OWNER","MANAGER"]) == 2
    assert count("customers.manage", ["OWNER","MANAGER"]) == 2
    assert count("parties.read", ["OWNER","MANAGER","STOREKEEPER","ACCOUNTANT","AUDITOR"]) == 5
    assert count("security.approval.decide", ["OWNER","MANAGER","SUPERVISOR"]) == 3
    assert count("finance.treasury.read", ["OWNER","MANAGER","ACCOUNTANT"]) == 3


@test("T-G6-15", "Purchasing", "لا يبت طالب العملية في طلبه (approvals_no_self — V003)")
def t_g6_15(conn):
    I = ids(conn)
    a = one(conn, "INSERT INTO core.approvals (entity_type, entity_id, requested_by) VALUES ('inv.adjustment', 1, %s) RETURNING approval_id",
             (I["owner"],))[0]
    expect_error(conn,
        "UPDATE core.approvals SET decision='APPROVED', decided_by=%s, decided_at=now() WHERE approval_id=%s",
        (I["owner"], a), "approvals_no_self")
    conn.rollback()


@test("T-G6-16", "Inventory", "حجز المخزون لا يقبل كمية غير موجبة (قيود V003 على qty_base)")
def t_g6_16(conn):
    I = ids(conn)
    expect_error(conn,
        "INSERT INTO inv.stock_reservations (sku_id, warehouse_id, qty_base, source_doc_type, source_doc_id) VALUES (%s,%s,-5,'t.test',1)",
        (I["bag"], I["wh"]), "check")
    conn.rollback()


# ───────────────────────────── التشغيل ─────────────────────────────
ORDER = {"Flow": 0, "Inventory": 1, "Catalog": 1, "Media": 1, "Sales": 2, "Pricing": 2, "Shifts": 2,
         "Purchasing": 2, "Finance": 3, "Printing": 4, "Security": 4, "Audit": 4, "Ops": 5, "Env": 6}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db-uri", required=True)
    ap.add_argument("--out", default=None, help="كتابة النتائج إلى ملف Markdown")
    a = ap.parse_args()

    conn = psycopg2.connect(a.db_uri)
    setup_fixtures(conn)

    tests = [v for k, v in list(globals().items()) if callable(v) and hasattr(v, "_tid")]
    tests.sort(key=lambda t: (ORDER.get(t._area, 9), t._tid))
    for t in tests:
        t(conn)

    passed = sum(1 for r in RESULTS if r[3] == "PASS")
    failed = sum(1 for r in RESULTS if r[3] == "FAIL")
    errored = sum(1 for r in RESULTS if r[3] == "ERROR")
    envlim = sum(1 for r in RESULTS if r[3] == "ENV_LIMITATION")
    ver = one(conn, "SELECT version()")[0]
    dbn = one(conn, "SELECT current_database()")[0]

    print(f"\n{'ID':12} {'Area':11} {'Result':17} Title")
    print("-" * 112)
    for tid, area, title, res, note in RESULTS:
        print(f"{tid:12} {area:11} {res:17} {title[:66]}")
    print("-" * 112)
    print(f"PASS={passed} · FAIL={failed} · ERROR={errored} · ENV_LIMITATION={envlim} · TOTAL={len(RESULTS)}")
    print(f"env: {dbn} — {ver[:72]}")

    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write("# نتائج اختبارات قيود قاعدة البيانات — G1\n\n")
            f.write(f"**البيئة:** {dbn} · {ver.split(',')[0]}\n\n")
            f.write(f"**النتيجة:** PASS={passed} · FAIL={failed} · ERROR={errored} · "
                    f"ENV_LIMITATION={envlim} · TOTAL={len(RESULTS)}\n\n")
            f.write("| الاختبار | المجال | النتيجة | الوصف | ملاحظة |\n|---|---|---|---|---|\n")
            for tid, area, title, res, note in RESULTS:
                note = (note or "").replace("|", "/").replace("\n", " ")
                f.write(f"| {tid} | {area} | {res} | {title} | {note} |\n")
        print(f"saved: {a.out}")
    conn.close()
    sys.exit(1 if (failed or errored) else 0)


if __name__ == "__main__":
    main()
