-- ═══════════════════════════════════════════════════════════════════════════
-- WERA PRO HD — Migration V009 · بيانات مرجعية أولية: وحدات وتحويلات، أدوار
-- وصلاحيات، إعدادات النشاط (قيم توصية D-01..D-14 موسومة بانتظار الحسم)،
-- قواعد الطباعة التلقائية (§29.10)، دليل حسابات مرجعي، فترة مالية مفتوحة
-- هذه بيانات مرجعية/تهيئة لا بيانات تجريبية — لا صف تجريبي في أي جدول تشغيلي
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1) وحدات القياس والتحويلات التعريفية ────────────────────────────────────
INSERT INTO catalog.units (code, name_ar, dimension, is_base, decimals) VALUES
    ('PIECE', 'قطعة',      'COUNT',  TRUE,  0),
    ('KG',    'كيلوجرام',  'MASS',   FALSE, 3),
    ('G',     'جرام',      'MASS',   TRUE,  0),
    ('L',     'لتر',       'VOLUME', FALSE, 3),
    ('ML',    'مليلتر',    'VOLUME', TRUE,  0),
    ('M',     'متر',       'LENGTH', FALSE, 2),
    ('CM',    'سنتيمتر',   'LENGTH', TRUE,  0);

INSERT INTO catalog.unit_conversions (from_uom_id, to_uom_id, factor, is_fixed) VALUES
    ((SELECT uom_id FROM catalog.units WHERE code='KG'), (SELECT uom_id FROM catalog.units WHERE code='G'),  1000, TRUE),
    ((SELECT uom_id FROM catalog.units WHERE code='L'),  (SELECT uom_id FROM catalog.units WHERE code='ML'), 1000, TRUE),
    ((SELECT uom_id FROM catalog.units WHERE code='M'),  (SELECT uom_id FROM catalog.units WHERE code='CM'), 100,  TRUE);

-- ── 2) الأدوار وفهرس الصلاحيات وإسنادها (§18) ────────────────────────────────
INSERT INTO core.roles (code, name_ar, description) VALUES
    ('OWNER',        'صاحب المنتج/المالك', 'كل الصلاحيات بما فيها رأس المال والمستخدمون والفترات المالية'),
    ('MANAGER',      'مدير فرع',           'تشغيل الفرع كاملاً دون رأس المال وإدارة المستخدمين المركزية'),
    ('SUPERVISOR',   'مشرف وردية',         'اعتماد المرتجعات والخصومات فوق حد الكاشير والجرد والتسويات الصغيرة'),
    ('CASHIER',      'كاشير',              'نقطة البيع والسلال المعلقة وفتح الدرج الاعتيادي'),
    ('STOREKEEPER',  'أمين مخزن',          'الاستلام والتحويل والجرد والتسويات بموافقتها'),
    ('ACCOUNTANT',   'محاسب',              'الخزينة والسندات والمصروفات والقيود والتقارير المالية'),
    ('AUDITOR',      'مراجع (قراءة فقط)',  'قراءة السجلات والتقارير دون أي تعديل تشغيلي');

