namespace WeraPro.Domain.Security;

/// <summary>الأدوار المرجعية السبعة (V001 core.roles · V009 seed) — ثوابت مرجعية لا نصوص حرة.</summary>
public static class RoleCodes
{
    public const string Owner = "OWNER";
    public const string Manager = "MANAGER";
    public const string Supervisor = "SUPERVISOR";
    public const string Cashier = "CASHIER";
    public const string Storekeeper = "STOREKEEPER";
    public const string Accountant = "ACCOUNTANT";
    public const string Auditor = "AUDITOR";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Owner, Manager, Supervisor, Cashier, Storekeeper, Accountant, Auditor,
    };
}

/// <summary>صلاحيات أساس G3 (V009 seed + V014) وصلاحيات الكتالوج G4 (V009 + V015) — بقية الصلاحيات في بواباتها.</summary>
public static class PermissionCodes
{
    /// <summary>قراءة إعدادات النشاط — كل الأدوار (V014: تكوين الواجهة من الخادم).</summary>
    public const string SettingsRead = "ops.settings.read";

    /// <summary>قراءة الفروع والمحطات — كل الأدوار (V014).</summary>
    public const string BranchesRead = "ops.branches.read";

    /// <summary>عرض سجل التدقيق — OWNER وAUDITOR فقط (V009 seed).</summary>
    public const string AuditView = "security.audit.view";

    /// <summary>تعديل إعدادات النشاط — OWNER (V009 seed؛ شاشات الإعدادات G14).</summary>
    public const string SettingsManage = "ops.settings.manage";

    /// <summary>قراءة الكتالوج والبحث Exact Match بالباركود/SKU/PLU — كل الأدوار (V015).</summary>
    public const string CatalogProductRead = "catalog.product.read";

    /// <summary>إنشاء منتج وSKUs وعبوات — OWNER/MANAGER (V009 seed).</summary>
    public const string CatalogProductCreate = "catalog.product.create";

    /// <summary>تعديل منتج قائم — OWNER/MANAGER (V009 seed).</summary>
    public const string CatalogProductEdit = "catalog.product.edit";

    /// <summary>تعطيل منتج (لا حذف تاريخي) — OWNER/MANAGER (V009 seed).</summary>
    public const string CatalogProductDeactivate = "catalog.product.deactivate";

    /// <summary>إدارة الباركود وPLU وملفات الوزن المتغير — OWNER/MANAGER (V009 seed).</summary>
    public const string CatalogBarcodeManage = "catalog.barcode.manage";

    /// <summary>تعديل القوائم السعرية والعروض — OWNER/MANAGER (V009 seed).</summary>
    public const string CatalogPriceEdit = "catalog.price.edit";

    /// <summary>ترحيل حركة مخزون موثقة إلى الدفتر — OWNER/MANAGER/STOREKEEPER (V015).</summary>
    public const string InventoryMovementPost = "inventory.movement.post";

    /// <summary>قراءة مكتبة الوسائط ومصغرات الكتالوج/POS — كل الأدوار (V016).</summary>
    public const string CatalogMediaRead = "catalog.media.read";

    /// <summary>إدارة المكتبة: رفع/ربط/فك ربط/رئيسية/إعادة معالجة/حذف أصل — OWNER/MANAGER/STOREKEEPER (V016).</summary>
    public const string CatalogMediaManage = "catalog.media.manage";

    /// <summary>تشغيل استيراد المنتجات/الصور الدفعي — OWNER (V009) + STOREKEEPER (V016).</summary>
    public const string CatalogImportRun = "catalog.import.run";

    // ── صلاحيات G6 (V017 — المخزون والمشتريات والأطراف والاعتمادات) ──

    /// <summary>قراءة الأرصدة وكرت الحركة والنواقص والدفعات — كل الأدوار (V017: تكوين عمل).</summary>
    public const string InventoryRead = "inventory.read";

    /// <summary>إدارة المخازن والفروع الفرعية/الخارجية — OWNER/MANAGER (V017 · رصد PAGE-28).</summary>
    public const string InventoryWarehouseManage = "inventory.warehouse.manage";

    /// <summary>فتح جلسة جرد وتسجيل العد — OWNER/MANAGER/STOREKEEPER (V017 · رصد PAGE-20).</summary>
    public const string InventoryStocktakeRun = "inventory.stocktake.run";

    /// <summary>اعتماد تسوية الجرد — OWNER/MANAGER (V017؛ المعتمد ≠ الجارّ بقيد V003).</summary>
    public const string InventoryStocktakeApprove = "inventory.stocktake.approve";

