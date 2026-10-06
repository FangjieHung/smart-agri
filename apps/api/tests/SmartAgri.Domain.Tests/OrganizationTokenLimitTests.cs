using Shouldly;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Tests;

/// <summary><see cref="Organization.MonthlyTokenLimit"/> (M5a plan §3 F, issue #195).</summary>
public class OrganizationTokenLimitTests
{
    [Fact]
    public void A_new_organization_uses_the_deployment_default()
    {
        new Organization(Guid.NewGuid(), "安心商行", "anxin").MonthlyTokenLimit.ShouldBeNull();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(5_000_000L)]
    public void A_limit_of_zero_or_more_is_kept_and_null_goes_back_to_the_default(long limit)
    {
        var organization = new Organization(Guid.NewGuid(), "安心商行", "anxin");

        organization.SetMonthlyTokenLimit(limit);
        organization.MonthlyTokenLimit.ShouldBe(limit);

        organization.SetMonthlyTokenLimit(null);
        organization.MonthlyTokenLimit.ShouldBeNull();
    }

    [Fact]
    public void A_negative_limit_is_refused_and_changes_nothing()
    {
        var organization = new Organization(Guid.NewGuid(), "安心商行", "anxin");
        organization.SetMonthlyTokenLimit(10);

        Should.Throw<ArgumentOutOfRangeException>(() => organization.SetMonthlyTokenLimit(-1));

        organization.MonthlyTokenLimit.ShouldBe(10);
    }
}
