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

// ── الكتالوج (G4) ───────────────────────────────────────────────────────────

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    private static readonly string[] ValidSaleModes = ["COUNT", "WEIGHT", "PACK", "SERIALIZED"];
    private static readonly string[] ValidBarcodeTypes = ["EAN13", "EAN8", "UPC_A", "UPC_E", "CODE128", "CODE39", "GS1_128", "INTERNAL"];
    private static readonly string[] ValidTaxTreatments = ["TAXABLE", "ZERO_RATED", "EXEMPT"];

    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32)
            .Matches("^[A-Za-z0-9_.-]+$").WithMessage("كود المنتج يحتمل أحرفاً إنجليزية وأرقاماً و._- فقط");
        RuleFor(x => x.NameAr).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NameEn).MaximumLength(200).When(x => x.NameEn is not null);
        RuleFor(x => x.CategoryId).GreaterThan(0);
        RuleFor(x => x.BrandId).GreaterThan(0).When(x => x.BrandId.HasValue);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Skus).NotEmpty().WithMessage("المنتج يحتاج صيغة بيع واحدة على الأقل");
        RuleForEach(x => x.Skus).ChildRules(sku =>
        {
            sku.RuleFor(s => s.SkuCode).NotEmpty().MaximumLength(48)
                .Matches("^[A-Za-z0-9_.-]+$").WithMessage("كود الصيغة يحتمل أحرفاً إنجليزية وأرقاماً و._- فقط");
            sku.RuleFor(s => s.SaleMode).Must(m => ValidSaleModes.Contains(m))
                .WithMessage("صيغة البيع يجب أن تكون COUNT/WEIGHT/PACK/SERIALIZED — لا تُستنتج");
            sku.RuleFor(s => s.BaseUomId).GreaterThan(0);
            sku.RuleFor(s => s.SaleUomId).GreaterThan(0);
            sku.RuleFor(s => s.NameAr).MaximumLength(120).When(s => s.NameAr is not null);
            sku.RuleFor(s => s.ReorderLevel).GreaterThanOrEqualTo(0);
        });
        RuleForEach(x => x.Barcodes).ChildRules(b =>
        {
            b.RuleFor(x => x.BarcodeValue).NotEmpty().MaximumLength(64);
            b.RuleFor(x => x.BarcodeType).Must(t => ValidBarcodeTypes.Contains(t))
                .WithMessage("نوع الباركود غير معتمد");
            b.RuleFor(x => x.SkuOrdinal).GreaterThanOrEqualTo(0);
        });
        RuleForEach(x => x.Plus).ChildRules(p =>
        {
            p.RuleFor(x => x.PluCode).NotEmpty().MaximumLength(5);
            p.RuleFor(x => x.SkuOrdinal).GreaterThanOrEqualTo(0);
        });
        RuleForEach(x => x.Prices).ChildRules(p =>
        {
            p.RuleFor(x => x.SkuOrdinal).GreaterThanOrEqualTo(0);
            p.RuleFor(x => x.PriceListId).GreaterThan(0);
            p.RuleFor(x => x.UnitId).GreaterThan(0);
            p.RuleFor(x => x.UnitPrice).GreaterThan(0)
                .WithMessage("السعر يجب أن يكون موجباً — لا سعر صفر غير مقصود");
            p.RuleFor(x => x.TaxTreatment).Must(t => ValidTaxTreatments.Contains(t));
        });
        RuleForEach(x => x.Packages).ChildRules(p =>
        {
            p.RuleFor(x => x.PackageSkuOrdinal).GreaterThanOrEqualTo(0);
            p.RuleFor(x => x.ContentSkuOrdinal).GreaterThanOrEqualTo(0);
            p.RuleFor(x => x.UnitsPerPackage).GreaterThan(0);
        });
    }
}

public sealed class PostStockMovementRequestValidator : AbstractValidator<PostStockMovementRequest>
{
    public PostStockMovementRequestValidator()
    {
        RuleFor(x => x.MovementType).NotEmpty().MaximumLength(20);
        RuleFor(x => x.WarehouseId).GreaterThan(0);
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.SourceUomId).GreaterThan(0);
        RuleFor(x => x.SourceDocType).NotEmpty().MaximumLength(40);
        RuleFor(x => x.SourceDocId).GreaterThan(0);
        RuleFor(x => x.SourceLineRef).MaximumLength(80).When(x => x.SourceLineRef is not null);
        RuleFor(x => x.Reason).MaximumLength(400).When(x => x.Reason is not null);
    }
}

public sealed class SetProductStatusRequestValidator : AbstractValidator<SetProductStatusRequest>
{
    public SetProductStatusRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().Must(s => s is "ACTIVE" or "INACTIVE" or "DISCONTINUED")
            .WithMessage("الحالة يجب أن تكون ACTIVE/INACTIVE/DISCONTINUED");
        RuleFor(x => x.ReasonAr).MaximumLength(400).When(x => x.ReasonAr is not null);
    }
}
