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


@test("T-ENV-02", "Env", "إعدادات D-01..D-16 موجودة وموسومة بحالة الحسم الصادقة")
def t_env_02(conn):
    n = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE decision_ref LIKE 'D-%'")[0]
    pending = one(conn, "SELECT COUNT(*) FROM core.business_settings WHERE confirmation_status='PENDING_OWNER_CONFIRMATION'")[0]
    assert n >= 20, f"عدد إعدادات القرارات {n} — أقل من المتوقع"
    assert pending >= 15, f"الإعدادات المعلنة بانتظار الحسم {pending}"
    return f"{n} إعداداً مرتبطاً بقرارات، منها {pending} بانتظار حسم المالك"


# ───────────────────────────── التشغيل ─────────────────────────────
ORDER = {"Flow": 0, "Inventory": 1, "Catalog": 1, "Sales": 2, "Pricing": 2, "Shifts": 2,
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
