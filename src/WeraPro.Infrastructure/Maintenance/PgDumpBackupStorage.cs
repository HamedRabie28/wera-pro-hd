namespace WeraPro.Infrastructure.Maintenance;

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeraPro.Application.Abstractions;

/// <summary>خيارات النسخ الاحتياطي — مسار الأدوات والمجلد من الإعدادات لا الكود.</summary>
public sealed record BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>مسار pg_dump — الاسم المجرد يعتمد PATH أو المسار الكامل لتثبيت PostgreSQL.</summary>
    public string PgDumpPath { get; init; } = "pg_dump";

    /// <summary>مسار pg_restore.</summary>
    public string PgRestorePath { get; init; } = "pg_restore";

    /// <summary>مجلد ملفات النسخ (يُنشأ إن لم يوجد) — نسخة القرص ليست خطة استعادة (§19).</summary>
    public string Directory { get; init; } = "backups";

    /// <summary>سلسلة اتصال قاعدة البيانات بصيغة Npgsql (تُحقن من ConnectionStrings:WeraDatabase).</summary>
    public string ConnectionString { get; init; } = "";
}

/// <summary>
/// التنفيذ الفيزيائي للنسخ: pg_dump بتنسيق custom + بصمة SHA-256، والاستعادة
/// pg_restore --clean. سلسلة Npgsql تُفكك إلى معاملات libpq مستقلة (Host= → --host)
/// وكلمة المرور عبر PGPASSWORD لا سطر الأوامر. الفشل برسالة عربية واضحة.
/// </summary>
public sealed class PgDumpBackupStorage : IBackupStorage
{
    private readonly BackupOptions _options;
    private readonly ILogger<PgDumpBackupStorage> _logger;

    public PgDumpBackupStorage(IOptions<BackupOptions> options, ILogger<PgDumpBackupStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(string FileName, long SizeBytes, string Checksum)> CreateFullBackupAsync(string fileName, CancellationToken ct = default)
    {
        var dir = Directory.CreateDirectory(_options.Directory);
        var path = Path.Combine(dir.FullName, fileName);

        var args = new List<string>
        {
            "--format=custom",
            "--file", path,
            "--host", Conn.Host,
            "--port", Conn.Port,
        };
        if (Conn.User is not null) args.AddRange(["--username", Conn.User]);
        args.Add(Conn.Database);

        var (code, error) = await RunAsync(_options.PgDumpPath, args, ct);
        if (code != 0 || !File.Exists(path))
        {
            _logger.LogError("pg_dump فشل برمز {Code}: {Error}", code, error);
            throw new InvalidOperationException(
                $"فشل إنشاء النسخة الاحتياطية (pg_dump رمز {code}) — تأكد من تثبيت أدوات PostgreSQL وضبط Backup:PgDumpPath. التفصيل: {Truncate(error)}");
        }

        await using var stream = File.OpenRead(path);
        var checksum = await SHA256.HashDataAsync(stream, ct);
        return (fileName, new FileInfo(path).Length, Convert.ToHexString(checksum).ToLowerInvariant());
    }

    public async Task RestoreAsync(string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(_options.Directory, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"ملف النسخة «{fileName}» غير موجود في مجلد النسخ — لا استعادة بلا مصدر موثق");
        }
        var args = new List<string>
        {
            "--clean", "--if-exists", "--no-owner", "--no-privileges",
            "--host", Conn.Host,
            "--port", Conn.Port,
        };
        if (Conn.User is not null) args.AddRange(["--username", Conn.User]);
        args.Add(path);
        args.Add(Conn.Database);

        var (code, error) = await RunAsync(_options.PgRestorePath, args, ct);
        if (code != 0)
        {
            _logger.LogError("pg_restore فشل برمز {Code}: {Error}", code, error);
            throw new InvalidOperationException($"فشل الاستعادة (pg_restore رمز {code}) — التفصيل: {Truncate(error)}");
        }
    }

    public async Task<bool> VerifyReadableAsync(string fileName, string expectedChecksum, long expectedSize, CancellationToken ct = default)
    {
        var path = Path.Combine(_options.Directory, fileName);
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize) return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedConnection(string Host, string Port, string? User, string? Database, string? Password);

    private ParsedConnection? _parsed;
    /// <summary>تفكيك سلسلة Npgsql إلى معاملات libpq — مرة واحدة لكل عملية (مُخزّنة).</summary>
    private ParsedConnection Conn => _parsed ??= Parse(_options.ConnectionString);

    private static ParsedConnection Parse(string connectionString)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) map[kv[0].Trim()] = kv[1].Trim().Trim('"');
        }
        string? Get(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (map.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }
        var database = Get("Database", "Db", "Database Name")
            ?? throw new InvalidOperationException("سلسلة الاتصال لا تحدد اسم قاعدة البيانات — لا نسخ احتياطي بلا هدف واضح");
        return new ParsedConnection(
            Get("Host", "Server", "Data Source") ?? "localhost",
            Get("Port") ?? "5432",
            Get("Username", "User Id", "User", "UserId"),
            database,
            Get("Password", "Pwd"));
    }

    private async Task<(int Code, string Error)> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // كلمة المرور عبر متغير البيئة فقط — لا تظهر في سطر الأوامر أو السجلات (§18)
        if (Conn.Password is not null)
        {
            psi.Environment["PGPASSWORD"] = Conn.Password;
        }
        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"تعذر تشغيل {exe} — تحقق من المسار في إعدادات Backup");
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (127, $"البرنامج غير موجود: {ex.Message}");
        }
    }

    private static string Truncate(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "بدون تفصيل" : text.ReplaceLineEndings(" ")[..Math.Min(text.ReplaceLineEndings(" ").Length, 220)];
}
