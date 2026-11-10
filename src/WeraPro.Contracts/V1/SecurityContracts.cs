namespace WeraPro.Contracts.V1;

// ── إدارة المستخدمين والأدوار (PAGE-47 · بوابة G3+) ─────────────────────────
// المستخدمون: إنشاء/تعديل/تعطيل/كلمة مرور + إسناد الأدوار بنطاق فرع أو الكل.
// الأدوار المرجعية السبعة صلبة (V009) — الإسناد والتجميد فقط هنا، لا تحرير أدوار.

/// <summary>مستخدم للعرض الإداري — بلا أي hash أو أسرار (§18).</summary>
public sealed record UserAdminDto(
    long UserId,
    string Username,
    string DisplayNameAr,
    string? Email,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> RoleCodes,
    IReadOnlyList<string> RoleNamesAr);

/// <summary>دور مرجعي مع صلاحياته الذرية.</summary>
public sealed record RoleDetailDto(
    long RoleId,
    string Code,
    string NameAr,
    string? Description,
    IReadOnlyList<PermissionDto> Permissions);

/// <summary>صلاحية ذرية (security.* / catalog.* / ...).</summary>
public sealed record PermissionDto(string Code, string DomainArea, string DescriptionAr);

/// <summary>إنشاء مستخدم — كلمة المرور تُجزَّأ bcrypt على الخادم ولا تُخزَّن نصاً.</summary>
public sealed record CreateUserRequest(
    string Username,
    string DisplayNameAr,
    string? Email,
    string Password,
    string RoleCode,
    long? BranchId);

/// <summary>تعديل بيانات مستخدم (الاسم/البريد) — اسم الدخول غير قابل للتغيير.</summary>
public sealed record UpdateUserRequest(string DisplayNameAr, string? Email);

/// <summary>إسناد دور لمستخدم داخل نطاق فرع (null = كل الفروع).</summary>
public sealed record AssignRoleRequest(string RoleCode, long? BranchId, string? ReasonAr);

/// <summary>إسناد دور كما يعود من الخادم — للعرض والفك بمعرّف السطر.</summary>
public sealed record UserRoleAssignmentDto(
    long UserRoleId, string RoleCode, string RoleNameAr, long? BranchId, DateTimeOffset GrantedAt);

/// <summary>فك إسناد دور محدد.</summary>
public sealed record RevokeRoleRequest(long UserRoleId, string? ReasonAr);

/// <summary>إعادة تعيين كلمة المرور — تجزئة جديدة وتسجيل تدقيق.</summary>
public sealed record ResetPasswordRequest(string NewPassword, string? ReasonAr);

/// <summary>تفعيل/تعطيل مستخدم — التعطيل لا الحذف (سجل الدخول محفوظ).</summary>
public sealed record SetUserActiveRequest(bool IsActive, string? ReasonAr);

// ── الإعدادات (PAGE-52) ─────────────────────────────────────────────────────
// التعديل بصلاحية ops.settings.manage (OWNER) — القيمة إعداد نشاط لا ثابت كود.

/// <summary>تعديل قيمة إعداد قائم بالمفتاح (BUSINESS فقط — إعدادات الفرع لاحقاً).</summary>
public sealed record UpdateSettingRequest(string ValueText, string? ReasonAr);

public sealed record UpdateSettingResponse(
    string SettingKey,
    string ValueText,
    string ConfirmationStatus,
    DateTimeOffset UpdatedAtUtc);

// ── النسخ الاحتياطي والصيانة (PAGE-51 + منطقة الخطر) ────────────────────────
// نسخ احتياطية موثقة بسلسلة إسناد + استعادة بتحذير + حذف بيانات كامل بضوابط مشددة.

/// <summary>نسخة احتياطية موثقة في السجل (system.backups).</summary>
public sealed record BackupDto(
    long BackupId,
    string FileName,
    string Kind,
    string Status,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    string? Checksum,
    string? NoteAr);

/// <summary>طلب تشغيل نسخة احتياطية فورية.</summary>
public sealed record RunBackupRequest(string? NoteAr);

/// <summary>طلب استعادة نسخة — يتطلب تأكيداً صريحاً من الواجهة.</summary>
public sealed record RestoreBackupRequest(long BackupId, string ConfirmText, string? ReasonAr);

/// <summary>طلب تنظيف النظام (سجلات قديمة/وسائط يتيمة/جلسات منتهية).</summary>
public sealed record SystemCleanupRequest(int RetentionDays, string? ReasonAr);

/// <summary>نتيجة التنظيف — أثر كل مجموعة موثق بالأرقام.</summary>
public sealed record SystemCleanupResult(
    int PurgedAuditEvents,
    int ClosedStaleSessions,
    int RemovedOrphanMedia,
    int ArchivedCompletedJobs,
    DateTimeOffset RanAtUtc);

/// <summary>طلب الحذف الكامل للبيانات — عبارة التأكيد الإلزامية «حذف نهائي».</summary>
public sealed record WipeDataRequest(string ConfirmText, string? ReasonAr);

/// <summary>نتيجة الحذف الكامل — ما حُذف وما بقي (المستخدمون يبقون للدخول).</summary>
public sealed record WipeDataResult(
    int TruncatedTables,
    IReadOnlyList<string> PreservedSchemas,
    DateTimeOffset WipedAtUtc,
    string WarningAr);