    /// <summary>حجز المخزون وتحريره — OWNER/MANAGER/STOREKEEPER (V017 · §12.1 الحجز والمتاح).</summary>
    public const string InventoryReservationManage = "inventory.reservation.manage";

    /// <summary>قراءة المشتريات والاستلامات والفواتير — كل الأدوار (V017: تكوين عمل).</summary>
    public const string PurchasingRead = "purchasing.read";

    /// <summary>سلة المشتريات والمسودات وأوامر الشراء — OWNER/MANAGER/STOREKEEPER (V017 · رصد PAGE-24).</summary>
    public const string PurchasingManage = "purchasing.manage";

    /// <summary>ترحيل استلام المورد — OWNER/MANAGER/STOREKEEPER (V017 · رصد PAGE-25).</summary>
    public const string PurchasingReceive = "purchasing.receive";

    /// <summary>فواتير الموردين — OWNER/MANAGER/ACCOUNTANT (V017 · رصد PAGE-26).</summary>
    public const string PurchasingInvoiceManage = "purchasing.invoice.manage";

    /// <summary>سداد المورد بأثر خزينة واحد — OWNER/ACCOUNTANT (V017 · §12.3 ق3).</summary>
    public const string PurchasingPaymentPost = "purchasing.payment.post";

    /// <summary>مرتجع المورد — OWNER/MANAGER/STOREKEEPER (V017 · رصد PAGE-27).</summary>
    public const string PurchasingReturnPost = "purchasing.return.post";

    /// <summary>سجل الموردين — OWNER/MANAGER (V017 · رصد PAGE-31).</summary>
    public const string SuppliersManage = "suppliers.manage";

    /// <summary>سجل العملاء — OWNER/MANAGER (V017 · رصد PAGE-29).</summary>
    public const string CustomersManage = "customers.manage";

    /// <summary>قراءة الأطراف وكشوف الحساب — OWNER/MANAGER/STOREKEEPER/ACCOUNTANT/AUDITOR (V017).</summary>
    public const string PartiesRead = "parties.read";

    /// <summary>البت في طلبات الاعتماد — OWNER/MANAGER/SUPERVISOR (V017 · D-03 ب).</summary>
    public const string SecurityApprovalDecide = "security.approval.decide";

    /// <summary>قراءة حسابات الخزينة (إنشاؤها وإدارتها في G8) — OWNER/MANAGER/ACCOUNTANT (V017).</summary>
    public const string FinanceTreasuryRead = "finance.treasury.read";

    // ── صلاحيات G7 (V009 seed — POS والمبيعات والطباعة؛ التحقق من إسناداتها في V018) ──

    /// <summary>استخدام نقطة البيع وإتمام البيع — OWNER/MANAGER/SUPERVISOR/CASHIER (V009).</summary>
    public const string PosUse = "pos.use";

    /// <summary>تعليق السلال واستعادتها — OWNER/MANAGER/SUPERVISOR/CASHIER (V009).</summary>
    public const string SalesHeldCartUse = "sales.held_cart.use";

    /// <summary>إلغاء فاتورة مرحلة بسبب موثق — OWNER/MANAGER (V009).</summary>
    public const string SalesInvoiceVoid = "sales.invoice.void";

    /// <summary>خصم حتى 5% (الكاشير) — OWNER/MANAGER/CASHIER (V009 · D-06).</summary>
    public const string SalesDiscountUpto5 = "sales.discount.upto_5";

    /// <summary>خصم حتى 15% (المشرف) — OWNER/SUPERVISOR (V009 · D-06).</summary>
    public const string SalesDiscountUpto15 = "sales.discount.upto_15";

    /// <summary>خصم فوق 15% (مدير/مالك فقط) — OWNER (V009 · D-06).</summary>
    public const string SalesDiscountOver15 = "sales.discount.over_15";

    /// <summary>تعديل سعر سطر بصلاحية موثقة — OWNER (V009 · §9.4).</summary>
    public const string SalesPriceOverride = "sales.price.override";

    /// <summary>اعتماد مرتجع ورد الأموال — OWNER/MANAGER/SUPERVISOR (V009 · D-04).</summary>
    public const string SalesReturnApprove = "sales.return.approve";

    /// <summary>رد نقدي لقيمة بطاقة (قرار استثنائي موثق) — OWNER (V009 · D-04).</summary>
    public const string SalesRefundCashForCard = "sales.refund.cash_for_card";

    /// <summary>بيع آجل ضمن حد ائتمان العميل — OWNER (V009 · D-05).</summary>
    public const string SalesCreditSell = "sales.credit.sell";

