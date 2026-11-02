-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V010 · ترقية بعد مراجعة خطة الأداء (G1 Review)
-- 1) فهارس البحث النصي المحدود (pg_trgm) لأسماء المنتجات — المرحلة الأخيرة من
--    البحث المتدرج (§3.1: باركود دقيق ← SKU ← PLU ← نصي محدود بحد نتائج معلن)
-- 2) فهرس تغطية لمسار الماسح الساخن على الباركود النشط (Exact Match p95 ≤ 200ms)
-- 3) فهرس تقارير المبيعات على الفواتير المرحلة
-- 4) منظر مطابقة الرصيد المخزن مع دفتر الحركة — أداة التسوية والتشغيل (T-INV-002)
-- لا partitioning: وفق §3.1 لا يضاف إلا بإثبات فائدة بالقياس — خطة العتبات في DATABASE_SCHEMA.md
-- صدق البيئة (§0.1 ق10): إن غاب pg_trgm عن بيئة التطبيق يُسجل WARNING صريح
-- ويوسم الفحص ENVIRONMENT_LIMITATION — لا يعتبر ناجحاً بصمت.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- 1) فهارس البحث النصي الثلاثية (contrib قياسي في كل توزيعة إنتاج PostgreSQL)
DO $$
BEGIN
    BEGIN
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'PG_TRGM_UNAVAILABLE: بيئة التطبيق الحالية بلا contrib — يوسم الفحص ENVIRONMENT_LIMITATION ويعاد في بيئة G3 المرجعية الكاملة';
    END;

    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
        CREATE INDEX IF NOT EXISTS idx_products_name_ar_trgm
            ON catalog.products USING gin (name_ar gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS idx_products_name_en_trgm
            ON catalog.products USING gin (coalesce(name_en,'') gin_trgm_ops);
    ELSE
        RAISE WARNING 'TRGM_INDEXES_SKIPPED: لم تنشأ فهارس trigram — البحث النصي يعمل على B-tree (lower(name_ar)) حتى تجهيز بيئة كاملة';
    END IF;
END $$;

-- 2) فهرس تغطية مسار الماسح: القيمة المنمّطة النشطة + SKU والنوع داخل الفهرس
--    (Index Only Scan لأعلى نقطة تردد في النظام)
CREATE INDEX idx_barcodes_scanner_covering
    ON catalog.barcodes (barcode_normalized)
    INCLUDE (sku_id, barcode_type)
    WHERE status = 'ACTIVE';

-- 3) تقارير المبيعات المرحلة (التجميع حسب الفرع والوقت)
CREATE INDEX idx_invoices_posted_reporting
    ON sales.invoices (branch_id, posted_at DESC)
    WHERE status IN ('POSTED','PARTIAL_RETURN','FULL_RETURN');

-- 4) منظر مطابقة الرصيد مع الدفتر — أي انحراف يعني خللاً يستوجب تسوية موثقة
CREATE OR REPLACE VIEW inv.v_stock_ledger_check AS
SELECT b.sku_id,
       b.warehouse_id,
       b.base_qty                                        AS stored_balance,
       COALESCE(SUM(m.direction * m.base_qty), 0)        AS ledger_sum,
       b.base_qty - COALESCE(SUM(m.direction * m.base_qty), 0) AS drift
  FROM inv.stock_balances b
  LEFT JOIN inv.stock_movements m
         ON m.sku_id = b.sku_id AND m.warehouse_id = b.warehouse_id
 GROUP BY b.sku_id, b.warehouse_id, b.base_qty;

COMMENT ON VIEW inv.v_stock_ledger_check IS 'مطابقة الرصيد المخزن مع مجموع الدفتر — drift ≠ 0 يعني حركة مفقودة أو رصيد معدول يدوياً ويستوجب تسوية معتمدة';

COMMIT;
