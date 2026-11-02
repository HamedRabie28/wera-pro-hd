namespace WeraPro.Api.Endpoints;

using System.Security.Claims;
using WeraPro.Application.Abstractions;

/// <summary>مستخدم الطلب الحالي من claims الـJWT (sub/roles/branch/station/session).</summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public long UserId => long.TryParse(
        Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value, out var v) ? v : 0;
    public string Username => Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.UniqueName)?.Value ?? string.Empty;
    public IReadOnlyList<string> Roles =>
        [.. Principal?.FindAll("role").Select(c => c.Value) ?? []];   // كما صدرت — لا خرائط ClaimTypes
    public long? BranchId => long.TryParse(Principal?.FindFirst("branch_id")?.Value, out var v) ? v : null;
    public long? StationId => long.TryParse(Principal?.FindFirst("station_id")?.Value, out var v) ? v : null;
    public long? SessionId => long.TryParse(Principal?.FindFirst("sid")?.Value, out var v) ? v : null;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
}
