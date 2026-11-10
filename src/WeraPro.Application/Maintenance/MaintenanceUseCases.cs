namespace WeraPro.Application.Maintenance;

using WeraPro.Application.Abstractions;
using WeraPro.Contracts.V1;

/// <summary>
/// النسخ الاحتياطي (PAGE-51 · §19 D-09): تشغيل موثق بسلسلة إسناد + تحقق قابلية
/// قراءة قبل إعلان النجاح + استعادة بتحذير صريح — النسخة على نفس القرص ليست
/// خطة استعادة، والواجهة تعلن ذلك (قاعدة الصدق).
/// </summary>
public sealed class BackupUseCase
{
    private readonly IBackupRepository _registry;
    private readonly IBackupStorage _storage;
    private readonly IAuditWriter _audit;

    public BackupUseCase(IBackupRepository registry, IBackupStorage storage, IAuditWriter audit)
    {
        _registry = registry;
        _storage = storage;
        _audit = audit;
    }

    public async Task<IReadOnlyList<BackupDto>> ListAsync(int limit, CancellationToken ct = default)
    {
        var rows = await _registry.ListAsync(limit is 0 ? 50 : limit, ct);
        return [.. rows.Select(r => new BackupDto(
            r.BackupId, r.FileName, r.Kind, r.Status, r.SizeBytes, r.CreatedAtUtc, r.CreatedBy, r.Checksum, r.NoteAr))];
    }

    public async Task<BackupDto> RunFullBackupAsync(RunBackupRequest request, long actorUserId, string actorName, CancellationToken ct = default)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"wera-full-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.dump";

        var backupId = await _registry.RegisterAsync(fileName, "FULL", 0, actorName, null, request.NoteAr, ct);
        try
        {
            var (actualFile, size, checksum) = await _storage.CreateFullBackupAsync(fileName, ct);
            var readable = await _storage.VerifyReadableAsync(actualFile, checksum, size, ct);
            await _registry.MarkStatusAsync(backupId, readable ? "VERIFIED" : "VERIFYING", ct);
            await _registry.UpdateSizeAsync(backupId, size, checksum, ct);

            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, null, "ops.backup_records", backupId,
                "BACKUP_RUN", readable ? "VERIFIED" : "UNVERIFIED",
                $"نسخة كاملة {actualFile} ({size} بايت)"), ct);

            return new BackupDto(backupId, actualFile, "FULL", readable ? "VERIFIED" : "VERIFYING",
                size, DateTimeOffset.UtcNow, actorName, checksum, request.NoteAr);
        }
        catch (Exception ex)
        {
            await _registry.MarkFailedAsync(backupId, ex.Message, ct);
            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, null, "ops.backup_records", backupId,
                "BACKUP_RUN", "FAILED", ex.Message), ct);
            throw;
        }
    }

    /// <summary>استعادة نسخة — عملية حساسة: تتحقق البصمة أولاً وتكتب في التدقيق قبل وبعد.</summary>
    public async Task<bool> RestoreAsync(RestoreBackupRequest request, long actorUserId, CancellationToken ct = default)
    {
        if (!string.Equals(request.ConfirmText, "استعادة", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("عبارة التأكيد يجب أن تكون «استعادة» حرفياً");
        }
        var backup = await _registry.FindByIdAsync(request.BackupId, ct)
            ?? throw new InvalidOperationException("النسخة غير موجودة في السجل");
        if (backup.Status is not ("VERIFIED" or "SUCCEEDED"))
        {
            throw new InvalidOperationException("لا تُستعاد إلا نسخة موثقة قابلة للقراءة (VERIFIED)");
        }

        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, null, "ops.backup_records", backup.BackupId,
            "BACKUP_RESTORE_BEGIN", "OK", $"استعادة {backup.FileName}" + (request.ReasonAr is null ? "" : $" — {request.ReasonAr}")), ct);

        await _storage.RestoreAsync(backup.FileName, ct);

        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, null, "ops.backup_records", backup.BackupId,
            "BACKUP_RESTORE_DONE", "OK", "اكتمال الاستعادة — أعد تشغيل العملاء"), ct);
        return true;
    }

    public async Task<bool> DeleteRegistryAsync(long backupId, long actorUserId, CancellationToken ct = default)
    {
        var ok = await _registry.DeleteRegistryAsync(backupId, ct);
        if (ok)
        {
            await _audit.WriteAsync(new AuditEntry(
                actorUserId, null, null, null, "ops.backup_records", backupId,
                "BACKUP_DELETE", "OK", "حذف سجل النسخة (الملف الفعلي يُدار يدوياً)"), ct);
        }
        return ok;
    }
}