INSERT INTO core.permissions (code, domain_area, description_ar) VALUES
    ('pos.use',                  'sales', 'استخدام نقطة البيع وإتمام البيع'),
    ('sales.held_cart.use',      'sales', 'تعليق السلال واستعادتها'),
    ('sales.invoice.void',       'sales', 'إلغاء فاتورة مرحلة بسبب موثق'),
    ('sales.discount.upto_5',    'sales', 'خصم حتى 5% (الكاشير)'),
    ('sales.discount.upto_15',   'sales', 'خصم حتى 15% (المشرف)'),
    ('sales.discount.over_15',   'sales', 'خصم فوق 15% (مدير/مالك فقط)'),
    ('sales.price.override',     'sales', 'تعديل سعر سطر بصلاحية موثقة'),
    ('sales.return.approve',     'sales', 'اعتماد مرتجع ورد الأموال'),
    ('sales.refund.cash_for_card', 'sales', 'رد نقدي لقيمة بطاقة (قرار استثنائي موثق)'),
    ('sales.credit.sell',        'sales', 'بيع آجل ضمن حد ائتمان العميل'),
    ('sales.drawer.open',        'sales', 'فتح درج النقد بغير حركة بيع (بسبب مسجل)'),
    ('catalog.product.create',   'catalog', 'إنشاء منتج وSKUs وعبوات'),
    ('catalog.product.edit',     'catalog', 'تعديل منتج قائم'),
    ('catalog.product.deactivate','catalog', 'تعطيل منتج (لا حذف تاريخي)'),
    ('catalog.barcode.manage',   'catalog', 'إدارة الباركود وPLU وملفات الوزن المتغير'),
    ('catalog.price.edit',       'catalog', 'تعديل القوائم السعرية والعروض'),
    ('catalog.media.upload',     'catalog', 'رفع الصور لمكتبة الوسائط'),
    ('catalog.import.run',       'catalog', 'تشغيل استيراد المنتجات/الصور الدفعي'),
    ('inventory.balance.view',   'inventory', 'عرض أرصدة المخزون وكرت الحركة'),
    ('inventory.count.conduct',  'inventory', 'تنفيذ جرد فعلي'),
    ('inventory.count.approve',  'inventory', 'اعتماد نتيجة الجرد'),
    ('inventory.adjustment.post','inventory', 'ترحيل تسوية مخزون معتمدة'),
    ('inventory.transfer.send',  'inventory', 'إرسال تحويل بين مخزنين'),
    ('inventory.transfer.receive','inventory', 'استلام تحويل'),
    ('inventory.damage.post',    'inventory', 'ترحيل هالك/تالف'),
    ('purchasing.po.create',     'purchasing', 'إنشاء أمر شراء'),
    ('purchasing.po.approve',    'purchasing', 'اعتماد أمر شراء'),
    ('purchasing.receipt.post',  'purchasing', 'ترحيل استلام مورد'),
    ('purchasing.supplier.manage','purchasing', 'إدارة بيانات الموردين'),
    ('purchasing.payment.post',  'purchasing', 'ترحيل سداد مورد'),
    ('purchasing.return.post',   'purchasing', 'ترحيل مرتجع مورد'),
    ('finance.treasury.view',    'finance', 'عرض أرصدة الخزائن وحركاتها'),
    ('finance.voucher.create',   'finance', 'إنشاء سند قبض/صرف/تحويل'),
    ('finance.voucher.post',     'finance', 'ترحيل السند (أثر مالي)'),
    ('finance.expense.approve',  'finance', 'اعتماد مصروف'),
    ('finance.expense.post',     'finance', 'ترحيل مصروف مسدد'),
    ('finance.capital.manage',   'finance', 'أحداث رأس المال والمساهمات والسحوبات'),
    ('finance.gl.manage',        'finance', 'إدارة دليل الحسابات'),
    ('finance.journal.post',     'finance', 'ترحيل قيود اليومية'),
    ('finance.period.close',     'finance', 'إقفال فترة مالية'),
    ('printing.profile.manage',  'printing', 'إدارة ملفات الطابعات وأدوارها'),
    ('printing.calibrate',       'printing', 'معايرة الوسيط واعتماد Test Receipt'),
    ('printing.a4_detailed',     'printing', 'طباعة تفصيلية كاملة A4 (إجراء صريح)'),
    ('printing.reprint',         'printing', 'إعادة طباعة موثقة بعلامة نسخة'),
    ('printing.queue.manage',    'printing', 'إدارة قائمة الطباعة وإعادة المحاولة'),
    ('printing.payroll.print',   'printing', 'طباعة مستندات الرواتب على طابعة مؤمنة'),
    ('security.users.manage',    'security', 'إدارة المستخدمين والأدوار والنطاقات'),
    ('security.audit.view',      'security', 'عرض سجل التدقيق والاعتمادات'),
    ('ops.backup.run',           'ops', 'تشغيل نسخة احتياطية'),
    ('ops.backup.restore',       'ops', 'استعادة نسخة احتياطية (إجراء حرج)'),
    ('ops.settings.manage',      'ops', 'تعديل إعدادات النشاط (الضريبة/التكلفة/الوزن/الحدود)'),
    ('ops.branches.manage',      'ops', 'إدارة الفروع والمحطات'),
    ('reports.sales.view',       'reports', 'تقارير المبيعات والورديات'),
    ('reports.financial.view',   'reports', 'التقارير المالية والأرباح والخسائر'),
    ('reports.analytics.view',   'reports', 'لوحات التحليلات وذكاء المبيعات'),
    ('reports.payroll.view',     'reports', 'تقارير الرواتب (خصوصية مشددة)');

