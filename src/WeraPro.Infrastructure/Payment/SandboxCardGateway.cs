using System.Collections.Concurrent;
using WeraPro.Application.Sales;

namespace WeraPro.Infrastructure.Payment;

/// <summary>
/// بوابة دفع بالبطاقة محاكية موسومة بوضوح (§9.6: «المحاكاة Development/Test/Sandbox
/// فقط ومعلّمة بوضوح») — تُفعَّل وحدها بضبط الإعداد payments.card_provider =
/// SANDBOX_SIMULATOR، ولا تدّعي اتصالاً بأي مزود حقيقي (مانع B-02/D-11 قائم حتى
/// اعتماد المزود والبنك وتفعيل بيئة Sandbox موثقة). مرجع كل عملية يبدأ بـSBX
/// ويُخزن في sales.payments.provider_name بهذا الاسم الموسوم.
/// </summary>
public sealed class SandboxCardGateway : ICardPaymentGateway
{
    public const string SettingValue = "SANDBOX_SIMULATOR";

    /// <summary>الاسم المخزن في provider_name — يوسم المحاكاة في كل سجل دفع.</summary>
    public string ProviderName => "WeraSandbox-SIMULATED";

    /// <summary>مراجع الشحن التي لم تُعكس — لإثبات التعويض عند فشل الترحيل.</summary>
    private static readonly ConcurrentDictionary<string, decimal> OpenCharges = new();

    public Task<CardChargeResult> ChargeAsync(decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        if (amount <= 0)
        {
            return Task.FromResult(new CardChargeResult(false, string.Empty, "FAILED", "مبلغ غير صالح"));
        }
        // محاكاة حتمية: تقبل كل عملية مبلغها موجب وترفض الصفر/السالب — لا عشوائية تكسر الاختبارات
        var reference = $"SBX-{idempotencyKey[..Math.Min(24, idempotencyKey.Length)]}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        OpenCharges[reference] = amount;
        return Task.FromResult(new CardChargeResult(true, reference, "CAPTURED", null));
    }

    public Task VoidAsync(string reference, CancellationToken ct = default)
    {
        OpenCharges.TryRemove(reference, out _);
        return Task.CompletedTask;
    }
}
