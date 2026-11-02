namespace WeraPro.Hardware.Abstractions;

/// <summary>
/// حالة اتصال الجهاز (قواعد الحالة الموحدة: CONNECTED/DISCONNECTED/UNKNOWN/
/// NOT_CONFIGURED/ERROR) — لا يُعرض جهاز «متصل» زوراً (§10.6).
/// </summary>
public enum DeviceConnectionState
{
    Unknown = 0,
    Connected,
    Disconnected,
    NotConfigured,
    Error,
}

/// <summary>نتيجة عملية جهاز — ناجحة بقيمة حقيقية، أو فاشلة بسبب معلن، أو UNKNOWN بصدق.</summary>
public sealed record DeviceResult<T>
{
    public required DeviceConnectionState State { get; init; }
    public T? Value { get; init; }
    public string? FailureReasonAr { get; init; }
    public DateTimeOffset? ReadAtUtc { get; init; }
    public bool HasRealValue => State == DeviceConnectionState.Connected && Value is not null;
}

/// <summary>قراءة ميزان بالجرامات مع لحظة القراءة.</summary>
public sealed record ScaleReading(decimal Grams, DateTimeOffset ReadAtUtc);

/// <summary>شريط باركود مقروء (فك الترميز على الخادم أو العميل حسب الجهاز).</summary>
public sealed record BarcodeRead(string RawPayload, DateTimeOffset ReadAtUtc);

/// <summary>حدث نتيجة طباعة (نجاح/فشل مع كود حالة الطابعة).</summary>
public sealed record PrintOutcome(bool Printed, string? PrinterStatusText, int? JobId);

/// <summary>حدث فتح درج النقد.</summary>
public sealed record CashDrawerOutcome(bool Opened, string? FailureReasonAr);

/// <summary>
/// قارئ الباركود — الأحداث دفق readings؛ لا Polling داخل ViewModels (ADR-005).
/// </summary>
public interface IBarcodeScanner
{
    string DeviceId { get; }
    DeviceConnectionState Probe();
    IObservable<BarcodeRead> Reads { get; }
}

/// <summary>الميزان — قراءة وزن مستقرة عند الطلب أو دفق مستمر.</summary>
public interface IScaleDevice
{
    string DeviceId { get; }
    DeviceConnectionState Probe();
    Task<DeviceResult<ScaleReading>> ReadStableAsync(TimeSpan timeout, CancellationToken ct = default);
    IObservable<ScaleReading> LiveReadings { get; }
}

/// <summary>طابعة إيصال 80/58mm — التوجيه عبر print.printer_profiles المعتمدة فقط.</summary>
public interface IReceiptPrinter
{
    string DeviceId { get; }
    DeviceConnectionState Probe();
    Task<DeviceResult<PrintOutcome>> PrintRawAsync(ReadOnlyMemory<byte> escPosData, CancellationToken ct = default);
}

/// <summary>طابعة ملصقات.</summary>
public interface ILabelPrinter
{
    string DeviceId { get; }
    DeviceConnectionState Probe();
    Task<DeviceResult<PrintOutcome>> PrintLabelAsync(ReadOnlyMemory<byte> zplOrEscData, CancellationToken ct = default);
}

/// <summary>درج النقد — فتح بقاعدة صلاحية موثقة على الخادم.</summary>
public interface ICashDrawer
{
    string DeviceId { get; }
    DeviceConnectionState Probe();
    Task<DeviceResult<CashDrawerOutcome>> OpenAsync(CancellationToken ct = default);
}

/// <summary>طرف الدفع — معطل حتى اعتماد المزود وSandbox (B-02/D-11).</summary>
public interface IPaymentTerminal
{
    string DeviceId { get; }
    DeviceConnectionState Probe();
    Task<DeviceResult<string>> InitiatePaymentAsync(decimal amountEgp, CancellationToken ct = default);
}