-- إسناد الصلاحيات للأدوار (أقل صلاحية تفصل بين الأدوار — قابلة للتعديل من شاشة الصلاحيات)
INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r, core.permissions p WHERE r.code='OWNER';

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('pos.use','sales.held_cart.use','sales.invoice.void','sales.discount.upto_5','sales.return.approve',
                'sales.drawer.open','inventory.balance.view','catalog.product.create','catalog.product.edit',
                'catalog.barcode.manage','catalog.media.upload','inventory.count.conduct','inventory.count.approve',
                'inventory.transfer.send','inventory.transfer.receive','printing.reprint','reports.sales.view',
                'purchasing.po.create','purchasing.receipt.post','purchasing.supplier.manage')
 WHERE r.code='MANAGER';

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('pos.use','sales.held_cart.use','sales.discount.upto_15','sales.return.approve','sales.drawer.open',
                'inventory.balance.view','inventory.count.conduct','inventory.transfer.send','inventory.transfer.receive',
                'printing.reprint','reports.sales.view')
 WHERE r.code='SUPERVISOR';

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('pos.use','sales.held_cart.use','sales.discount.upto_5','sales.drawer.open','printing.reprint')
 WHERE r.code='CASHIER';

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('inventory.balance.view','inventory.count.conduct','inventory.transfer.send','inventory.transfer.receive',
                'inventory.damage.post','purchasing.po.create','purchasing.receipt.post','purchasing.return.post',
                'inventory.adjustment.post')
 WHERE r.code='STOREKEEPER';

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('finance.treasury.view','finance.voucher.create','finance.voucher.post','finance.expense.approve',
                'finance.expense.post','finance.gl.manage','finance.journal.post','purchasing.payment.post',
                'reports.sales.view','reports.financial.view','reports.analytics.view','printing.a4_detailed')
 WHERE r.code='ACCOUNTANT';

INSERT INTO core.role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM core.roles r JOIN core.permissions p
  ON p.code IN ('security.audit.view','reports.sales.view','reports.financial.view','reports.analytics.view','inventory.balance.view')
 WHERE r.code='AUDITOR';

