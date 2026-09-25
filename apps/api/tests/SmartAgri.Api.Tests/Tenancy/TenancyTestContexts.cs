using Microsoft.EntityFrameworkCore;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Tenancy;

/// <summary>
/// Contexts for tests that never open a connection (model metadata, generated SQL, the
/// save interceptor, which throws before any command is sent). The connection string is
/// unreachable on purpose: if a test accidentally does hit the database it fails fast
/// instead of silently needing Docker.
/// </summary>
internal static class TenancyTestContexts
{
    public static AppDbContext Create(Guid? organizationId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Infrastructure.PostgresFixture.UnreachableConnectionString)
            .Options;
        return new AppDbContext(options, new FixedOrganizationContext(organizationId));
    }
}
