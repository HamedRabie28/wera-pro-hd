namespace WeraPro.Infrastructure.Security;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WeraPro.Application.Abstractions;
using WeraPro.Domain.Security;

/// <summary>خيارات إصدار الرموز — المفتاح من البيئة/الإعدادات فقط، لا في الكود أبداً (§18).</summary>
public sealed record TokenOptions
{
    public const string SectionName = "Tokens";

    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string SigningKeySecret { get; init; }
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenHours { get; init; } = 12;
}

/// <summary>
/// إصدار JWT وصول قصير العمر (claims: sub/username/roles/scope) وتوليد رموز
/// التجديد العشوائية (256-bit) وتجزئتها SHA-256 على الخادم (V013).
/// </summary>
public sealed class SessionTokenService : ISessionTokenService
{
    private readonly TokenOptions _options;

    public SessionTokenService(IOptions<TokenOptions> options) => _options = options.Value;

    public (string AccessToken, DateTimeOffset ExpiresAtUtc) IssueAccessToken(User user, long? branchId, long? stationId, long sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new("name_ar", user.DisplayNameAr),
            // معرف الجلسة في قاعدة البيانات — لإغلاقها عند الخروج (§11.2)
            new("sid", sessionId.ToString(CultureInfo.InvariantCulture)),
        };
        if (branchId is not null)
        {
            claims.Add(new Claim("branch_id", branchId.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (stationId is not null)
        {
            claims.Add(new Claim("station_id", stationId.Value.ToString(CultureInfo.InvariantCulture)));
        }
        foreach (var role in user.Roles.Select(r => r.RoleCode).Distinct())
        {
            // نوع قصير ثابت ("role") يقابل RoleClaimType في التحقق — لا خرائط ClaimTypes
            claims.Add(new Claim("role", role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKeySecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}

/// <summary>تجزئة كلمات المرور — bcrypt حصراً (§18: Argon2id/bcrypt فقط).</summary>
public sealed class BCryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

    public bool Verify(string password, string passwordHash) =>
        BCrypt.Net.BCrypt.Verify(password, passwordHash);
}
