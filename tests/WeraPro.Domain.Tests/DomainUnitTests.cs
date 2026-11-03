namespace WeraPro.Domain.Tests;

using System.Globalization;
using WeraPro.Domain.Security;
using WeraPro.Domain.SharedKernel;
using WeraPro.Domain.Weighing;
using Xunit;

/// <summary>قواعد المال والتقريب — Banker's Rounding الملزمة بحسم D-07 (أ).</summary>
public class MoneyRoundingTests
{
    [Theory]
    [InlineData("10.005", "10.00")]   // نصف زوجي: 00 زوجي → 10.00
    [InlineData("10.015", "10.02")]   // 01 فردي → يرتفع إلى 02 الزوجي
    [InlineData("0.125", "0.12")]
    [InlineData("0.135", "0.14")]
    [InlineData("219.595", "219.60")]
    public void ToCent_AppliesBankersRounding(string input, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), MoneyRounding.ToCent(decimal.Parse(input, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("7.5", "5", "10")]     // 7.5/5=1.5 → ToEven(1.5)=2 → 10
    [InlineData("12.3", "5", "10")]
    [InlineData("12.8", "5", "15")]
    [InlineData("0.0", "5", "0")]
    [InlineData("355.65", "5", "355")]
    public void ToStep_RoundsToNearestStepEven(string value, string step, string expected)
    {
        var culture = CultureInfo.InvariantCulture;
        Assert.Equal(decimal.Parse(expected, culture), MoneyRounding.ToStep(decimal.Parse(value, culture), decimal.Parse(step, culture)));
    }

    [Fact]
    public void ToStep_RejectsNonPositiveStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneyRounding.ToStep(10m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => MoneyRounding.ToStep(10m, -5m));
    }
}

/// <summary>
/// المثال المرجعي الملزم بحسم D-07:
/// 200 EGP/kg × مبلغ مستهدف 70 EGP → وزن 350g (على الخطوة بلا تعديل).
/// </summary>
public class WeightCalculatorTests
{
    private static readonly WeighingSettings Settings = WeighingSettings.Default();

    [Fact]
    public void WeightFromAmount_ReferenceExample_200PerKg_70Egp_Gives_350g()
    {
        var result = WeightCalculator.WeightFromAmount(pricePerKg: 200m, targetAmount: 70m, settings: Settings);

        Assert.Equal(350m, result.DisplayWeightGrams);      // على الخطوة (5g) بلا تعديل
        Assert.Equal(350m, result.ExactWeightGrams);
        Assert.Equal(70.00m, result.EffectiveAmount);
    }

    [Fact]
    public void WeightFromAmount_OffStep_RoundsToNearestStep()
    {
        // 200 EGP/kg × 71.13 EGP → خام 355.65g → أقرب خطوة 355g
        var result = WeightCalculator.WeightFromAmount(200m, 71.13m, Settings);
        Assert.Equal(355m, result.DisplayWeightGrams);
        Assert.Equal(71.00m, result.EffectiveAmount);
    }

    [Fact]
    public void AmountFromWeight_BankersRounding()
    {
        // 149.99 EGP/kg × 250g = 37.4975 → قرش Banker's → 37.50
        Assert.Equal(37.50m, WeightCalculator.AmountFromWeight(149.99m, 250m, Settings));
    }

    [Fact]
    public void WeightFromAmount_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightCalculator.WeightFromAmount(0m, 70m, Settings));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightCalculator.WeightFromAmount(200m, -1m, Settings));
    }
}

/// <summary>نطاق أدوار المستخدم — الإسناد بلا فرع يعني كل الفروع (V001).</summary>
public class UserRoleScopeTests
{
    private static User UserWithRole(string role, long? branchId) => new()
    {
        Username = "tester",
        DisplayNameAr = "مختبر",
        PasswordHash = "hash",
        IsActive = true,
        Roles = [new UserRole { RoleCode = role, BranchId = branchId }],
    };

    [Fact]
    public void GlobalRole_AppliesToAnyBranch()
    {
        var user = UserWithRole(RoleCodes.Manager, branchId: null);
        Assert.True(user.HasRoleInBranch(RoleCodes.Manager, 1));
        Assert.True(user.HasRoleInBranch(RoleCodes.Manager, 99));
        Assert.True(user.HasRoleInBranch(RoleCodes.Manager, null));
    }

    [Fact]
    public void BranchScopedRole_DoesNotLeakToOtherBranches()
    {
        var user = UserWithRole(RoleCodes.Supervisor, branchId: 7);
        Assert.True(user.HasRoleInBranch(RoleCodes.Supervisor, 7));
        Assert.False(user.HasRoleInBranch(RoleCodes.Supervisor, 8));
    }

    [Fact]
    public void InactiveUser_HasNoRoles()
    {
        var user = new User
        {
            Username = "x",
            DisplayNameAr = "معطل",
            PasswordHash = "h",
            IsActive = false,
            Roles = [new UserRole { RoleCode = RoleCodes.Owner }],
        };
        Assert.False(user.HasRoleInBranch(RoleCodes.Owner, null));
    }

    [Fact]
    public void RoleCodes_All_SevenSeededRoles()
    {
        Assert.Equal(7, RoleCodes.All.Count);
        Assert.Contains(RoleCodes.Cashier, RoleCodes.All);
        Assert.Contains(RoleCodes.Auditor, RoleCodes.All);
    }
}

/// <summary>نتيجة المجال — نجاح أو فشل موثق بلا استثناءات للتحكم بالتدفق.</summary>
public class ResultTests
{
    [Fact]
    public void Ok_CarriesValue()
    {
        var result = Result.Ok<int>(42);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Fail_CarriesError()
    {
        var result = Result.Fail<int>(new DomainError("E1", "خطأ"));
        Assert.False(result.IsSuccess);
        Assert.Equal("E1", result.Error!.Code);
    }

    [Fact]
    public void Match_DirectsToBranch()
    {
        var ok = Result.Ok<int>(1);
        var fail = Result.Fail<int>(new DomainError("E", "x"));
        Assert.Equal("success", ok.Match(_ => "success", _ => "failure"));
        Assert.Equal("failure", fail.Match(_ => "success", _ => "failure"));
    }
}
