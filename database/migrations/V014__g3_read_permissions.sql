-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V014 · G3: صلاحيات قراءة الأساس لكل الأدوار
-- المرجع: GFM-CSharp 2.4.1 §18 (فهرس الصلاحيات الذري وأقل صلاحية) و§11.2.
--
-- الغرض: نقاط نهاية G3 (قراءة الإعدادات + الفروع/المحطات) تغذي واجهة كل
-- محطة (إعدادات الوزن D-07 لكل كاشير) — تحتاج صلاحية قراءة منفصلة عن
-- الصلاحية الكتابية ops.settings.manage (فصل القراءة عن التعديل).
-- تُمنح للأدوار السبعة كلها: كل مستخدم نشط يقرأ تكوين الواجهة من الخادم
-- (المصدر الوحيد للحقيقة) — أما التعديل فيبقى ops.settings.manage (مالك/إدارة).
-- إضافة صفوف مرجعية فقط (Forward Fix) — لا مساس بأي ترحيل مطبق.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('ops.settings.read',  'ops', 'قراءة إعدادات النشاط (تكوين الواجهة: الوزن/الضريبة/العرض) — للقراءة فقط'),
    ('ops.branches.read',  'ops', 'قراءة الفروع والمحطات النشطة (اختيار المحطة عند الدخول)')
ON CONFLICT (code) DO NOTHING;

-- منح الصلاحيتين لكل الأدوار السبعة (قراءة تكوين الواجهة حق لكل مستخدم نشط)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id
  FROM core.roles r
  JOIN core.permissions p ON p.code IN ('ops.settings.read', 'ops.branches.read')
ON CONFLICT DO NOTHING;

-- ═══ تحقق داخلي: كل الأدوار السبعة تملك صلاحيتي القراءة ═══
DO $$
DECLARE missing INTEGER;
BEGIN
    SELECT COUNT(*) INTO missing
      FROM core.roles r
     WHERE NOT EXISTS (
        SELECT 1 FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id
           AND p.code IN ('ops.settings.read', 'ops.branches.read')
           HAVING COUNT(*) = 2)
       OR (
        SELECT COUNT(*) FROM core.role_permissions rp
          JOIN core.permissions p ON p.permission_id = rp.permission_id
         WHERE rp.role_id = r.role_id
           AND p.code IN ('ops.settings.read', 'ops.branches.read')) < 2;
    IF missing > 0 THEN
        RAISE EXCEPTION 'V014 check failed: % roles missing read permissions', missing;
    END IF;
END $$;

COMMIT;
