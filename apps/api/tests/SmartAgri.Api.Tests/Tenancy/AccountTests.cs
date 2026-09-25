using Shouldly;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure.Accounts;

namespace SmartAgri.Api.Tests.Tenancy;

public class AccountTests
{
    private static readonly Organization Farm = new(Guid.NewGuid(), "Farm", "Farm-01");

    [Fact]
    public void Identity_user_name_is_organization_code_slash_login_name()
    {
        var account = Account.Create(Farm, " Admin ", "系統管理員", AccountRole.SmbAdmin);

        account.LoginName.ShouldBe("Admin");
        account.NormalizedLoginName.ShouldBe("ADMIN");
        account.UserName.ShouldBe("farm-01/Admin");
        account.NormalizedUserName.ShouldBe("FARM-01/ADMIN");
        account.OrganizationId.ShouldBe(Farm.Id);
        account.Id.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_login_names_are_rejected(string loginName)
    {
        Should.Throw<ArgumentException>(() => Account.Create(Farm, loginName, "Name", AccountRole.SmbAdmin));
    }

    [Fact]
    public void Same_login_name_in_different_organizations_gives_different_user_names()
    {
        var other = new Organization(Guid.NewGuid(), "Other", "other");

        Account.Create(Farm, "admin", "A", AccountRole.SmbAdmin).NormalizedUserName
            .ShouldNotBe(Account.Create(other, "admin", "B", AccountRole.SmbAdmin).NormalizedUserName);
    }
}
