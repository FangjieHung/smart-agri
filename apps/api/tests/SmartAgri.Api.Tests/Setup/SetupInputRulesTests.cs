using Microsoft.AspNetCore.Identity;
using Shouldly;
using SmartAgri.Api.Setup;

namespace SmartAgri.Api.Tests.Setup;

/// <summary>Validation of the values <c>setup</c> collects. No database needed.</summary>
public class SetupInputRulesTests
{
    // What AddSmartAgriAuthentication configures: Identity's default plus '/'.
    private static readonly SetupInputRules Rules = new(new UserOptions().AllowedUserNameCharacters + "/");

    [Theory]
    [InlineData("anxin", "anxin")]
    [InlineData("  AnXin-2 ", "anxin-2")]
    public void Organization_codes_are_normalized_like_the_entity_does(string input, string expected)
    {
        SetupInputRules.OrganizationCode(input).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("an/xin")]
    [InlineData("安心")]
    [InlineData("an xin")]
    [InlineData("an_xin")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void Invalid_organization_codes_are_rejected(string input)
    {
        SetupInputRules.OrganizationCode(input).Error.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("a.b_c-d@e+f")]
    [InlineData("Admin01")]
    public void Ascii_login_names_that_Identity_accepts_are_valid(string input)
    {
        Rules.AdminLogin(input).Value.ShouldBe(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("管理者")]
    [InlineData("ad min")]
    [InlineData("admín")]
    [InlineData("a:b")]
    public void Login_names_with_other_characters_are_rejected(string input)
    {
        Rules.AdminLogin(input).Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Login_names_are_limited_to_64_characters()
    {
        Rules.AdminLogin(new string('a', 64)).IsValid.ShouldBeTrue();
        Rules.AdminLogin(new string('a', 65)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Names_are_trimmed_and_length_checked()
    {
        SetupInputRules.OrganizationName("  安心農場 ").Value.ShouldBe("安心農場");
        SetupInputRules.OrganizationName(" ").IsValid.ShouldBeFalse();
        SetupInputRules.OrganizationName(new string('農', 201)).IsValid.ShouldBeFalse();
        SetupInputRules.AdminDisplayName(" 王小明 ").Value.ShouldBe("王小明");
        SetupInputRules.AdminDisplayName("").IsValid.ShouldBeFalse();
        SetupInputRules.AdminDisplayName(new string('王', 201)).IsValid.ShouldBeFalse();
    }
}
