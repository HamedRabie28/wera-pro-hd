namespace WeraPro.Infrastructure.Migrations;

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// عدّاء الترحيلات الموازي لـdatabase/scripts/run_migrations.py (ADR-009):
/// يقرأ ملفات SQL الخطية V###__*.sql المضمّنة في التجميعة (من database/migrations
/// مباشرة — المصدر الواحد)، يطبقها بالترتيب داخل معاملة لكل ملف، ويسجل كل نسخة
/// في core.schema_migrations ببصمة sha256 لنفس محتوى الملف.
/// القواعد الحرفية نفسها:
///   - لا يطبق نسخة مسجلة سابقاً (Idempotent).
///   - يرفض بصمة مختلفة لنفس النسخة (تعديل ترحيل مطبق ممنوع — Forward Fix فقط).
/// </summary>
public sealed partial class SqlMigrationRunner
{
    private const string ResourcePrefix = "WeraPro.Infrastructure.Migrations.";
    private static readonly ConcurrentDictionary<string, IReadOnlyList<EmbeddedMigration>> Cache = new();

    private readonly ILogger<SqlMigrationRunner> _logger;

    public SqlMigrationRunner(ILogger<SqlMigrationRunner> logger) => _logger = logger;

    /// <summary>ترحيل مضمّن واحد: رقم النسخة واسم الملف ونص SQL.</summary>
    public sealed record EmbeddedMigration(int Version, string Name, string Sql);

    /// <summary>يقرأ ملفات الترحيل المضمّنة مرتبة بالنسخة (كاش واحد لكل تجميعة).</summary>
    public static IReadOnlyList<EmbeddedMigration> LoadEmbeddedMigrations()
    {
        return Cache.GetOrAdd(typeof(SqlMigrationRunner).Assembly.FullName!, _ =>
        {
            var asm = typeof(SqlMigrationRunner).Assembly;
            var list = new List<EmbeddedMigration>();
            foreach (var name in asm.GetManifestResourceNames()
                         .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                var fileName = name[ResourcePrefix.Length..];
                var dash = fileName.IndexOf("__", StringComparison.Ordinal);
                if (dash <= 1 || !int.TryParse(fileName[1..dash], out var version))
                {
                    throw new InvalidOperationException($"اسم ملف ترحيل غير قانوني: {fileName}");
                }
                using var stream = asm.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"مورد غير قابل للقراءة: {name}");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                list.Add(new EmbeddedMigration(version, fileName, reader.ReadToEnd()));
            }
            if (list.Count == 0)
            {
                throw new InvalidOperationException("لا توجد ملفات ترحيل مضمّنة");
            }
            if (list.Select(x => x.Version).Distinct().Count() != list.Count)
            {
                throw new InvalidOperationException("أرقام نسخ ترحيل مكررة");
            }
            return (IReadOnlyList<EmbeddedMigration>)[.. list.OrderBy(x => x.Version)];
        });
    }

    public static string Sha256Hex(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    public sealed record MigrationOutcome(int Applied, int SkippedAlready, int Head);

    /// <summary>
    /// يطبق الترحيلات المعلقة على قاعدة معطاة — يُستدعى عند إقلاع API وقبل الاختبارات.
    /// لا يستخدم سلاسل اتصال من العميل مطلقاً — إعداد خادم فقط.
    /// قفل استشاري على مستوى الجلسة (pg_advisory_lock) يسلسل أي إقلاعين متوازيين
    /// على نفس القاعدة (اختبارات متوازية أو نسخ خادم متعددة) — الثّاني ينتظر ثم
    /// يجد الرأس مطبقاً فيتخطاه بسلام.
    /// </summary>
    /// <summary>مفتاح القفل الاستشاري — 0x57455241 = "WERA".</summary>
    private const long AdvisoryLockKey = 0x57455241L;

    public async Task<MigrationOutcome> ApplyPendingAsync(string connectionString, CancellationToken ct = default)
    {
        var migrations = LoadEmbeddedMigrations();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // تسلسل الإقلاعات المتوازية على نفس القاعدة — يُحرر في finally أو بإغلاق الجلسة
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("k", AdvisoryLockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            return await ApplyUnderLockAsync(conn, migrations, ct);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", AdvisoryLockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<MigrationOutcome> ApplyUnderLockAsync(
        NpgsqlConnection conn, IReadOnlyList<EmbeddedMigration> migrations, CancellationToken ct)
    {
        var applied = new Dictionary<int, string>();
        // V001 هو من ينشئ core.schema_migrations — على قاعدة نظيفة تماماً الجدول غير موجود بعد.
        await using (var exists = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables " +
            "WHERE table_schema='core' AND table_name='schema_migrations')", conn))
        {
            var hasTable = (bool)(await exists.ExecuteScalarAsync(ct))!;
            if (hasTable)
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT version, checksum FROM core.schema_migrations ORDER BY version", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    applied[reader.GetInt32(0)] = reader.GetString(1);
                }
            }
        }

        int appliedNow = 0, skipped = 0, head = applied.Keys.DefaultIfEmpty(0).Max();
        foreach (var (version, name, sql) in migrations)
        {
            var checksum = Sha256Hex(sql);
            if (applied.TryGetValue(version, out var existing))
            {
                if (!string.Equals(existing, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"ترحيل V{version} مسجل ببصمة مختلفة — تعديل ترحيل مطبق ممنوع (سياسة Forward Fix): {name}");
                }
                skipped++;
                continue;
            }

            // مثل run_migrations.py حرفياً: الملف يُنفَّذ دفعة واحدة (بمعاملته الضمنية
            // أو بمعاملته الصريحة BEGIN/COMMIT داخل الملف نفسه) ثم يُسجَّل — بلا
            // معاملة خارجية (COMMIT داخل ملف يغلقها ويكسر تسجيل النسخة).
            await using (var ddl = new NpgsqlCommand(sql, conn))
            {
                await ddl.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO core.schema_migrations (version, name, checksum) VALUES ($1, $2, $3)", conn))
            {
                ins.Parameters.AddWithValue(version);
                ins.Parameters.AddWithValue(name);
                ins.Parameters.AddWithValue(checksum);
                await ins.ExecuteNonQueryAsync(ct);
            }
            head = version;
            appliedNow++;
            LogMigrationApplied(version, name);
        }

        LogMigrationSummary(appliedNow, skipped, head);
        return new MigrationOutcome(appliedNow, skipped, head);
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Migration V{Version} {Name} APPLIED")]
    private partial void LogMigrationApplied(int version, string name);

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Migration runner: applied={Applied} already={Skipped} head=V{Head}")]
    private partial void LogMigrationSummary(int applied, int skipped, int head);
}
