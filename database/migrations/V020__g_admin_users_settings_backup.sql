-- ═══════════════════════════════════════════════════════════════════════════
-- V020 — G-ADMIN: إدارة المستخدمين والإعدادات والنسخ الاحتياطي (PAGE-47/51/52)
-- 1) توسيع ops.backup_records: من أنشأ النسخة + بصمة SHA-256 (سلسلة الإسناد §19)
-- 2) صلاحيات قراءة إدارية (عرض الصفحات لغير المنفذ) + صيانة وحذف كامل (حرج)
-- 3) إسناد الصلاحيات الجديدة للمالك (كل شيء) والمدير (القراءة الإدارية فقط)
-- ═══════════════════════════════════════════════════════════════════════════

-- ── 1) توسعة سجل النسخ الاحتياطي — من أنشأها وبصمة الملف ──
ALTER TABLE ops.backup_records
    ADD COLUMN IF NOT EXISTS created_by TEXT NOT NULL DEFAULT 'system',
    ADD COLUMN IF NOT EXISTS checksum_sha256 TEXT;

COMMENT ON COLUMN ops.backup_records.created_by IS 'من أنشأ النسخة (اسم الدخول) — سلسلة الإسناد';
COMMENT ON COLUMN ops.backup_records.checksum_sha256 IS 'بصمة الملف SHA-256 — تتحقق قبل الاستعادة (§19 قابلية القراءة)';

-- ── 2) الصلاحيات الجديدة ──
INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('security.users.read',   'security', 'عرض صفحة المستخدمين والأدوار (التعديل بصلاحية الإدارة)'),
    ('ops.backup.read',       'ops',      'عرض سجل النسخ الاحتياطية وإحصاءات الصيانة'),
    ('ops.system.cleanup',    'ops',      'تنظيف النظام: تدقيق قديم وجلسات معلقة ووسائط يتيمة'),
    ('ops.system.wipe',       'ops',      'الحذف الكامل لبيانات التشغيل (إجراء حرج بتأكيد نصي مزدوج)')
ON CONFLICT (code) DO NOTHING;

-- ── 3) الإسنادات: المالك كل شيء؛ المدير قراءة إدارية + تنظيف ──
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r, core.permissions p
WHERE r.code = 'OWNER'
ON CONFLICT DO NOTHING;

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r
JOIN core.permissions p ON p.code IN ('security.users.read', 'ops.backup.read')
WHERE r.code = 'MANAGER'
ON CONFLICT DO NOTHING;

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r
JOIN core.permissions p ON p.code IN ('security.users.read', 'ops.backup.read')
WHERE r.code = 'AUDITOR'
ON CONFLICT DO NOTHING;
