using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// Inspects the SQL EF Core generates (no database needed) to show the organization
/// filter is part of every query and is parameterized per context instance. The results
/// themselves are asserted against PostgreSQL in <see cref="OrganizationIsolationTests"/>.
/// </summary>
public class OrganizationFilterSqlTests
{
    [Fact]
    public void Queries_are_filtered_by_the_current_organization()
    {
        var organizationId = Guid.NewGuid();
        using var dbContext = TenancyTestContexts.Create(organizationId);

        var sql = dbContext.Accounts.ToQueryString();

        sql.ShouldContain("\"OrganizationId\" = @");
        sql.ShouldContain(organizationId.ToString());
    }

    [Fact]
    public void Queries_without_an_organization_cannot_match_any_row()
    {
        using var dbContext = TenancyTestContexts.Create(organizationId: null);

        var sql = dbContext.AccountPermissions.ToQueryString();

        sql.ShouldContain("\"OrganizationId\" = @");
        sql.ShouldContain(Guid.Empty.ToString());
        sql.ShouldContain("='False'"); // the "has an organization" guard parameter
    }

    [Fact]
    public void Each_context_uses_its_own_organization()
    {
        // Model (and filter) are built once and cached; the organization must still come
        // from the context running the query, not the one that built the model.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var firstContext = TenancyTestContexts.Create(first);
        using var secondContext = TenancyTestContexts.Create(second);

        firstContext.Accounts.ToQueryString().ShouldContain(first.ToString());
        var secondSql = secondContext.Accounts.ToQueryString();
        secondSql.ShouldContain(second.ToString());
        secondSql.ShouldNotContain(first.ToString());
    }
}
