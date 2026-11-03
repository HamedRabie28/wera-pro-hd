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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("core");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WeraDbContext).Assembly);
    }
}
