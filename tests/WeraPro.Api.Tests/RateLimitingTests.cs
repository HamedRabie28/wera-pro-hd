namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using Xunit;

/// <summary>
/// اختبار Rate Limiting بحد صارم معزول (§11.2: حماية brute-force):
/// مصنع مستقل بحد دخول = 3/دقيقة — الطلبات الزائدة تُرفض 429 بلا استثناء.
/// </summary>
public sealed class RateLimitingTests
{
    [Fact]
    public async Task LoginBurst_ExceedsLimit_Gets429()
    {
        using var factory = new WeraApiFactory(authPermitLimit: 3);
        using var client = factory.CreateClient();

        HttpStatusCode[] statuses =
        [
            await PostLoginAsync(client),
            await PostLoginAsync(client),
            await PostLoginAsync(client),
            await PostLoginAsync(client),   // الرابع فوق الحد
        ];

        Assert.Contains(HttpStatusCode.Unauthorized, statuses.Take(3));   // الثلاثة الأولى تعالجت (فشلت بياناتها)
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[3]);        // الرابع مرفوض حداً
    }

    private static async Task<HttpStatusCode> PostLoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "nobody", password = "wrong", branchId = (long?)null, stationId = (long?)null });
        return response.StatusCode;
    }
}
