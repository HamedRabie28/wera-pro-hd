namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;

/// <summary>
/// فحوص البوابة G3 (§21.1 API/Contract): مصادقة وتفويض لكل دور · نطاق فرع ·
/// correlation · validation · health · OpenAPI · عقد الاستجابات · جلسات وتدقيق.
/// تعمل على قاعدة حقيقية مُرحّلة V001..V014 مع مستخدمي اختبار bcrypt حقيقيين.
/// </summary>
public sealed class AuthAndContractTests : IClassFixture<LiveApiFixture>
{
    private readonly LiveApiFixture _fx;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AuthAndContractTests(LiveApiFixture fx) => _fx = fx;

    // ── المصادقة (§11.2) ────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ValidOwner_ReturnsTokensAndIdentity()
    {
        var login = await _fx.LoginAsync("g3.owner", "Owner#G3-Pass");
        Assert.NotNull(login);
        Assert.NotEmpty(login.AccessToken);
        Assert.NotEmpty(login.RefreshToken);
        Assert.Equal("مالك الاختبار", login.User.DisplayNameAr);
        Assert.Contains("OWNER", login.User.Roles);
        Assert.Contains("ops.settings.read", login.User.Permissions);   // V014
        Assert.Contains("security.audit.view", login.User.Permissions); // OWNER = الكل
    }

