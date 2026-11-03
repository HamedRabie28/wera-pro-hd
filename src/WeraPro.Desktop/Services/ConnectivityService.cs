// Services/ConnectivityService.cs — WERA PRO HD Desktop (G3)
// حالة الاتصال الحقيقية (D-08: Online-first افتراضياً): فحص دوري خفيف لـ
// health/ready وتحديث الحالة المعروضة — لا تُعرض «متصل» زوراً أبداً (§10.6).
namespace WeraPro.Desktop.Services;

public enum ConnectionState { Unknown, Online, Offline }

public sealed class ConnectivityService(WeraApiClient api, ILogger<ConnectivityService> logger)
{
    private ConnectionState _state = ConnectionState.Unknown;
    private DateTimeOffset? _lastCheckedUtc;
    private DateTimeOffset? _lastOnlineUtc;

    public event Action? StateChanged;

    public ConnectionState State => _state;
    public DateTimeOffset? LastOnlineUtc => _lastOnlineUtc;
    public DateTimeOffset? LastCheckedUtc => _lastCheckedUtc;

    public string DisplayAr => _state switch
    {
        ConnectionState.Online => "متصل",
        ConnectionState.Offline => "غير متصل",
        _ => "جارٍ التحقق…",
    };

    /// <summary>فحص واحد — يُستدعى دورياً من مؤقت الشاشة وعند فشل أي نداء.</summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        var reachable = await api.IsServerReadyAsync(ct);
        var next = reachable ? ConnectionState.Online : ConnectionState.Offline;
        _lastCheckedUtc = DateTimeOffset.UtcNow;
        if (reachable)
        {
            _lastOnlineUtc = DateTimeOffset.UtcNow;
        }
        if (next != _state)
        {
            _state = next;
            logger.LogInformation("حالة الاتصال: {State}", DisplayAr);
            StateChanged?.Invoke();
        }
    }
}
