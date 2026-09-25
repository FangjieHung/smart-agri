using Shouldly;
using SmartAgri.Api.Setup;

namespace SmartAgri.Api.Tests.Setup;

/// <summary><c>setup</c> command-line parsing. No database needed.</summary>
public class SetupArgumentsTests
{
    [Fact]
    public void Accepts_space_and_equals_forms()
    {
        var result = SetupArguments.Parse(
            ["--organization-name", "安心農場", "--organization-code=anxin", "--admin-login", "admin", "--admin-display-name=管理者"]);

        result.Error.ShouldBeNull();
        result.Arguments.ShouldBe(new SetupArguments("安心農場", "anxin", "admin", "管理者"));
    }

    [Fact]
    public void No_arguments_leaves_everything_to_prompts()
    {
        SetupArguments.Parse([]).Arguments.ShouldBe(new SetupArguments(null, null, null, null));
    }

    [Theory]
    [InlineData("--organisation-name", "x")]
    [InlineData("--password", "x")]
    [InlineData("positional")]
    [InlineData("-o", "x")]
    public void Rejects_unknown_flags_and_positional_arguments(params string[] args)
    {
        var result = SetupArguments.Parse(args);

        result.Arguments.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain(args[0]);
    }

    [Theory]
    [InlineData("--admin-login")]
    [InlineData("--admin-login", "--organization-code", "x")]
    public void Rejects_a_flag_without_a_value(params string[] args)
    {
        SetupArguments.Parse(args).Error.ShouldNotBeNull().ShouldContain("--admin-login");
    }

    [Fact]
    public void Rejects_a_repeated_flag()
    {
        SetupArguments.Parse(["--admin-login", "a", "--admin-login=b"]).Error.ShouldNotBeNull().ShouldContain("--admin-login");
    }

    [Fact]
    public void An_empty_equals_value_is_kept_for_validation_to_reject()
    {
        SetupArguments.Parse(["--organization-code="]).Arguments!.OrganizationCode.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_is_recognized(string flag)
    {
        SetupArguments.Parse(["--admin-login", "a", flag]).Arguments!.HelpRequested.ShouldBeTrue();
    }
}
