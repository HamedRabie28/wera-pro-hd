namespace WeraPro.Infrastructure.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WeraPro.Infrastructure.HealthChecks;
using WeraPro.Infrastructure.Migrations;
using Xunit;

/// <summary>
/// اختبارات عدّاء الترحيلات الموازي لـrun_migrations.py (ADR-009):
/// نفس ملفات SQL، نفس بصمات sha256، نفس قواعد Idempotent/Forward-Fix.
/// تعمل على PostgreSQL حقيقي (بيئة الاختبار localhost:55432 trust).
/// </summary>
public sealed class SqlMigrationRunnerTests : IAsyncLifetime
{
    // قاعدة فريدة لكل اختبار (IAsyncLifetime لكل طريقة) — لا DROP متزامن يقتل تجمعات الاتصال
    private readonly string _scratchDb = $"wera_g3_scratch_{Guid.NewGuid().ToString("N")[..10]}";
    private string ScratchUri => $"Host=localhost;Port=55432;Username=postgres;Database={_scratchDb}";
    private const string AdminUri = "Host=localhost;Port=55432;Username=postgres;Database=postgres";
    private static readonly SqlMigrationRunner Runner = new(NullLogger<SqlMigrationRunner>.Instance);

    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(AdminUri);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_scratchDb}\"", admin);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        // تفريغ التجمعات أولاً حتى لا يقتل DROP اتصالات مستخدمة (57P01)
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminUri);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_scratchDb}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task FreshDatabase_AppliesAllMigrations_HeadIsV18()
    {
        var outcome = await Runner.ApplyPendingAsync(ScratchUri);

        Assert.Equal(18, outcome.Head);
        Assert.Equal(18, outcome.Applied);
        Assert.Equal(0, outcome.SkippedAlready);
        await AssertMigrationCountAsync(18);
    }

    [Fact]
    public async Task ReRun_IsIdempotent_AllSkipped()
    {
        await Runner.ApplyPendingAsync(ScratchUri);
        var second = await Runner.ApplyPendingAsync(ScratchUri);

        Assert.Equal(0, second.Applied);
        Assert.Equal(18, second.SkippedAlready);
        Assert.Equal(18, second.Head);
    }

    [Fact]
    public async Task ModifiedAppliedMigration_IsRejected_ForwardFixOnly()
    {
        await Runner.ApplyPendingAsync(ScratchUri);

        // تلاعب ببصمة نسخة مطبقة (يحاكي تعديل ملف ترحيل بعد التطبيق)
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var tamper = new NpgsqlCommand(
            "UPDATE core.schema_migrations SET checksum = 'tampered' WHERE version = 1", conn);
        await tamper.ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Runner.ApplyPendingAsync(ScratchUri));
        Assert.Contains("Forward Fix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checksums_MatchPythonRunnerConvention_Sha256OfUtf8File()
    {
        await Runner.ApplyPendingAsync(ScratchUri);

        var migrations = SqlMigrationRunner.LoadEmbeddedMigrations();
        Assert.NotEmpty(migrations);

        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM core.schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var fromDb = new Dictionary<int, string>();
        while (await reader.ReadAsync())
        {
            fromDb[reader.GetInt32(0)] = reader.GetString(1);
        }

        foreach (var (version, name, sql) in migrations)
        {
            var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            Assert.Equal(expected, fromDb[version]);   // نفس بصمة run_migrations.py حرفياً
        }
    }

    [Fact]
    public void EmbeddedMigrations_AreOrdered_WithNoDuplicates()
    {
        var migrations = SqlMigrationRunner.LoadEmbeddedMigrations();
        var versions = migrations.Select(m => m.Version).ToList();

        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Contains(migrations, m => m.Name.Contains("V001__"));
        Assert.Contains(migrations, m => m.Name.Contains("V014__"));
        Assert.Contains(migrations, m => m.Name.Contains("V016__"));
    }

    private async Task AssertMigrationCountAsync(int expected)
    {
        await using var conn = new NpgsqlConnection(ScratchUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM core.schema_migrations", conn);
        Assert.Equal(expected, Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }
}

/// <summary>فحص جاهزية قاعدة البيانات — حقيقي مع DB شغالة وUnhealthy مع اتصال ميت.</summary>
public sealed class NpgsqlHealthCheckTests
{
    private const string LiveUri = "Host=localhost;Port=55432;Username=postgres;Database=postgres";

    [Fact]
    public async Task LiveDatabase_ReportsHealthy_WithServerVersion()
    {
        var check = new NpgsqlHealthCheck(LiveUri);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(result.Data.ContainsKey("server_version"));
        Assert.Contains("PostgreSQL 16", (string)result.Data["server_version"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeadConnection_ReportsUnhealthy()
    {
        var check = new NpgsqlHealthCheck("Host=localhost;Port=59999;Username=postgres;Database=dead;Timeout=3");
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