-- ── 3) إعدادات النشاط — قيم التوصية موسومة بحالة الحسم (لا ثوابت في الكود) ───
INSERT INTO core.business_settings (setting_key, scope_type, value_text, value_type, category, decision_ref, confirmation_status, description_ar) VALUES
    ('currency.code',                'BUSINESS', 'EGP',                  'string',  'GENERAL',    NULL,   'APPROVED_WITH_SPEC',        'عملة النشاط الافتراضية — قابلة للتغيير كإعداد لا ثابت كود (§2 ق9)'),
    ('currency.decimals',            'BUSINESS', '2',                    'integer', 'GENERAL',    NULL,   'APPROVED_WITH_SPEC',        'منازل العرض والتقريب النقدي (قرش)'),
    ('tax.policy_mode',              'BUSINESS', 'VAT_INCLUSIVE',        'enum',    'TAX',        'D-01', 'PENDING_OWNER_CONFIRMATION','الأسعار المعروضة تشمل الضريبة ويفصلها النظام عند الترحيل (توصية D-01 أ)'),
    ('tax.standard_rate',            'BUSINESS', '0.1400',               'decimal', 'TAX',        'D-01', 'PENDING_OWNER_CONFIRMATION','نسبة الضريبة القياسية المرجعية (توصية D-01 أ — مصر)'),
    ('tax.einvoice_integration',     'BUSINESS', 'DISABLED',             'enum',    'TAX',        'D-01', 'APPROVED_WITH_SPEC',        'تكامل الفوترة الإلكترونية ETA مرحلة لاحقة منفصلة بعد اعتماد نطاقها'),
    ('inventory.costing_method',     'BUSINESS', 'WEIGHTED_AVERAGE',     'enum',    'INVENTORY',  'D-02', 'PENDING_OWNER_CONFIRMATION','المتوسط المرجح المتحرك لكل صيغ البيع مع لقطة تكلفة عند الترحيل (توصية D-02 أ)'),
    ('inventory.negative_stock_policy','BUSINESS','SUPERVISOR_APPROVAL', 'enum',    'INVENTORY',  'D-03', 'PENDING_OWNER_CONFIRMATION','سماح بالسالب بموافقة مشرف وسبب مسجل، مع إمكان تشديده لكل فرع/صنف (توصية D-03 ب)'),
    ('returns.approval_policy',      'BUSINESS', 'SUPERVISOR_ORIGINAL_TENDER','enum','RETURNS',   'D-04', 'PENDING_OWNER_CONFIRMATION','اعتماد مشرف لكل مرتجع والرد بوسيلة الدفع الأصلية (توصية D-04 أ)'),
    ('returns.cash_for_card_allowed','BUSINESS', 'false',                'boolean', 'RETURNS',    'D-04', 'PENDING_OWNER_CONFIRMATION','رد نقدي لقيمة بطاقة = قرار استثنائي موثق فقط (توصية D-04 أ)'),
    ('credit.enabled',               'BUSINESS', 'true',                 'boolean', 'CREDIT',     'D-05', 'PENDING_OWNER_CONFIRMATION','آجل لعملاء محددين بحد ائتمان وتاريخ استحقاق وموافقة صلاحية (توصية D-05 أ)'),
    ('pricing.discount_limits',      'BUSINESS', '{"cashier_pct":5,"supervisor_pct":15,"over_requires":"MANAGER"}','json','PRICING','D-06','PENDING_OWNER_CONFIRMATION','حدود الخصم: كاشير 5% · مشرف 15% · فوقها مدير/مالك بسبب موثق (توصية D-06 أ)'),
    ('weighing.precision_g',         'BUSINESS', '1',                    'integer', 'WEIGHT',     'D-07', 'PENDING_OWNER_CONFIRMATION','دقة الوزن 1g (توصية D-07 أ)'),
    ('weighing.step_g',              'BUSINESS', '5',                    'integer', 'WEIGHT',     'D-07', 'PENDING_OWNER_CONFIRMATION','خطوة الوزن 5g — أزرار الوزن السريع لا تظهر إن خالفت دقة الصنف (توصية D-07 أ)'),
    ('weighing.amount_rounding',     'BUSINESS', 'ROUND_HALF_EVEN_0.01', 'enum',    'WEIGHT',     'D-07', 'PENDING_OWNER_CONFIRMATION','تقريب المبلغ لأقرب قرش بقاعدة Banker''s Rounding (توصية D-07 أ)'),
    ('weighing.weight_rounding',     'BUSINESS', 'NEAREST_STEP',         'enum',    'WEIGHT',     'D-07', 'PENDING_OWNER_CONFIRMATION','تقريب الوزن الناتج من المبلغ إلى أقرب خطوة معتمدة (توصية D-07 أ)'),
    ('offline.mode',                 'BUSINESS', 'ONLINE_FIRST',         'enum',    'OFFLINE',    'D-08', 'PENDING_OWNER_CONFIRMATION','Online-first مع حالة اتصال حقيقية وقراءة Cache موسومة (توصية D-08 أ)'),
    ('retention.financial_years',    'BUSINESS', '7',                    'integer', 'RETENTION',  'D-09', 'PENDING_OWNER_CONFIRMATION','الاحتفاظ بالمستندات المالية 7 سنوات (تحقق نهائي من المستشار القانوني)'),
    ('retention.operational_months', 'BUSINESS', '13',                   'integer', 'RETENTION',  'D-09', 'PENDING_OWNER_CONFIRMATION','سجلات التشغيل التفصيلية 13 شهراً (جلسات/تشخيص أجهزة)'),
    ('payments.card_provider',       'BUSINESS', 'NOT_CONFIGURED',       'string',  'PAYMENTS',   'D-11', 'APPROVED_WITH_SPEC',        'البطاقة معطلة حتى اعتماد مزود وSandbox موثق — النقد وSplit نقد/نقد يعملان'),
    ('accounting.scope',             'BUSINESS', 'OPERATIONAL',          'enum',    'ACCOUNTING', 'D-12', 'PENDING_OWNER_CONFIRMATION','محاسبة تشغيلية في الإصدار الأول والقيد المزدوج جاهز للتفعيل (توصية D-12 ب)'),
    ('packaging.kind',               'BUSINESS', 'MSIX',                 'enum',    'PACKAGING',  'D-13', 'PENDING_OWNER_CONFIRMATION','تغليف MSIX مبدئياً مع اختبار فعلي للأجهزة (توصية D-13 أ)'),
    ('launch.reference_topology',    'BUSINESS', '1_SERVER_10_STATIONS', 'string',  'GENERAL',    'D-14', 'PENDING_OWNER_CONFIRMATION','طوبولوجيا القياس المرجعية: خادم واحد + 10 محطات (الأرقام الافتتاحية بقرار D-14)'),
    ('print.media_policy',           'BUSINESS', 'ROLL_80_MEDIUM__ROLL_58_SMALL__A4_DETAILED_ONLY','string','PRINTING',NULL,'APPROVED_WITH_SPEC','سياسة الوسائط الملزمة المعتمدة مع GFM 2.4.1 §29.2'),
    ('print.a4_policy',              'BUSINESS', 'EXPLICIT_ONLY',        'enum',    'PRINTING',   'D-16', 'APPROVED_WITH_SPEC',        'A4 تفصيلي بإجراء صريح مخول فقط — لا يظهر افتراضياً في أي تدفق ولا يتحول إليه تلقائياً (§29.11)');

