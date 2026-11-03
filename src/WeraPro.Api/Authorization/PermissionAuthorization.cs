namespace WeraPro.Api.Authorization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Security;

/// <summary>متطلب صلاحية ذرية — الفحص على الخادم لكل endpoint (§18: لا إخفاء أزرار كحماية).</summary>
public sealed record PermissionRequirement(string PermissionCode) : IAuthorizationRequirement;

/// <summary>
/// فاحص الصلاحيات: يقرأ أدوار المستخدم من JWT ثم يحل الأدوار → صلاحيات عبر
/// الجدول المرجعي (كاش 60 ثانية) — الرمز يحمل الأدوار فقط، والصلاحيات تُحل
/// خادمياً حتى تظل إلغاءات الصلاحية نافذة فوراً.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IUserRepository _users;
    private readonly IMemoryCache _cache;

    public PermissionHandler(IUserRepository users, IMemoryCache cache)
    {
        _users = users;
        _cache = cache;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var userIdClaim = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (!long.TryParse(userIdClaim, out var userId))
        {
            return;
        }

        var branchRaw = context.User.FindFirst("branch_id")?.Value;
        long? branchId = long.TryParse(branchRaw, out var b) ? b : null;

        var cacheKey = $"perms:{userId}:{branchId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"}";
        var permissions = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return await _users.GetPermissionCodesAsync(userId, branchId);
        });

        if (permissions is not null && permissions.Contains(requirement.PermissionCode))
        {
            context.Succeed(requirement);
        }
    }
}

public static class PermissionPolicy
{
    /// <summary>تسجيل سياسات صلاحيات G3 (البقية تسجل مع بواباتها G4+).</summary>
    public static void AddPermissionPolicies(this IServiceCollection services)
    {
        // Scoped لا Singleton: الفاحص يعتمد IUserRepository (Scoped/DbContext) —
        // تسجيله Singleton يأسر تبعية Scoped فيشترك طلبات متوازية في DbContext واحد.
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PermissionCodes.SettingsRead,
                p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(PermissionCodes.SettingsRead)));
            options.AddPolicy(PermissionCodes.BranchesRead,
                p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(PermissionCodes.BranchesRead)));
            options.AddPolicy(PermissionCodes.AuditView,
                p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(PermissionCodes.AuditView)));

            // ── صلاحيات الكتالوج G4 (V009 + V015) ──
            foreach (var code in new[]
            {
                PermissionCodes.CatalogProductRead,
                PermissionCodes.CatalogProductCreate,
                PermissionCodes.CatalogProductEdit,
                PermissionCodes.CatalogProductDeactivate,
                PermissionCodes.CatalogBarcodeManage,
                PermissionCodes.CatalogPriceEdit,
                PermissionCodes.InventoryMovementPost,
            })
            {
                options.AddPolicy(code,
                    p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(code)));
            }

            // ── صلاحيات الوسائط G5 (V016 + V009) ──
            foreach (var code in new[]
            {
                PermissionCodes.CatalogMediaRead,
                PermissionCodes.CatalogMediaManage,
                PermissionCodes.CatalogImportRun,
            })
            {
                options.AddPolicy(code,
                    p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(code)));
            }
        });
    }
}
