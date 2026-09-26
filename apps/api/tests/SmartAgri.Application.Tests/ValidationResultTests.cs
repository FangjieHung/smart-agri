using Shouldly;
using SmartAgri.Application.Validation;

namespace SmartAgri.Application.Tests;

public class ValidationResultTests
{
    [Fact]
    public void A_valid_result_has_a_value_and_no_failures()
    {
        var result = ValidationResult<string>.Valid("ok");

        result.IsValid.ShouldBeTrue();
        result.Value.ShouldBe("ok");
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void An_invalid_result_has_failures_and_refuses_to_give_a_value()
    {
        var result = ValidationResult<string>.Invalid("name", "請輸入名稱。");

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldBe([new ValidationFailure("name", "請輸入名稱。")]);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void An_invalid_result_needs_at_least_one_failure()
    {
        Should.Throw<ArgumentException>(() => ValidationResult<string>.Invalid([]));
    }
}
