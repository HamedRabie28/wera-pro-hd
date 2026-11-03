namespace WeraPro.Api.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

/// <summary>
/// مصنع الاختبار: يقلع API الحقيقي (Program الكاملة) على PostgreSQL حقيقي
/// (wera_g3_net) مع ترحيلات V001..V014 تُطبق آلياً عند الإقلاع (نفس مسار
/// الإنتاج بالحرف)، ومفتاح توقيع اختباري من الإعدادات لا من الكود، وحدود
/// Rate Limit مرتفعة للاختبارات العامة (الحد الصارم يُختبر بمصنع مخصص).
/// </summary>
public sealed class WeraApiFactory : WebApplicationFactory<WeraPro.Api.Program>
{
    public const string TestDbUri = "Host=localhost;Port=55432;Username=postgres;Database=wera_g3_net";
    public const string TestSigningKey = "g3-test-signing-key-0123456789abcdef-0123456789abcdef";

    private readonly int _authPermitLimit;

    public WeraApiFactory(int authPermitLimit = 1000)
    {
        _authPermitLimit = authPermitLimit;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WeraDatabase"] = TestDbUri,
                ["Tokens:SigningKeySecret"] = TestSigningKey,
                ["Tokens:Issuer"] = "wera-pro-hd-api",
                ["Tokens:Audience"] = "wera-pro-hd-clients",
                ["RateLimiting:AuthPermitLimit"] = _authPermitLimit.ToString(),
                ["RateLimiting:AuthWindowSeconds"] = "60",
            });
        });
    }
}