    /// <summary>فتح درج النقد بغير حركة بيع بسبب مسجل — OWNER/MANAGER/SUPERVISOR/CASHIER (V009 · §9.9).</summary>
    public const string SalesDrawerOpen = "sales.drawer.open";

    /// <summary>إعادة طباعة موثقة بعلامة نسخة — OWNER/MANAGER/SUPERVISOR/CASHIER (V009 · §29.9).</summary>
    public const string PrintingReprint = "printing.reprint";

    /// <summary>إدارة قائمة الطباعة وإعادة المحاولة — OWNER (V009 · §29.9).</summary>
    public const string PrintingQueueManage = "printing.queue.manage";

    /// <summary>طباعة تفصيلية كاملة A4 بإجراء صريح — OWNER/ACCOUNTANT (V009 · §29.11).</summary>
    public const string PrintingA4Detailed = "printing.a4_detailed";

    // ── صلاحيات G8 (V019 — الورديات والنقد والأدراج والمصروفات ورأس المال) ──

    /// <summary>إغلاق الوردية بعد عدّ النقدية — OWNER/MANAGER/SUPERVISOR/CASHIER (V019 · §14.1).</summary>
    public const string SalesShiftClose = "sales.shift.close";

    /// <summary>اعتماد إغلاق وردية بفارق موثق السبب — OWNER/MANAGER/SUPERVISOR (V019 · §14.1 — الكاشير لا يعتمد فارقه).</summary>
    public const string SalesShiftApproveVariance = "sales.shift.approve_variance";

    /// <summary>إدارة حسابات الخزينة والأدراج والبنوك — OWNER/MANAGER (V019 · §14.2).</summary>
    public const string FinanceTreasuryManage = "finance.treasury.manage";

    /// <summary>تسجيل مصروف (ومصروف وردية مدفوع من درج المحطة) — OWNER/MANAGER/SUPERVISOR/CASHIER/ACCOUNTANT (V019 · §14.4).</summary>
    public const string FinanceExpenseCreate = "finance.expense.create";

    // ── صلاحيات G8 من بذر V009 (مزروعة بإسناداتها منذ G1 — يستخدمها كود G8) ──

    /// <summary>عرض أرصدة الخزائن وحركاتها — OWNER/ACCOUNTANT (V009 seed).</summary>
    public const string FinanceTreasuryView = "finance.treasury.view";

    /// <summary>إنشاء سند قبض/صرف/تحويل — OWNER/ACCOUNTANT (V009 seed).</summary>
    public const string FinanceVoucherCreate = "finance.voucher.create";

    /// <summary>ترحيل السند (أثر مالي) — OWNER/ACCOUNTANT (V009 seed).</summary>
    public const string FinanceVoucherPost = "finance.voucher.post";

    /// <summary>اعتماد مصروف — OWNER/ACCOUNTANT (V009 seed).</summary>
    public const string FinanceExpenseApprove = "finance.expense.approve";

    /// <summary>ترحيل مصروف مسدد — OWNER/ACCOUNTANT (V009 seed).</summary>
    public const string FinanceExpensePost = "finance.expense.post";

    /// <summary>أحداث رأس المال والمساهمات والسحوبات — OWNER (V009 seed).</summary>
    public const string FinanceCapitalManage = "finance.capital.manage";

    // ── صلاحيات G-ADMIN (V020 — المستخدمون والإعدادات والنسخ الاحتياطي والصيانة) ──

    /// <summary>عرض صفحة المستخدمين والأدوار — OWNER/MANAGER/AUDITOR (V020).</summary>
    public const string SecurityUsersRead = "security.users.read";

    /// <summary>إنشاء/تعديل/تعطيل مستخدم وإسناد الأدوار — OWNER (V009 seed).</summary>
    public const string SecurityUsersManage = "security.users.manage";

    /// <summary>عرض سجل النسخ الاحتياطية وإحصاءات الصيانة — OWNER/MANAGER/AUDITOR (V020).</summary>
    public const string BackupRead = "ops.backup.read";

    /// <summary>تشغيل نسخة احتياطية — OWNER (V009 seed).</summary>
    public const string BackupRun = "ops.backup.run";

    /// <summary>استعادة نسخة احتياطية (إجراء حرج) — OWNER (V009 seed).</summary>
    public const string BackupRestore = "ops.backup.restore";

    /// <summary>تنظيف النظام: تدقيق قديم وجلسات ووسائط يتيمة — OWNER (V020).</summary>
    public const string SystemCleanup = "ops.system.cleanup";

    /// <summary>الحذف الكامل لبيانات التشغيل (حرج بتأكيد نصي) — OWNER فقط (V020).</summary>
    public const string SystemWipe = "ops.system.wipe";
}