    [Fact]
    public async Task Login_WrongPassword_401_AndAuditDeniedRow()
    {
        var response = await _fx.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "g3.owner", password = "WRONG", branchId = (long?)null, stationId = (long?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("فشل تسجيل الدخول", problem.GetProperty("title").GetString());

        // سجل التدقيق: صف DENIED بسبب INVALID_CREDENTIALS مع معرف ترابط
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.audit_logs WHERE action='LOGIN' AND result='DENIED' AND reason='INVALID_CREDENTIALS'", conn);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) >= 1, "لا يوجد صف DENIED في سجل التدقيق");
    }

    [Fact]
    public async Task Login_InactiveUser_401_WithClearMessage()
    {
        var response = await _fx.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "g3.inactive", password = "Same#Pass-1", branchId = (long?)null, stationId = (long?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("معطل", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Login_InvalidPayload_400_ValidationProblem()
    {
        var response = await _fx.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "اسم عربي بفراغات!", password = "", branchId = 0, stationId = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("errors", out _), "يجب أن تحمل أخطاء التحقق قاموس errors");
    }

    [Fact]
    public async Task Me_WithToken_ReturnsIdentityAndScope()
    {
        var login = await _fx.LoginAsync("g3.cashier", "Cashier#G3-Pass", branchId: _fx.BranchId);
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var me = await authed.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("g3.cashier", me.GetProperty("username").GetString());
        Assert.Contains("CASHIER", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(_fx.BranchId, me.GetProperty("branchId").GetInt64());
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_401()
    {
        var response = await _fx.Client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── التفويض: صلاحية ذرية خادمية (§18) ─────────────────────────────────

    [Fact]
    public async Task AuditLogins_OwnerAllowed_200()
    {
        var login = await _fx.LoginAsync("g3.owner", "Owner#G3-Pass");
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var response = await authed.GetAsync("/api/v1/audit/logins?limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("count").GetInt32() >= 1, "أحداث الدخول يجب أن تُسجّل");
    }

    [Fact]
    public async Task AuditLogins_CashierDenied_403()
    {
        var login = await _fx.LoginAsync("g3.cashier", "Cashier#G3-Pass");
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var response = await authed.GetAsync("/api/v1/audit/logins");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);   // RBAC خادمي مفروض
    }

    // ── الإعدادات: مصدر الحقيقة الوحيد + حالة الحسم الصادقة ───────────────

    [Fact]
    public async Task Settings_ByWeightCategory_ContainD07_OwnerConfirmed()
    {
        // الكاشير نطاقه فرع — دخوله بلا فرع يعني دوره لا ينطبق (فرض نطاق الفرع)،
        // لذا يدخل بنطاق فرعه كما في المحطة الحقيقية.
        var login = await _fx.LoginAsync("g3.cashier", "Cashier#G3-Pass", branchId: _fx.BranchId);
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var body = await authed.GetFromJsonAsync<JsonElement>("/api/v1/settings?category=WEIGHT");
        var items = body.GetProperty("items");
        Assert.Equal(4, body.GetProperty("count").GetInt32());

        var byKey = items.EnumerateArray().ToDictionary(i => i.GetProperty("settingKey").GetString()!);
        Assert.Equal("1", byKey["weighing.precision_g"].GetProperty("valueText").GetString());
        Assert.Equal("5", byKey["weighing.step_g"].GetProperty("valueText").GetString());
        Assert.Equal("OWNER_CONFIRMED", byKey["weighing.precision_g"].GetProperty("confirmationStatus").GetString());
    }

    [Fact]
    public async Task Settings_BranchScopedRoleWithoutBranch_IsDenied_403()
    {
        // فرض نطاق الفرع (§7.1): كاشير مقيد بفرع يدخل بلا نطاق فرع → دوره لا ينطبق
        var login = await _fx.LoginAsync("g3.cashier", "Cashier#G3-Pass", branchId: null);
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var response = await authed.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Settings_InventoryShowOwnerSettlements_V012()
    {
        var login = await _fx.LoginAsync("g3.owner", "Owner#G3-Pass");
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var body = await authed.GetFromJsonAsync<JsonElement>("/api/v1/settings?category=INVENTORY");
        var items = body.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("settingKey").GetString()!);

        // حسم D-02 (أ) وD-03 (ب) بأمر المالك 2026-11-02 — ترحيل V012
        Assert.Equal("WEIGHTED_AVERAGE", items["inventory.costing_method"].GetProperty("valueText").GetString());
        Assert.Equal("OWNER_CONFIRMED", items["inventory.costing_method"].GetProperty("confirmationStatus").GetString());
        Assert.Equal("SUPERVISOR_APPROVAL", items["inventory.negative_stock_policy"].GetProperty("valueText").GetString());
        Assert.Equal("OWNER_CONFIRMED", items["inventory.negative_stock_policy"].GetProperty("confirmationStatus").GetString());
    }

    // ── معرف الترابط (§11.1/§20) ───────────────────────────────────────────

    [Fact]
    public async Task CorrelationId_EchoedAndPropagatedToAudit()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "g3.owner", password = "WRONG", branchId = (long?)null, stationId = (long?)null }),
        };
        request.Headers.Add("X-Correlation-ID", "wera-test-correlation-123");
        var response = await _fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("wera-test-correlation-123", response.Headers.GetValues("X-Correlation-ID").First());

        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.audit_logs WHERE correlation_id = 'wera-test-correlation-123'", conn);
        Assert.True(Convert.ToInt64(await cmd.ExecuteScalarAsync()) >= 1, "معرف الترابط لم يصل سجل التدقيق");
    }

    [Fact]
    public async Task CorrelationId_Malformed_ReplacedWithGenerated()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", "معرف غير قانوني <script>");
        var response = await _fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = response.Headers.GetValues("X-Correlation-ID").First();
        Assert.Matches("^wera-[a-z0-9]+$", echoed);   // لا حقن — يولّد نظيفاً
    }

    // ── الجلسات وتجديدها (§11.2) ──────────────────────────────────────────

    [Fact]
    public async Task Refresh_RotatesToken_OldRefreshRejected()
    {
        var login = await _fx.LoginAsync("g3.owner", "Owner#G3-Pass");
        Assert.NotEmpty(login.RefreshToken);

        var refreshed = await _fx.Client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var fresh = await refreshed.Content.ReadFromJsonAsync<JsonElement>();

        // رمز التجديد عشوائي 256-bit — يتغير وجوباً مع كل تدوير.
        // (رمز الوصول قد يتطابق نصياً إذا أعيد إصداره في نفس الثانية بنفس الـclaims — سلوك JWT القياسي)
        Assert.NotEqual(login.RefreshToken, fresh.GetProperty("refreshToken").GetString());
        Assert.False(string.IsNullOrEmpty(fresh.GetProperty("accessToken").GetString()));

        // إعادة استخدام الرمز القديم بعد التدوير = مرفوضة (كشف السرقة)
        var reuse = await _fx.Client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Logout_ClosesSession_AndRevokesRefreshTokens()
    {
        var login = await _fx.LoginAsync("g3.owner", "Owner#G3-Pass");
        using var authed = _fx.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var logout = await authed.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // الجلسة أُغلقت في قاعدة البيانات ورموز التجديد أُلغيت (§11.2)
        await Task.Delay(200);
        var refreshAfterLogout = await _fx.Client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    // ── Health + OpenAPI (عقد v1 موثق) ─────────────────────────────────────

    [Fact]
    public async Task HealthLive_Returns200()
    {
        var response = await _fx.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_IncludesDatabaseCheck()
    {
        var response = await _fx.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("postgresql", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_DocumentExposesV1Contract()
    {
        var response = await _fx.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/api/v1/auth/login", paths);
        Assert.Contains("/api/v1/auth/me", paths);
        Assert.Contains("/api/v1/settings", paths);
        Assert.Contains("/api/v1/branches/{branchId}/stations", paths);
    }

    // ── عقد الاستجابة (Contract stability §11.1) ──────────────────────────

    [Fact]
    public async Task LoginResponse_ShapeIsStable()
    {
        var response = await _fx.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "g3.cashier", password = "Cashier#G3-Pass", branchId = _fx.BranchId, stationId = (long?)null });
        var login = await response.Content.ReadFromJsonAsync<JsonElement>();

        // العقود v1 مجمدة — أي نقص هنا كسر للعقد يستلزم v2
        foreach (var property in new[] { "accessToken", "expiresAtUtc", "refreshToken", "user" })
        {
            Assert.True(login.TryGetProperty(property, out _), $"الحقل {property} مفقود من عقد الاستجابة");
        }
        foreach (var property in new[] { "userId", "username", "displayNameAr", "roles", "permissions" })
        {
            Assert.True(login.GetProperty("user").TryGetProperty(property, out _), $"user.{property} مفقود من العقد");
        }
    }

    // ── عميلان متزامنان على خادم الاختبار (شرط بوابة G3) ──────────────────

    [Fact]
    public async Task TwoConcurrentClients_BothServed()
    {
        var owner = await _fx.LoginAsync("g3.owner", "Owner#G3-Pass");
        var cashier = await _fx.LoginAsync("g3.cashier", "Cashier#G3-Pass", branchId: _fx.BranchId);

        using var client1 = _fx.CreateClient();
        using var client2 = _fx.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cashier.AccessToken);

        var task1 = Enumerable.Range(0, 10).Select(_ => client1.GetAsync("/api/v1/settings"));
        var task2 = Enumerable.Range(0, 10).Select(_ => client2.GetAsync("/api/v1/branches"));
        var all = await Task.WhenAll(task1.Concat(task2));

        Assert.Equal(20, all.Length);
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

/// <summary>مصنع حي واحد لكل حزمة الاختبارات + تهيئة مستخدمي اختبار حقيقيين (bcrypt).</summary>
public sealed class LiveApiFixture : IAsyncLifetime
{
    public long BranchId { get; private set; }
    public long StationId { get; private set; }

    public WeraApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient();   // يقلع التطبيق ويطبق الترحيلات حتى رأس V014
        await SeedTestUsersAsync();
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task<Contracts.V1.LoginResponse> LoginAsync(string username, string password, long? branchId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password, branchId, stationId = (long?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contracts.V1.LoginResponse>())!;
    }

    private async Task SeedTestUsersAsync()
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();

        long ExecuteScalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        // فرع ومحطة اختبار (معرفات مولدة — لا إدراج صريح في ALWAYS IDENTITY)
        ExecuteScalar("INSERT INTO core.branches (code, name_ar, timezone) VALUES ('G3B1', 'فرع الاختبار G3', 'Africa/Cairo') ON CONFLICT (code) DO NOTHING");
        BranchId = ExecuteScalar("SELECT branch_id FROM core.branches WHERE code = 'G3B1'");
        ExecuteScalar($"INSERT INTO core.stations (branch_id, code, name_ar, is_pos) VALUES ({BranchId}, 'G3S1', 'محطة الاختبار G3', true) ON CONFLICT (branch_id, code) DO NOTHING");
        StationId = ExecuteScalar($"SELECT station_id FROM core.stations WHERE branch_id = {BranchId} AND code = 'G3S1'");

        long RoleId(string code) => ExecuteScalar($"SELECT role_id FROM core.roles WHERE code = '{code}'");

        void UpsertUser(string username, string display, string password, bool active, string roleCode, long? branchId)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
            ExecuteScalar($"INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('{username}', '{display}', '{hash}', {active.ToString().ToLowerInvariant()}) ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash, is_active = EXCLUDED.is_active");
            var userId = ExecuteScalar($"SELECT user_id FROM core.users WHERE username = '{username}'");
            ExecuteScalar($"DELETE FROM core.user_roles WHERE user_id = {userId}");
            var branchSql = branchId is null ? "NULL" : branchId.Value.ToString();
            ExecuteScalar($"INSERT INTO core.user_roles (user_id, role_id, branch_id) VALUES ({userId}, {RoleId(roleCode)}, {branchSql})");
        }

        UpsertUser("g3.owner", "مالك الاختبار", "Owner#G3-Pass", true, "OWNER", null);
        UpsertUser("g3.cashier", "كاشير الاختبار", "Cashier#G3-Pass", true, "CASHIER", BranchId);
        UpsertUser("g3.inactive", "مستخدم معطل", "Same#Pass-1", false, "CASHIER", null);
    }

    public async Task DisposeAsync()
    {
        // ترتيب الحذف يحترم الفواصل: التدقيق والجلسات ورموز التجديد ثم المستخدمون
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cleanup = new NpgsqlCommand(
            "DELETE FROM audit.audit_logs WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g3.%'); " +
            "DELETE FROM core.refresh_tokens WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g3.%'); " +
            "DELETE FROM audit.user_sessions WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g3.%'); " +
            "DELETE FROM core.user_roles WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g3.%'); " +
            "DELETE FROM core.users WHERE username LIKE 'g3.%'; " +
            "DELETE FROM core.stations WHERE branch_id = " + BranchId + " AND code = 'G3S1'; " +
            "DELETE FROM core.branches WHERE code = 'G3B1';", conn);
        await cleanup.ExecuteNonQueryAsync();
        Factory.Dispose();
    }
}
