namespace WeraPro.Application.Auth;

using System.Threading;
using Microsoft.Extensions.Logging;
using WeraPro.Application.Abstractions;
using WeraPro.Application.Validation;
using WeraPro.Contracts.V1;
using WeraPro.Domain.Security;
using WeraPro.Domain.SharedKernel;

/// <summary>
/// حالة استخدام تسجيل الدخول (§11.2): تحقق BCrypt → جلسة (audit.user_sessions)
/// → تدقيق LOGIN (SUCCESS/DENIED) → رمز وصول قصير + رمز تجديد hash على الخادم.
/// الحماية من brute-force عبر Rate Limiting على مستوى الـendpoint (سياسة auth).
/// </summary>
public sealed partial class LoginUseCase
{
    private readonly IUserRepository _users;
    private readonly ISessionRepository _sessions;
    private readonly IRefreshTokenStore _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly ISessionTokenService _tokens;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<LoginUseCase> _logger;

    public LoginUseCase(
        IUserRepository users,
        ISessionRepository sessions,
        IRefreshTokenStore refreshTokens,
        IPasswordHasher hasher,
        ISessionTokenService tokens,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<LoginUseCase> logger)
    {
        _users = users;
        _sessions = sessions;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _tokens = tokens;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<LoginResponse>> ExecuteAsync(LoginRequest request, string? correlationId, CancellationToken ct = default)
    {
        var user = await _users.FindByUsernameAsync(request.Username, ct);

        // رسالة فشل واحدة للعميل (لا تكشف هل المستخدم موجود) — التفصيل في التدقيق فقط.
        var denied = new DomainError("AUTH_INVALID_CREDENTIALS", "اسم المستخدم أو كلمة المرور غير صحيحة");

        if (user is null || !_hasher.Verify(request.Password, user.PasswordHash))
        {
            // التدقيق: صف DENIED يُكتب لحساب معروف فقط (user_id FK في audit_logs)؛
            // المحاولات على أسماء غير موجودة تُقيّدها سياسة Rate Limit وتُسجَّل في سجلات الخادم.
            if (user is not null)
            {
                await _audit.WriteAsync(new AuditEntry(
                    UserId: user.UserId, RoleCode: null, StationId: request.StationId, BranchId: request.BranchId,
                    EntitySchema: "core.users", EntityId: user.UserId, Action: "LOGIN", Result: "DENIED",
                    Reason: "INVALID_CREDENTIALS", CorrelationId: correlationId), ct);
            }
            LogLoginDenied(request.Username, correlationId);
            return Result.Fail<LoginResponse>(denied);
        }

        if (!user.IsActive)
        {
            await _audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: request.StationId, BranchId: request.BranchId,
                EntitySchema: "core.users", EntityId: user.UserId, Action: "LOGIN", Result: "DENIED",
                Reason: "USER_INACTIVE", CorrelationId: correlationId), ct);
            return Result.Fail<LoginResponse>(new DomainError("AUTH_USER_INACTIVE", "الحساب معطل — راجع المالك"));
        }

        var permissions = await _users.GetPermissionCodesAsync(user.UserId, request.BranchId, ct);
        var sessionId = await _sessions.OpenSessionAsync(user.UserId, request.StationId, ipAddress: null, ct);

        var (accessToken, expiresAt) = _tokens.IssueAccessToken(user, request.BranchId, request.StationId, sessionId);
        var refreshToken = _tokens.GenerateRefreshToken();
        var refreshExpiry = _clock.GetUtcNow().AddHours(12);
        await _refreshTokens.IssueAsync(user.UserId, sessionId, _tokens.HashRefreshToken(refreshToken), refreshExpiry, ct);

        await _audit.WriteAsync(new AuditEntry(
            UserId: user.UserId,
            RoleCode: user.Roles.Count > 0 ? user.Roles[0].RoleCode : null,
            StationId: request.StationId, BranchId: request.BranchId,
            EntitySchema: "audit.user_sessions", EntityId: sessionId, Action: "LOGIN", Result: "SUCCESS",
            CorrelationId: correlationId), ct);

        var info = new UserInfo(user.UserId, user.Username, user.DisplayNameAr,
            [.. user.Roles.Select(r => r.RoleCode)], permissions);
        return Result.Ok(new LoginResponse(accessToken, expiresAt, refreshToken, info));
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Login DENIED for {Username} (correlation={CorrelationId})")]
    private partial void LogLoginDenied(string username, string? correlationId);
}

/// <summary>
/// تجديد رمز الوصول عبر رمز التجديد — تدوير إلزامي لكل استخدام؛
/// إعادة استخدام رمز مُدار تُلغي السلسلة كلها (كشف السرقة §11.2).
/// </summary>
public sealed class RefreshUseCase
{
    private readonly IRefreshTokenStore _store;
    private readonly IUserRepository _users;
    private readonly ISessionTokenService _tokens;
    private readonly TimeProvider _clock;

    public RefreshUseCase(
        IRefreshTokenStore store,
        IUserRepository users,
        ISessionTokenService tokens,
        TimeProvider clock)
    {
        _store = store;
        _users = users;
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<Result<RefreshResponse>> ExecuteAsync(RefreshRequest request, CancellationToken ct = default)
    {
        var hash = _tokens.HashRefreshToken(request.RefreshToken);
        var record = await _store.FindActiveByHashAsync(hash, ct);

        if (record is null)
        {
            return Result.Fail<RefreshResponse>(new DomainError("AUTH_REFRESH_INVALID", "رمز التجديد غير صالح أو منتهٍ"));
        }

        var user = await _users.FindByIdAsync(record.UserId, ct);
        if (user is null || !user.IsActive)
        {
            await _store.RevokeByUserAsync(record.UserId, "USER_INACTIVE", ct);
            return Result.Fail<RefreshResponse>(new DomainError("AUTH_USER_INACTIVE", "الحساب معطل — راجع المالك"));
        }

        var now = _clock.GetUtcNow();
        var newRefresh = _tokens.GenerateRefreshToken();
        await _store.RotateAsync(
            record.Id, record.UserId, record.SessionId,
            _tokens.HashRefreshToken(newRefresh), now.AddHours(12), ct);

        var (accessToken, expiresAt) = _tokens.IssueAccessToken(user, branchId: null, stationId: null, sessionId: record.SessionId);
        return Result.Ok(new RefreshResponse(accessToken, expiresAt, newRefresh));
    }
}

/// <summary>تسجيل الخروج: إغلاق الجلسة وإلغاء كل رموز تجديد المستخدم (§11.2 إبطال عند الخروج).</summary>
public sealed class LogoutUseCase
{
    private readonly ICurrentUser _currentUser;
    private readonly ISessionRepository _sessions;
    private readonly IRefreshTokenStore _refreshTokens;

    public LogoutUseCase(ICurrentUser currentUser, ISessionRepository sessions, IRefreshTokenStore refreshTokens)
    {
        _currentUser = currentUser;
        _sessions = sessions;
        _refreshTokens = refreshTokens;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (_currentUser.SessionId is { } sid)
        {
            await _sessions.CloseSessionAsync(sid, ct);
        }
        if (_currentUser.IsAuthenticated)
        {
            await _refreshTokens.RevokeByUserAsync(_currentUser.UserId, "LOGOUT", ct);
        }
    }
}
