-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V015 · G4: صلاحيات الكتالوج وترحيل الحركات + اسم صيغة البيع
-- المرجع: GFM-CSharp 2.4.1 §18 (فهرس الصلاحيات الذري) و§4 (الكتالوج) و§12 (دفتر الحركة).
--
-- الغرض (بوابة G4 — الكتالوج/SKU/الوحدات/العبوات/الباركود):
--   1) catalog.product.read: كل محطة تحتاج قراءة الكتالوج للبحث Exact Match
--      بالباركود/SKU/PLU (POS في G7 تبحث بالماسح) — صلاحية قراءة منفصلة عن
--      الصلاحيات الكتابية (catalog.product.create/edit وغيرها من V009).
--      تُمنح للأدوار السبعة كلها أسوة بنمط V014 (قراءة تكوين العمل حق كل مستخدم نشط).
--   2) inventory.movement.post: ترحيل حركة مخزون موثقة بمستند مصدر إلى الدفتر
--      (inv.stock_movements — Append-only). أول من يحتاجها G4 (إثبات تحويل
--      المخزون الصحيح لكل صيغة بيع: بيع/شراء/مرتجع/جرد) ثم تعيد G6/G7
--      استخدامها من مستنداتها. تُمنح لمن يرحّل المستندات: OWNER/MANAGER/STOREKEEPER.
--   3) catalog.skus.sku_name_ar: اسم صيغة البيع (كيس/علبة/كرتونة) لعرضه في محرر
--      المنتج وجدول الكتالوج — عمود عرضي قابل للفراغ؛ الهوية والتفرد يبقيان في sku_code.
-- إضافة أمامية فقط (Forward Fix) — لا مساس بأي ترحيل مطبق.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

ALTER TABLE catalog.skus ADD COLUMN IF NOT EXISTS sku_name_ar TEXT;
COMMENT ON COLUMN catalog.skus.sku_name_ar IS 'اسم صيغة البيع للعرض (كيس مفرد/علبة 12 كيساً) — قابل للفراغ؛ الهوية والتفرد في sku_code';

INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('catalog.product.read',    'catalog',   'قراءة الكتالوج والبحث بالباركود/SKU/PLU (Exact Match) — للقراءة فقط'),
    ('inventory.movement.post', 'inventory', 'ترحيل حركة مخزون موثقة بمستند مصدر إلى الدفتر (Append-only)')
ON CONFLICT (code) DO NOTHING;

-- 1) قراءة الكتالوج لكل الأدوار السبعة
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'catalog.product.read'
ON CONFLICT DO NOTHING;

-- 2) ترحيل حركات المخزون: المالك والمدير وأمين المخزن
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code = 'inventory.movement.post'
 WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
ON CONFLICT DO NOTHING;

-- ═══ تحقق داخلي: الأدوار السبعة كلها تقرأ الكتالوج، والثلاثة المخوّلة ترحّل ═══
DO $$
DECLARE
    missing_read INTEGER;
    missing_post INTEGER;
BEGIN
    SELECT COUNT(*) INTO missing_read
      FROM core.roles r
     WHERE NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id AND p.code = 'catalog.product.read');

    SELECT COUNT(*) INTO missing_post
      FROM core.roles r
     WHERE r.code IN ('OWNER', 'MANAGER', 'STOREKEEPER')
       AND NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id AND p.code = 'inventory.movement.post');

    IF missing_read > 0 THEN
        RAISE EXCEPTION 'V015 check failed: % roles missing catalog.product.read', missing_read;
    END IF;
    IF missing_post > 0 THEN
        RAISE EXCEPTION 'V015 check failed: % roles missing inventory.movement.post', missing_post;
    END IF;
END $$;

COMMIT;
