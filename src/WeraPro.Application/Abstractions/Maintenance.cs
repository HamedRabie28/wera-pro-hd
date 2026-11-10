namespace WeraPro.Application.Abstractions;

// ── النسخ الاحتياطي والصيانة (PAGE-51 · §19 D-09) ────────────────────────────

/// <summary>إحصاءات النظام لصفحة الصيانة — أرقام حقيقية قبل أي قرار حذف.</summary>
public sealed record SystemStats(
    long Products, long Skus, long StockMovements, long Invoices,
    long MediaAssets, long AuditEvents, long OpenSessions, long PrintJobs,
    long BackupRecords, long Users, string DatabaseName);

/// <summary>التنفيذ الفيزيائي للنسخ/الاستعادة (pg_dump/pg_restore) — معزول كمنفذ.</summary>
public interface IBackupStorage
{
    /// <summary>إنشاء ملف نسخة كاملة باسم محدد وإرجاع حجمه وبصمته SHA-256.</summary>
    Task<(string FileName, long SizeBytes, string Checksum)> CreateFullBackupAsync(string fileName, CancellationToken ct = default);

    /// <summary>استعادة نسخة بملفها الفعلي — تفشل برسالة عربية واضحة إن تعذّرت.</summary>
    Task RestoreAsync(string fileName, CancellationToken ct = default);

    /// <summary>تحقق قابلية القراءة (§19: لا يعلن النجاح إلا بعد تحقق) — بصمة + تحقق الحجم.</summary>
    Task<bool> VerifyReadableAsync(string fileName, string expectedChecksum, long expectedSize, CancellationToken ct = default);
}

/// <summary>عمليات صيانة خام عبر SQL — تنفيقه Infrastructure على اتصال قاعدة البيانات.</summary>
public interface ISystemMaintenanceRepository
{
    Task<SystemStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>حذف كل بيانات التشغيل (مخزون/مبيعات/مشتريات/مالية/وسائط/تدقيق) —
    /// مع إبقاء الهوية والأدوار والإعدادات والفروع والمخازن ودليل الحسابات.
    /// يعيد أسماء الجداول المفرَّغة فعلياً للتوثيق.</summary>
    Task<IReadOnlyList<string>> WipeBusinessDataAsync(CancellationToken ct = default);

    Task<int> PurgeAuditEventsAsync(int retentionDays, CancellationToken ct = default);
    Task<int> CloseStaleSessionsAsync(int retentionDays, CancellationToken ct = default);
    Task<int> RemoveOrphanMediaAsync(int retentionDays, CancellationToken ct = default);
    Task<int> PurgeCompletedPrintJobsAsync(int retentionDays, CancellationToken ct = default);
}