-- ── 4) قواعد الطباعة التلقائية لكل نوع مستند (§29.10) ────────────────────────
INSERT INTO print.auto_print_rules (document_type, policy, printer_role, notes) VALUES
    ('CUSTOMER_RECEIPT',        'AUTO_AFTER_POST',      'CUSTOMER_RECEIPT',   'تلقائي بعد اعتماد Printer Profile للمحطة — قالب 80 أو 58 المستقل'),
    ('ADMIN_COPY',              'ELECTRONIC_ONLY',     NULL,                 'حفظ إلكتروني؛ الطباعة بإعداد إداري منفصل (§29.10)'),
    ('PURCHASE_INTERNAL_INVOICE','AUTO_AFTER_POST',    'SUPPLIER_DOCUMENTS', 'طباعة رول بعد الاعتماد/الترحيل على طابعة المالية (D-16 توصية أ — بانتظار الحسم النهائي)'),
    ('SUPPLIER_PAYMENT_VOUCHER','AUTO_AFTER_POST',      'FINANCE_VOUCHERS',   'بعد نجاح السداد وتسجيل السند على طابعة المالية المعتمدة'),
    ('SUPPLIER_STATEMENT',      'EXPLICIT_USER',       NULL,                 'طباعة عند الطلب؛ A4 فقط لو اختير التقرير الكامل'),
    ('PAYROLL_SLIP',            'EXPLICIT_USER',       'CONFIDENTIAL_PAYROLL','طباعة صريحة مخولة على طابعة مؤمنة فقط (اختبار 21)'),
    ('PAYROLL_PAYMENT_VOUCHER', 'AUTO_AFTER_POST',      'CONFIDENTIAL_PAYROLL','بعد نجاح السداد وفق سياسة الخصوصية والطابعة المؤمنة'),
    ('CASHIER_SALES_APPENDIX',  'EXPLICIT_USER',       NULL,                 'ملخص رول عند الطلب؛ التفصيلي الكامل A4 بطلب صريح'),
    ('EXPENSE_VOUCHER',         'AUTO_AFTER_POST',      'FINANCE_VOUCHERS',   'بعد الاعتماد أو السداد وفق حالة المستند'),
    ('EMPLOYEE_EXPENSE_CLAIM',  'EXPLICIT_USER',       NULL,                 'عند الإنشاء/الاعتماد باختيار المستخدم — لا تساوي سند دفع'),
    ('EMPLOYEE_COMPENSATION',   'AUTO_AFTER_POST',      'FINANCE_VOUCHERS',   'بعد نجاح عملية التعويض وتسجيل المرجع'),
    ('EXPENSE_CONTROL_REPORT',  'EXPLICIT_USER',       NULL,                 'عند الطلب فقط مع احترام صلاحيات البيانات'),
    ('A4_DETAILED_REPORT',      'EXPLICIT_A4_DETAILED','A4_DETAILED_REPORTS','إجراء صريح + صلاحية printing.a4_detailed + طابعة بدور A4 (§29.11)');

