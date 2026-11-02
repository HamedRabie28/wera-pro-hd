namespace WeraPro.Contracts.V1;

/// <summary>عقد v1 — مسار URL الثابت /api/v1 (تغييره كسر للعقد ويستلزم v2).</summary>
public static class ApiRoutes
{
    public const string Prefix = "api/v1";

    public const string Login = Prefix + "/auth/login";
    public const string Refresh = Prefix + "/auth/refresh";
    public const string Logout = Prefix + "/auth/logout";
    public const string Me = Prefix + "/auth/me";
    public const string Branches = Prefix + "/branches";
    public const string Stations = Prefix + "/branches/{branchId:long}/stations";
    public const string Settings = Prefix + "/settings";
    public const string HealthLive = "health/live";
    public const string HealthReady = "health/ready";
}

// ── المصادقة ────────────────────────────────────────────────────────────────

public sealed record LoginRequest(string Username, string Password, long? BranchId, long? StationId);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, string RefreshToken, UserInfo User);

public sealed record RefreshRequest(string RefreshToken);

public sealed record RefreshResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, string RefreshToken);

public sealed record UserInfo(long UserId, string Username, string DisplayNameAr, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public sealed record UserScope(long? BranchId, string? BranchNameAr, long? StationId, string? StationNameAr);

// ── الفروع والمحطات ─────────────────────────────────────────────────────────

public sealed record BranchDto(long BranchId, string Code, string NameAr, string Timezone, bool IsActive);

public sealed record StationDto(long StationId, long BranchId, string Code, string NameAr, bool IsPos, bool IsActive);

// ── الإعدادات ───────────────────────────────────────────────────────────────

public sealed record SettingDto(
    string SettingKey,
    string ValueText,
    string ValueType,
    string Category,
    string? DecisionRef,
    string ConfirmationStatus,
    string DescriptionAr);

// ── الأخطاء (RFC 7807) ──────────────────────────────────────────────────────

/// <summary>خطأ موحد على هيئة ProblemDetails — بلا أسرار ولا مسارات داخلية.</summary>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string? CorrelationId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string[]>? Errors = null);
