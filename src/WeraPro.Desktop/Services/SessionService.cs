// Services/SessionService.cs — WERA PRO HD Desktop (G3)
// حالة الجلسة على العميل: رمز وصول + رمز تجديد في الذاكرة فقط (G3)؛
// التخزين المحلي المحمي (DPAPI) يُفعّل مع التغليف في G14 (D-13).
// التجديد الشفاف يتم عبر عميل HTTP مخصص مستقل عن سلسلة CorrelationHandler
// (منع أي دورة معالجة) — ويُقفل بقفل واحد يمنع طلبات تجديد متزامنة.
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeraPro.Contracts.V1;

namespace WeraPro.Desktop.Services;

public sealed class SessionService
{
    public const string RefreshHttpClientName = "wera-api-refresh";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<SessionService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SessionService(IHttpClientFactory factory, ILogger<SessionService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTimeOffset AccessTokenExpiresAt { get; private set; }
    public UserInfo? User { get; private set; }
    public long? BranchId { get; private set; }
    public long? StationId { get; private set; }
    public string? BranchNameAr { get; private set; }
    public string? StationNameAr { get; private set; }
    public bool IsLoggedIn => User is not null && AccessToken is not null;
    public bool CanRefresh => RefreshToken is not null;

    public event Action? SessionChanged;

    public void Establish(LoginResponse login, long? branchId, long? stationId, string? branchNameAr, string? stationNameAr)
    {
        AccessToken = login.AccessToken;
        RefreshToken = login.RefreshToken;
        AccessTokenExpiresAt = login.ExpiresAtUtc;
        User = login.User;
        BranchId = branchId;
        StationId = stationId;
        BranchNameAr = branchNameAr;
        StationNameAr = stationNameAr;
        SessionChanged?.Invoke();
    }

    public void ApplyRefresh(RefreshResponse refresh)
    {
        AccessToken = refresh.AccessToken;
        RefreshToken = refresh.RefreshToken;
        AccessTokenExpiresAt = refresh.ExpiresAtUtc;
    }

    /// <summary>
    /// إثراء نطاق الجلسة بعد الدخول (2026-11-09): تُستدعى بعد نجاح الدخول وقبل فتح
    /// القشرة — يجلب LoginViewModel الفرع/المحطة بالرمز الجديد ويحدّث النطاق هنا،
    /// فتعرض القشرة وPOS اسم الفرع/المحطة دون أي محددات في بوابة الدخول.
    /// </summary>
    public void EnrichScope(long? branchId, long? stationId, string? branchNameAr, string? stationNameAr)
    {
        BranchId = branchId;
        StationId = stationId;
        BranchNameAr = branchNameAr;
        StationNameAr = stationNameAr;
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// تجديد شفاف لرمز الوصول عبر رمز التجديد — قفل واحد لكل المحطة،
    /// وبعد نجاح واحد يعيد بقية المنتظرين استخدام الرمز الجديد مباشرة.
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        if (RefreshToken is null)
        {
            return false;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // ربما جدّده نداء آخر أثناء انتظار القفل — تحقق من الصلاحية أولاً
            if (AccessToken is not null && DateTimeOffset.UtcNow < AccessTokenExpiresAt.AddSeconds(-30))
            {
                return true;
            }
            if (RefreshToken is null)
            {
                return false;
            }

            using var client = _factory.CreateClient(RefreshHttpClientName);
            using var response = await client.PostAsJsonAsync(
                ApiRoutes.Refresh, new RefreshRequest(RefreshToken), ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("تجديد الجلسة فشل ({Status}) — تسجيل خروج محلي", response.StatusCode);
                Clear();
                return false;
            }
            var refreshed = await response.Content.ReadFromJsonAsync<RefreshResponse>(Json, ct);
            if (refreshed is null)
            {
                Clear();
                return false;
            }
            ApplyRefresh(refreshed);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "تعذر تجديد الجلسة — ستُعاد المحاولة مع الطلب التالي");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
        User = null;
        BranchId = null;
        StationId = null;
        BranchNameAr = null;
        StationNameAr = null;
        SessionChanged?.Invoke();
    }
}
