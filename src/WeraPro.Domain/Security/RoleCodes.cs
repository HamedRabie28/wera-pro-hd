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
}
