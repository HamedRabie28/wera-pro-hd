namespace WeraPro.Api.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

/// <summary>
/// مصنع الاختبار: يقلع API الحقيقي (Program الكاملة) على PostgreSQL حقيقي
/// (wera_g3_net) مع ترحيلات V001..V016 تُطبق آلياً عند الإقلاع (نفس مسار
/// الإنتاج بالحرف)، ومفتاح توقيع اختباري من الإعدادات لا من الكود، وحدود
/// Rate Limit مرتفعة للاختبارات العامة (الحد الصارم يُختبر بمصنع مخصص).
/// </summary>
public sealed class WeraApiFactory : WebApplicationFactory<WeraPro.Api.Program>
{
    public const string TestDbUri = "Host=localhost;Port=55432;Username=postgres;Database=wera_g3_net";
    public const string TestSigningKey = "g3-test-signing-key-0123456789abcdef-0123456789abcdef";

    /// <summary>
    /// جذر تخزين وسائط خاص بكل مصنع (G5) — لكل مثيل مصنع جذر مستقل: المصانع
    /// المتوازية (RateLimiting وغيرها) لا تمسح جذور غيرها أبداً.
    /// </summary>
    public string MediaRoot { get; } =
        Path.Combine(Path.GetTempPath(), "wera-g5-media-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly int _authPermitLimit;

    public WeraApiFactory(int authPermitLimit = 1000)
    {
        _authPermitLimit = authPermitLimit;
        Directory.CreateDirectory(MediaRoot);
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
                ["Media:StorageRoot"] = MediaRoot,
            });
        });
    }
}
