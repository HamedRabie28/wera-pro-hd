namespace WeraPro.Application.Validation;

using FluentValidation;
using WeraPro.Contracts.V1;

/// <summary>مدققو الإدخال — مسار التحقق الموحد (ADR-007: FluentValidation حصراً).</summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(64)
            .Matches("^[A-Za-z0-9_.-]+$").WithMessage("اسم المستخدم يحتمل أحرفاً إنجليزية وأرقاماً و._- فقط");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.BranchId).GreaterThan(0).When(x => x.BranchId.HasValue);
        RuleFor(x => x.StationId).GreaterThan(0).When(x => x.StationId.HasValue);
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
    }
}
