// Program.cs — WERA PRO HD G3 (top-level statements — الشكل القانوني لاعتراض
// WebApplicationFactory). الترتيب الملزم: ترحيلات → خط أنابيب → استماع.
// لا اتصال مباشر من Desktop بـPostgreSQL (ADR-002) — هذا الخادم هو البوابة الوحيدة.
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using WeraPro.Api.Authorization;
using WeraPro.Api.Endpoints;
using WeraPro.Api.Middleware;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Auth;
using WeraPro.Application.Settings;
using WeraPro.Application.Validation;
using WeraPro.Infrastructure.HealthChecks;
using WeraPro.Infrastructure.Migrations;
using WeraPro.Infrastructure.Persistence;
using WeraPro.Infrastructure.Persistence.Repositories;
using WeraPro.Infrastructure.Security;
using WeraPro.Domain.Security;
using WeraPro.Api;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var isTestHost = false;
try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        // إنتاج 2026-11-08: جذر المحتوى = مجلد التجميع دائماً — يضمن قراءة
        // appsettings.json (Tokens:Issuer/Audience وكل الإعدادات) أياً كان مجلد
        // التشغيل (خدمة Windows/مثبّت/سكربت) — بدونه تُصدَّر جلسات بلا aud/iss
        // فيرفضها التحقق بـ401 (اكتُشف في الاختبار الحي لهذا الإصدار).
        ContentRootPath = AppContext.BaseDirectory,
    });
    isTestHost = builder.Environment.IsEnvironment("Testing");

    // ── السجلات: Serilog (لا تُسجَّل هيئات الطلبات — حماية الأسرار §18) ──
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
        .WriteTo.File(
            Path.Combine(builder.Environment.ContentRootPath, "logs", "wera-api-.log"),
            formatProvider: System.Globalization.CultureInfo.InvariantCulture,
            rollingInterval: RollingInterval.Day,
            retainedFileTimeLimit: TimeSpan.FromDays(13))); // retention.operational_months

    // ── الإعدادات والخيارات ──
    builder.Services.Configure<TokenOptions>(builder.Configuration.GetSection(TokenOptions.SectionName));
    builder.Services.AddOptions<TokenOptions>()
        .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKeySecret), "مفتاح التوقيع مطلوب من البيئة TOKENS__SIGNINGKEYSECRET (متغيرات البيئة تُقرأ بدون بادئة)")
        .Validate(o => o.SigningKeySecret is { Length: >= 32 }, "مفتاح التوقيع لا يقل عن 32 محرفاً")
        .ValidateOnStart();

    // ── قاعدة البيانات: EF Core/Npgsql + عدّاء الترحيلات SQL ──
    var connectionString = builder.Configuration.GetConnectionString("WeraDatabase")
        ?? throw new InvalidOperationException("ConnectionStrings:WeraDatabase مفقود");
    builder.Services.AddDbContext<WeraDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));

    builder.Services.AddSingleton(sp => new SqlMigrationRunner(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<SqlMigrationRunner>()));

    // ── طبقة التطبيق والبنية التحتية ──
    builder.Services.AddMemoryCache();
    // IHttpContextAccessor يسجل Singleton واحدة عبر AddHttpContextAccessor —
    // لا يجوز إعادة تسجيلها Scoped بوسيط يحل نفسه (تكرار لا نهائي في DI)
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
    builder.Services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
    builder.Services.AddScoped<ISessionTokenService, SessionTokenService>();
    builder.Services.AddScoped<IUserRepository, UserRepository>();
    builder.Services.AddScoped<ISessionRepository, SessionRepository>();
    builder.Services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
    builder.Services.AddScoped<ISettingsRepository, SettingsRepository>();
    builder.Services.AddScoped<IBranchRepository, BranchRepository>();
    builder.Services.AddScoped<IAuditWriter, AuditWriter>();
    // ── G-ADMIN: المستخدمون والإعدادات والصيانة (PAGE-47/51/52) ──
    builder.Services.AddScoped<WeraPro.Application.Abstractions.IUsersAdminRepository, WeraPro.Infrastructure.Persistence.Repositories.UsersAdminRepository>();
    builder.Services.AddScoped<WeraPro.Application.Abstractions.IBackupRepository, WeraPro.Infrastructure.Persistence.Repositories.BackupRepository>();
    builder.Services.AddScoped<WeraPro.Application.Abstractions.ISystemMaintenanceRepository, WeraPro.Infrastructure.Maintenance.SystemMaintenanceRepository>();
    builder.Services.AddScoped<WeraPro.Application.Abstractions.IBackupStorage, WeraPro.Infrastructure.Maintenance.PgDumpBackupStorage>();
    builder.Services.Configure<WeraPro.Infrastructure.Maintenance.BackupOptions>(options =>
    {
        options.ConnectionString = connectionString;
    });
    builder.Services.Configure<WeraPro.Infrastructure.Maintenance.BackupOptions>(builder.Configuration.GetSection(WeraPro.Infrastructure.Maintenance.BackupOptions.SectionName));
    builder.Services.AddScoped<WeraPro.Application.Security.UsersAdminUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Settings.UpdateSettingUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Maintenance.BackupUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Maintenance.SystemCleanupUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Maintenance.WipeDataUseCase>();
    builder.Services.AddScoped<LoginUseCase>();
    builder.Services.AddScoped<RefreshUseCase>();
    builder.Services.AddScoped<LogoutUseCase>();
    builder.Services.AddScoped<GetSettingsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Audit.IAuditReadRepository, AuditReadRepository>();
    builder.Services.AddScoped<WeraPro.Application.Audit.GetLoginEventsUseCase>();

    // ── الكتالوج (G4) ──
    builder.Services.AddScoped<WeraPro.Application.Catalog.IUnitCatalog, WeraPro.Infrastructure.Persistence.Repositories.UnitCatalog>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.ICatalogRepository, WeraPro.Infrastructure.Persistence.Repositories.CatalogRepository>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.CreateProductUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.SearchCatalogUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.GetProductUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.ListUnitsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.GetCatalogReferenceUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.PostStockMovementUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Catalog.SetProductStatusUseCase>();

    // ── الوسائط (G5) ──
    builder.Services.Configure<WeraPro.Infrastructure.Media.MediaStorageOptions>(
        builder.Configuration.GetSection(WeraPro.Infrastructure.Media.MediaStorageOptions.SectionName));
    builder.Services.AddSingleton<WeraPro.Application.Media.IMediaStorage, WeraPro.Infrastructure.Media.LocalFileStorage>();
    builder.Services.AddSingleton<WeraPro.Application.Media.IMediaImageProcessor, WeraPro.Infrastructure.Media.SkiaImageProcessor>();
    builder.Services.AddScoped<WeraPro.Infrastructure.Persistence.Repositories.MediaRepository>();
    builder.Services.AddScoped<WeraPro.Application.Media.IMediaRepository>(sp => sp.GetRequiredService<WeraPro.Infrastructure.Persistence.Repositories.MediaRepository>());
    builder.Services.AddScoped<WeraPro.Application.Media.IMediaImportRepository>(sp => sp.GetRequiredService<WeraPro.Infrastructure.Persistence.Repositories.MediaRepository>());
    builder.Services.AddScoped<WeraPro.Application.Media.UploadMediaUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.SearchMediaUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.GetMediaDetailsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.ServeMediaVariantUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.LinkMediaUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.UnlinkMediaUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.SetPrimaryLinkUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.DeleteMediaUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.ReprocessThumbnailsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.GetDuplicatesReportUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.GetSkuDefaultImageUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.CreateImportJobUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.GetImportJobUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.AddImportFilesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.StartImportJobUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.CancelImportJobUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.ListImportFilesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.GetImportReportUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Media.MediaImportFileProcessor>();
    builder.Services.AddHostedService<WeraPro.BackgroundServices.MediaImportWorker>();

    // ── المخزون والمشتريات والأطراف (G6) ──
    builder.Services.AddScoped<WeraPro.Application.Inventory.IInventoryRepository, WeraPro.Infrastructure.Persistence.Repositories.InventoryRepository>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.IApprovalRepository, WeraPro.Infrastructure.Persistence.Repositories.ApprovalRepository>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.IPurchasingRepository, WeraPro.Infrastructure.Persistence.Repositories.PurchasingRepository>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ICustomerRepository, WeraPro.Infrastructure.Persistence.Repositories.CustomerRepository>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.PurchasingLineResolver>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.ManageWarehousesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.GetStockBalancesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.GetInventoryReportsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.ManageReservationsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.ManageStocktakesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.PostQuickAdjustmentUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.ManageTransfersUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.GetInventoryAdvisoriesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Inventory.ManageApprovalsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManageSuppliersUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManageCustomersUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManagePurchaseOrdersUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManageReceiptsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManageSupplierInvoicesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManageSupplierPaymentsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Purchasing.ManageSupplierReturnsUseCase>();

    // ── نقطة البيع والمبيعات والطباعة (G7) ──
    builder.Services.AddScoped<WeraPro.Application.Sales.ISalesRepository, WeraPro.Infrastructure.Persistence.Repositories.SalesRepository>();
    builder.Services.AddScoped<WeraPro.Application.Sales.ICardPaymentGateway, WeraPro.Infrastructure.Payment.SandboxCardGateway>();
    builder.Services.AddScoped<WeraPro.Application.Sales.PosSettingsReader>();
    builder.Services.AddScoped<WeraPro.Application.Sales.PosScopeResolver>();
    builder.Services.AddScoped<WeraPro.Application.Sales.SaleLineResolver>();
    builder.Services.AddScoped<WeraPro.Application.Sales.GetPosContextUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.LookupPosItemUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.SearchPosItemsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.PreviewSaleUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.WeightPreviewUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.PostSaleUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.VoidSaleUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.ManageHeldCartsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.PostRefundUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.ManageShiftsUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.OpenDrawerUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.ManageReceiptPrintingUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Sales.GetSaleUseCase>();

    // ── الورديات والنقد والأدراج والمصروفات ورأس المال (G8 · §14/§13) ──
    builder.Services.AddScoped<WeraPro.Application.Finance.IFinanceRepository, WeraPro.Infrastructure.Persistence.Repositories.FinanceRepository>();
    builder.Services.AddScoped<WeraPro.Application.Finance.ManageTreasuryUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Finance.ManageVouchersUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Finance.ManageExpensesUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Finance.ManageCapitalUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Finance.CloseShiftUseCase>();
    builder.Services.AddScoped<WeraPro.Application.Finance.PrintFinanceDocumentsUseCase>();

    // ── التحقق الموحد (ADR-007) ──
    builder.Services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();

    // ── المصادقة: JWT قصير العمر ──
    // يُقرأ قسم Tokens داخل تكوين المخطط (تقييم كسول عند أول استخدام) — بعد اكتمال
    // كل مصادر الإعداد (مصادر الاختبار تُطبَّق بعد نقطة الدخول فلا تُقرأ مبكراً)
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var tokens = builder.Configuration.GetSection(TokenOptions.SectionName).Get<TokenOptions>()
                ?? throw new InvalidOperationException("قسم Tokens مفقود");
            // لا إعادة تخطيط لأنواع الـclaims الواردة — نقرأ sub/unique_name/role
            // كما أصدرناها حرفياً (خلافاً للخريطة الافتراضية ClaimTypes.* الطويلة)
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = tokens.Issuer,
                ValidateAudience = true,
                ValidAudience = tokens.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(tokens.SigningKeySecret)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                // أنواع الـclaims كما أصدرناها حرفياً — لا خرائط ClaimTypes الطويلة
                NameClaimType = "unique_name",
                RoleClaimType = "role",
            };
        });

    // ── التفويض: صلاحيات ذرية تُفحص على الخادم لكل endpoint ──
    builder.Services.AddPermissionPolicies();

    // ── Health Checks ──
    builder.Services.AddHealthChecks()
        .AddCheck("self",
            () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("عملية الخادم حية"),
            tags: [HealthTags.Live])
        .AddCheck(NpgsqlHealthCheck.Name, new NpgsqlHealthCheck(connectionString), tags: [HealthTags.Ready]);

    // ── OpenAPI (مدمج في ASP.NET Core 10 — عقد v1 موثق آلياً) ──
    builder.Services.AddOpenApi(options =>
    {
        options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0;
    });

    // ── Rate Limiting (§11.2: حماية من brute-force والطلبات المتكررة) ──
    // القيم تُقرأ داخل تكوين الخيارات (تقييم كسول) — مصادر إعداد الاختبار
    // تُطبَّق بعد نقطة الدخول فلا تصح القراءة المبكرة
    builder.Services.AddRateLimiter(options =>
    {
        var authLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimit", 5);
        var authWindow = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimiting:AuthWindowSeconds", 60));
        var apiTokenLimit = builder.Configuration.GetValue("RateLimiting:ApiTokenLimit", 200);
        var apiPerSecond = builder.Configuration.GetValue("RateLimiting:ApiTokensPerSecond", 20);
        var apiQueue = builder.Configuration.GetValue("RateLimiting:ApiQueueLimit", 50);

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = static (context, ct) =>
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)TimeSpan.FromSeconds(10).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ValueTask.CompletedTask;
        };

        // سياسة auth: نافذة ثابتة صارمة لكل IP — قفل brute-force محاولات الدخول
        options.AddPolicy(RateLimitPolicies.Auth, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authLimit,
                    Window = authWindow,
                    QueueLimit = 0,
                }));

        // سياسة api عامة: دلو رموز لكل مستخدم موثق
        options.AddPolicy(RateLimitPolicies.Api, context =>
        {
            var user = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            return RateLimitPartition.GetTokenBucketLimiter(
                partitionKey: user ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = apiTokenLimit,
                    TokensPerPeriod = apiPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = apiQueue,
                });
        });
    });

    var app = builder.Build();

    // ── الترحيلات قبل أي استماع (سياسة قاعدة نظيفة عند الإقلاع) ──
    var runner = app.Services.GetRequiredService<SqlMigrationRunner>();
    await runner.ApplyPendingAsync(connectionString);

    // ── خط الأنابيب (الترتيب ملزم) ──
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<ErrorHandlingMiddleware>();
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "{RequestMethod} {RequestPath} → {StatusCode} في {Elapsed:0.0}ms (correlation={CorrelationId})";
        options.EnrichDiagnosticContext = (diagnostic, http) =>
            diagnostic.Set("CorrelationId", http.GetCorrelationId());
    });

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapOpenApi();                      // /openapi/v1.json
    app.MapHealthChecks("/health/live", new()
    {
        Predicate = check => check.Tags.Contains(HealthTags.Live),
    });
    app.MapHealthChecks("/health/ready", new()
    {
        Predicate = _ => true,
        // إخراج JSON منظم: الحالة الكلية + كل فحص باسمه ومدته (يُقرأ آلياً)
        ResponseWriter = static async (httpContext, report) =>
        {
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await using var writer = new System.Text.Json.Utf8JsonWriter(httpContext.Response.Body);
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteNumber("totalDurationMs", report.TotalDuration.TotalMilliseconds);
            writer.WriteStartObject("checks");
            foreach (var (name, entry) in report.Entries)
            {
                writer.WriteStartObject(name);
                writer.WriteString("status", entry.Status.ToString());
                writer.WriteNumber("durationMs", entry.Duration.TotalMilliseconds);
                if (entry.Data.Count > 0)
                {
                    writer.WriteStartObject("data");
                    foreach (var (key, value) in entry.Data)
                    {
                        writer.WriteString(key, value?.ToString());
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        },
    });

    app.MapAuthEndpoints()
       .MapReferenceEndpoints()
       .MapAuditEndpoints()
       .MapUsersEndpoints()
       .MapSettingsEndpoints()
       .MapMaintenanceEndpoints()
       .MapCatalogEndpoints()
       .MapMediaEndpoints()
       .MapInventoryEndpoints()
       .MapPurchasingEndpoints()
       .MapSalesEndpoints()
       .MapFinanceEndpoints();

    app.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "فشل إقلاع الخادم");
    return 1;
}
finally
{
    // تحت WebApplicationFactory (بيئة Testing) يعود نقطة الدخول قبل انتهاء دورة
    // حياة الخادم المؤجل — تفريغ السجلات هنا يُغلق المسجل أثناء استخدامه؛
    // التفريغ الكامل مسؤولية المضيف (UseSerilog) في الإنتاج.
    if (!isTestHost)
    {
        await Log.CloseAndFlushAsync();
    }
}

namespace WeraPro.Api
{
    /// <summary>وسوم فحوص الجاهزية.</summary>
    internal static class HealthTags
    {
        public const string Live = "live";
        public const string Ready = "ready";
    }

    /// <summary>علامة نقطة الدخول لـWebApplicationFactory — عقد Program القانوني.</summary>
    public sealed partial class Program;

    /// <summary>أسماء سياسات Rate Limiting المسجلة.</summary>
    public static class RateLimitPolicies
    {
        public const string Auth = "auth";
        public const string Api = "api";
    }
}
