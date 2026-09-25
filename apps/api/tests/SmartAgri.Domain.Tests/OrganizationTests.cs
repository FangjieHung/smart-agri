using Shouldly;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Tests;

public class OrganizationTests
{
    [Fact]
    public void Code_is_trimmed_and_lower_cased()
    {
        new Organization(Guid.NewGuid(), " 農會 ", " Farm-01 ").Code.ShouldBe("farm-01");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("農會")]
    [InlineData("under_score")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33 characters
    public void Invalid_codes_are_rejected(string code)
    {
        Should.Throw<ArgumentException>(() => new Organization(Guid.NewGuid(), "Name", code));
    }

    [Fact]
    public void Empty_id_and_blank_name_are_rejected()
    {
        Should.Throw<ArgumentException>(() => new Organization(Guid.Empty, "Name", "code"));
        Should.Throw<ArgumentException>(() => new Organization(Guid.NewGuid(), " ", "code"));
    }
}
