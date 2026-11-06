namespace WeraPro.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>
/// سياق القراءة/الكتابة للمخططات التأسيسية (core + audit) — بوابات G4+ تضيف
/// مخططات أعمالها بنفس النمط. المخطط لا يُدار بترحيلات EF (ADR-009) بل بملفات
/// SQL الخطية عبر <see cref="Migrations.SqlMigrationRunner"/>؛ لذا
/// <c>Database.Migrate()</c> محظور الاستخدام هنا.
/// </summary>
public sealed class WeraDbContext : DbContext
{
    public WeraDbContext(DbContextOptions<WeraDbContext> options) : base(options) { }

    public DbSet<BranchRow> Branches => Set<BranchRow>();
    public DbSet<StationRow> Stations => Set<StationRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<RoleRow> Roles => Set<RoleRow>();
    public DbSet<PermissionRow> Permissions => Set<PermissionRow>();
    public DbSet<RolePermissionRow> RolePermissions => Set<RolePermissionRow>();
    public DbSet<BusinessSettingRow> BusinessSettings => Set<BusinessSettingRow>();
    public DbSet<UserSessionRow> UserSessions => Set<UserSessionRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();
    public DbSet<AuditLogRow> AuditLogs => Set<AuditLogRow>();

    // ── الكتالوج (G4 — V002/V015) ──
    public DbSet<CategoryRow> Categories => Set<CategoryRow>();
    public DbSet<BrandRow> Brands => Set<BrandRow>();
    public DbSet<UnitRow> Units => Set<UnitRow>();
    public DbSet<UnitConversionRow> UnitConversions => Set<UnitConversionRow>();
    public DbSet<ProductRow> Products => Set<ProductRow>();
    public DbSet<SkuRow> Skus => Set<SkuRow>();
    public DbSet<PackageRow> Packages => Set<PackageRow>();
    public DbSet<BarcodeRow> Barcodes => Set<BarcodeRow>();
    public DbSet<PluRow> Plus => Set<PluRow>();
    public DbSet<PriceListRow> PriceLists => Set<PriceListRow>();
    public DbSet<ProductPriceRow> ProductPrices => Set<ProductPriceRow>();

    // ── المخزون: الدفتر والمخازن (G4 — V003) ──
    public DbSet<WarehouseRow> Warehouses => Set<WarehouseRow>();
    public DbSet<StockMovementRow> StockMovements => Set<StockMovementRow>();

    // ── الوسائط ومهام الاستيراد (G5 — V002/V016) ──
    public DbSet<MediaAssetRow> MediaAssets => Set<MediaAssetRow>();
    public DbSet<ProductImageRow> ProductImages => Set<ProductImageRow>();
    public DbSet<MediaImportJobRow> MediaImportJobs => Set<MediaImportJobRow>();
    public DbSet<MediaImportFileRow> MediaImportFiles => Set<MediaImportFileRow>();

    // ── G6: المخزون والأرصدة والدفعات والتحويلات (V003/V017) ──
    public DbSet<StockBalanceRow> StockBalances => Set<StockBalanceRow>();
    public DbSet<LotRow> Lots => Set<LotRow>();
    public DbSet<StockReservationRow> StockReservations => Set<StockReservationRow>();
    public DbSet<CountSessionRow> CountSessions => Set<CountSessionRow>();
    public DbSet<CountLineRow> CountLines => Set<CountLineRow>();
    public DbSet<StockAdjustmentRow> StockAdjustments => Set<StockAdjustmentRow>();
    public DbSet<TransferOrderRow> TransferOrders => Set<TransferOrderRow>();
    public DbSet<TransferLineRow> TransferLines => Set<TransferLineRow>();
    public DbSet<ApprovalRow> Approvals => Set<ApprovalRow>();

    // ── G6: المشتريات والأطراف والخزينة والتكلفة (V004/V005/V006/V017) ──
    public DbSet<SupplierRow> Suppliers => Set<SupplierRow>();
    public DbSet<PurchaseOrderRow> PurchaseOrders => Set<PurchaseOrderRow>();
    public DbSet<PurchaseOrderLineRow> PurchaseOrderLines => Set<PurchaseOrderLineRow>();
    public DbSet<PurchaseReceiptRow> PurchaseReceipts => Set<PurchaseReceiptRow>();
    public DbSet<PurchaseReceiptLineRow> PurchaseReceiptLines => Set<PurchaseReceiptLineRow>();
    public DbSet<SupplierInvoiceRow> SupplierInvoices => Set<SupplierInvoiceRow>();
    public DbSet<SupplierInvoiceLineRow> SupplierInvoiceLines => Set<SupplierInvoiceLineRow>();
    public DbSet<SupplierPaymentRow> SupplierPayments => Set<SupplierPaymentRow>();
    public DbSet<SupplierPaymentAllocationRow> SupplierPaymentAllocations => Set<SupplierPaymentAllocationRow>();
    public DbSet<SupplierReturnRow> SupplierReturns => Set<SupplierReturnRow>();
    public DbSet<SupplierReturnLineRow> SupplierReturnLines => Set<SupplierReturnLineRow>();
    public DbSet<TreasuryAccountRow> TreasuryAccounts => Set<TreasuryAccountRow>();
    public DbSet<TreasuryTxnRow> TreasuryTransactions => Set<TreasuryTxnRow>();
    public DbSet<CostLayerRow> CostLayers => Set<CostLayerRow>();
    public DbSet<CustomerRow> Customers => Set<CustomerRow>();

    // ── G7: المبيعات ونقطة البيع والطباعة (V005/V007/V018) ──
    public DbSet<SalesInvoiceRow> SalesInvoices => Set<SalesInvoiceRow>();
    public DbSet<SalesInvoiceLineRow> SalesInvoiceLines => Set<SalesInvoiceLineRow>();
    public DbSet<SalesPaymentRow> SalesPayments => Set<SalesPaymentRow>();
    public DbSet<SalesRefundRow> SalesRefunds => Set<SalesRefundRow>();
    public DbSet<SalesRefundLineRow> SalesRefundLines => Set<SalesRefundLineRow>();
    public DbSet<HeldCartRow> HeldCarts => Set<HeldCartRow>();
    public DbSet<SalesShiftRow> SalesShifts => Set<SalesShiftRow>();
    public DbSet<DrawerEventRow> DrawerEvents => Set<DrawerEventRow>();
    public DbSet<PrinterProfileRow> PrinterProfiles => Set<PrinterProfileRow>();
    public DbSet<PrinterDocumentRoleRow> PrinterDocumentRoles => Set<PrinterDocumentRoleRow>();
    public DbSet<DocumentTemplateRow> DocumentTemplates => Set<DocumentTemplateRow>();
    public DbSet<PrintJobRow> PrintJobs => Set<PrintJobRow>();

    // ── G8: السندات والمصروفات ورأس المال وعدّات النقدية وتوزيعات السندات (V006/V019) ──
    public DbSet<FinanceVoucherRow> FinanceVouchers => Set<FinanceVoucherRow>();
    public DbSet<VoucherAllocationRow> VoucherAllocations => Set<VoucherAllocationRow>();
    public DbSet<ExpenseCategoryRow> ExpenseCategories => Set<ExpenseCategoryRow>();
    public DbSet<ExpenseRow> Expenses => Set<ExpenseRow>();
    public DbSet<CapitalEventRow> CapitalEvents => Set<CapitalEventRow>();
    public DbSet<ShiftCashCountRow> ShiftCashCounts => Set<ShiftCashCountRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("core");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WeraDbContext).Assembly);
    }
}