-- ── 5) دليل حسابات مرجعي (يبقى جاهزاً بحسب نطاق D-12) ────────────────────────
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('1',    'الأصول',                 'ASSET',      NULL, FALSE);
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('11',   'الأصول المتداولة',       'ASSET',      (SELECT gl_account_id FROM finance.gl_accounts WHERE code='1'), FALSE);
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('1101', 'النقدية بالخزائن',       'ASSET',      (SELECT gl_account_id FROM finance.gl_accounts WHERE code='11'), TRUE),
    ('1102', 'النقدية بالبنوك',        'ASSET',      (SELECT gl_account_id FROM finance.gl_accounts WHERE code='11'), TRUE),
    ('1103', 'مستحقات لدى مزودي الدفع','ASSET',      (SELECT gl_account_id FROM finance.gl_accounts WHERE code='11'), TRUE),
    ('1104', 'المخزون',                'ASSET',      (SELECT gl_account_id FROM finance.gl_accounts WHERE code='11'), TRUE),
    ('1105', 'ذمم العملاء (مدينة)',    'ASSET',      (SELECT gl_account_id FROM finance.gl_accounts WHERE code='11'), TRUE);
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('2',    'الخصوم',                 'LIABILITY',  NULL, FALSE);
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('21',   'الخصوم المتداولة',       'LIABILITY',  (SELECT gl_account_id FROM finance.gl_accounts WHERE code='2'), FALSE);
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('2101', 'ذمم الموردين (دائنة)',   'LIABILITY',  (SELECT gl_account_id FROM finance.gl_accounts WHERE code='21'), TRUE),
    ('2102', 'ضريبة القيمة المضافة المستحقة', 'LIABILITY', (SELECT gl_account_id FROM finance.gl_accounts WHERE code='21'), TRUE),
    ('2103', 'دفعات مقدمة من العملاء', 'LIABILITY',  (SELECT gl_account_id FROM finance.gl_accounts WHERE code='21'), TRUE);
INSERT INTO finance.gl_accounts (code, name_ar, account_type, parent_id, is_postable) VALUES
    ('3101', 'رأس مال المالك',         'EQUITY',     NULL, TRUE),
    ('3102', 'مسحوبات المالك',         'EQUITY',     NULL, TRUE),
    ('3103', 'الأرباح المحتجزة',       'EQUITY',     NULL, TRUE),
    ('4101', 'إيرادات المبيعات',       'REVENUE',    NULL, TRUE),
    ('4102', 'خصومات المبيعات (معاكس)','REVENUE',    NULL, TRUE),
    ('4103', 'مرتجعات المبيعات (معاكس)','REVENUE',   NULL, TRUE),
    ('5101', 'تكلفة البضاعة المباعة',  'EXPENSE',    NULL, TRUE),
    ('5201', 'الأجور والرواتب',        'EXPENSE',    NULL, TRUE),
    ('5202', 'الإيجارات',              'EXPENSE',    NULL, TRUE),
    ('5203', 'المرافق والكهرباء',      'EXPENSE',    NULL, TRUE),
    ('5204', 'مصروفات تشغيلية أخرى',   'EXPENSE',    NULL, TRUE);

-- ── 6) الفترة المالية المفتوحة والقائمة السعرية الافتراضية ────────────────────
INSERT INTO finance.fiscal_periods (name, period_start, period_end, status)
    VALUES ('نوفمبر 2026', DATE '2026-11-01', DATE '2026-11-30', 'OPEN');

INSERT INTO catalog.price_lists (code, name_ar, currency, is_default)
    VALUES ('DEFAULT_EGP', 'قائمة البيع الافتراضية', 'EGP', TRUE);

COMMIT;
