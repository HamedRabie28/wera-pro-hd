namespace WeraPro.Infrastructure.Media;

using System.IO;
using System.Globalization;
using Microsoft.Extensions.Options;
using WeraPro.Application.Media;
using WeraPro.Domain.Media;

/// <summary>إعدادات مخزن الوسائط — الجذر من البيئة/الإعدادات (لا مسار مطلق في الكود §6.1).</summary>
public sealed class MediaStorageOptions
{
    public const string SectionName = "Media";

    /// <summary>جذر التخزين (نسبي لجذر المحتوى أو مطلق من البيئة) — الافتراضي media-store.</summary>
    public string StorageRoot { get; set; } = "media-store";
}

/// <summary>
/// تخزين ملفات محلي بمفاتيح مُدارة (G5): كل مفتاح يمر عبر
/// <see cref="MediaStorageKeys.IsManagedKey"/> ثم عبر فحص المسار المطلق
/// (يجب أن يبقى تحت الجذر) — اجتياز المسار مستحيل بالمconstruction.
/// كتابة ذرية (مؤقت + Move) — لا حالات نصف مكتوبة.
/// </summary>
public sealed class LocalFileStorage : IMediaStorage
{
    private readonly string _root;

    public LocalFileStorage(IOptions<MediaStorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.StorageRoot);
        Directory.CreateDirectory(_root);
    }

    /// <summary>للدليل الجذر — تشخيص فقط، لا يُستخدم في المفاتيح.</summary>
    public string Root => _root;

    public long? GetAvailableFreeBytes()
    {
        try
        {
            return new DriveInfo(_root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // غير معروفة (fs بلا driveInfo) — تُسمح الرفعات (تصريح موثق §6.1)
            return null;
        }
    }

    public async Task WriteAsync(string managedKey, Stream content, CancellationToken ct = default)
    {
        var path = ResolveManagedPath(managedKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // كتابة ذرية: مؤقت في نفس الدليل ثم Move فوق الهدف
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var target = File.Create(temp, 64 * 1024, FileOptions.Asynchronous))
            {
                content.Seek(0, SeekOrigin.Begin);
                await content.CopyToAsync(target, 64 * 1024, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (IOException) { /* أفضل جهد */ }
            }
        }
    }

    public Task<Stream> OpenReadAsync(string managedKey, CancellationToken ct = default)
    {
        var path = ResolveManagedPath(managedKey);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ملف التخزين المُدار غير موجود", path);
        }
        return Task.FromResult<Stream>(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public Task<bool> DeleteAsync(string managedKey, CancellationToken ct = default)
    {
        var path = ResolveManagedPath(managedKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }
        File.Delete(path);
        return Task.FromResult(true);
    }

    public bool Exists(string managedKey)
    {
        var path = ResolveManagedPath(managedKey);
        return File.Exists(path);
    }

    public void SweepStaging(long jobId)
    {
        // staging/ فقط — يُرفض أي جذر آخر صراحة
        var dir = Path.Combine(_root, "staging", jobId.ToString("D10", System.Globalization.CultureInfo.InvariantCulture));
        var stagingRoot = Path.GetFullPath(Path.Combine(_root, "staging"));
        var full = Path.GetFullPath(dir);
        if (!full.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("الكنس يقبل staging/ فقط");
        }
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    /// <summary>مفتاح مُدار → مسار مطلق تحت الجذر — حارسان: النمط ثم المسار.</summary>
    private string ResolveManagedPath(string managedKey)
    {
        if (!MediaStorageKeys.IsManagedKey(managedKey))
        {
            throw new InvalidOperationException("مفتاح تخزين غير مُدار (مرفوض): " + managedKey);
        }
        var full = Path.GetFullPath(Path.Combine(_root, managedKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("اجتياز مسار مرفوض");
        }
        return full;
    }
}
