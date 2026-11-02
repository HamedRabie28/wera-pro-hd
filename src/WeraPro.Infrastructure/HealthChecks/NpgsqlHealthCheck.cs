namespace WeraPro.Infrastructure.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

/// <summary>
/// فحص جاهزية قاعدة البيانات — SELECT خفيف + إصدار الخادم؛ يفشل الفحص عند
/// أي استثناء اتصال (لا «جاهز» زوراً).
/// </summary>
public sealed class NpgsqlHealthCheck : IHealthCheck
{
    public const string Name = "postgresql";

    private readonly string _connectionString;

    public NpgsqlHealthCheck(string connectionString) => _connectionString = connectionString;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT version()", conn);
            var version = (string)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            return HealthCheckResult.Healthy(
                description: "اتصال قاعدة البيانات يعمل",
                data: new Dictionary<string, object> { ["server_version"] = version });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: "تعذر الوصول إلى قاعدة البيانات",
                exception: ex);
        }
    }
}