/// <summary>تنظيف النظام (PAGE-51): تدقيق قديم وجلسات معلقة ووسائط يتيمة — كل أثر موثق.</summary>
public sealed class SystemCleanupUseCase
{
    private readonly ISystemMaintenanceRepository _maintenance;
    private readonly IAuditWriter _audit;

    public SystemCleanupUseCase(ISystemMaintenanceRepository maintenance, IAuditWriter audit)
    {
        _maintenance = maintenance;
        _audit = audit;
    }

    public Task<SystemStats> GetStatsAsync(CancellationToken ct = default) => _maintenance.GetStatsAsync(ct);

    public async Task<SystemCleanupResult> ExecuteAsync(SystemCleanupRequest request, long actorUserId, CancellationToken ct = default)
    {
        var days = request.RetentionDays is < 7 or > 3650 ? 180 : request.RetentionDays;

        var purgedAudit = await _maintenance.PurgeAuditEventsAsync(days, ct);
        var closedSessions = await _maintenance.CloseStaleSessionsAsync(days, ct);
        var removedMedia = await _maintenance.RemoveOrphanMediaAsync(days, ct);
        var purgedJobs = await _maintenance.PurgeCompletedPrintJobsAsync(days, ct);

        var result = new SystemCleanupResult(purgedAudit, closedSessions, removedMedia, purgedJobs, DateTimeOffset.UtcNow);
        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, null, "ops", 0, "SYSTEM_CLEANUP", "OK",
            $"تنظيف (احتفاظ {days} يوماً): تدقيق {purgedAudit} · جلسات {closedSessions} · وسائط يتيمة {removedMedia} · مهام طباعة {purgedJobs}"), ct);
        return result;
    }
}

/// <summary>
/// الحذف الكامل للبيانات (منطقة الخطر): يفرغ كل بيانات التشغيل بإعادة تعيين
/// التسلسل — مع إبقاء الهوية (المستخدمون/الأدوار) والإعدادات والهيكل التنظيمي.
/// عبارة تأكيد إلزامية «حذف نهائي» + توثيق كامل بما حُذف وما بقي.
/// </summary>
public sealed class WipeDataUseCase
{
    private const string ConfirmPhrase = "حذف نهائي";

    private readonly ISystemMaintenanceRepository _maintenance;
    private readonly IAuditWriter _audit;

    public WipeDataUseCase(ISystemMaintenanceRepository maintenance, IAuditWriter audit)
    {
        _maintenance = maintenance;
        _audit = audit;
    }

    public async Task<WipeDataResult> ExecuteAsync(WipeDataRequest request, long actorUserId, CancellationToken ct = default)
    {
        if (!string.Equals(request.ConfirmText?.Trim(), ConfirmPhrase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"عبارة التأكيد يجب أن تكون «{ConfirmPhrase}» حرفياً — الحماية من التنفيذ الخاطئ");
        }

        // توثيق ما قبل الحذف — إحصاءات اللحظة الأخيرة تبقى في التدقيق الذي لا يُفرغ مع البيانات
        var before = await _maintenance.GetStatsAsync(ct);
        var truncated = await _maintenance.WipeBusinessDataAsync(ct);

        await _audit.WriteAsync(new AuditEntry(
            actorUserId, null, null, null, "ops", 0, "SYSTEM_WIPE", "OK",
            $"حذف كامل: {truncated.Count} جدولاً (منتجات {before.Products} · حركات {before.StockMovements} · فواتير {before.Invoices})" +
            (request.ReasonAr is null ? "" : $" — {request.ReasonAr}")), ct);

        return new WipeDataResult(
            truncated.Count,
            ["core (المستخدمون والأدوار والإعدادات)", "الهيكل (الفروع والمحطات والمخازن)", "دليل الحسابات والخزائن", "إعدادات الطابعات والأجهزة"],
            DateTimeOffset.UtcNow,
            "حُذفت كل بيانات التشغيل نهائياً ولا يمكن التراجع إلا باستعادة نسخة احتياطية موثقة");
    }
}
