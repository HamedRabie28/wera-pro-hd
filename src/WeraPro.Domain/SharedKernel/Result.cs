namespace WeraPro.Domain.SharedKernel;

/// <summary>نتيجة عملية مجال — نجاح أو فشل بسبب موثق، بلا استثناءات للتحكم بالتدفق.</summary>
public readonly record struct Result<T>
{
    public T? Value { get; }
    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;

    internal Result(T? value, DomainError? error)
    {
        Value = value;
        Error = error;
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<DomainError, TOut> onFailure)
        => IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}

/// <summary>مصنع النتائج (خارج النوع العام التزاماً بـ CA1000).</summary>
public static class Result
{
    public static Result<T> Ok<T>(T value) => new(value, null);
    public static Result<T> Fail<T>(DomainError error) => new(default, error);
}

/// <summary>خطأ مجال موثق برمز ثابت ورسالة عربية للعرض.</summary>
public sealed record DomainError(string Code, string MessageAr)
{
    public static readonly DomainError None = new(string.Empty, string.Empty);
}
