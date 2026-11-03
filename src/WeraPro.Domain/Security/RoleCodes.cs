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
}
