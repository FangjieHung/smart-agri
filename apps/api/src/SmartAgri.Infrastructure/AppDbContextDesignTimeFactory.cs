using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Infrastructure;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without the full Api host. <c>migrations add</c>
/// and <c>migrations script</c> never open this connection (EF Core only needs it to pick
/// the Npgsql provider); <c>database update</c> does, so it is the local development
/// database from <c>deploy/docker-compose.dev.yml</c> — the same string as
/// <c>appsettings.Development.json</c> (<c>AppDbContextDesignTimeFactoryTests</c> keeps the
/// two equal). Pass <c>--connection</c> to target any other database. Design-time
/// tooling never acts for an organization, so it gets "no organization".
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string DevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=smartagri;Username=smartagri;Password=smartagri_dev";

    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(DevelopmentConnectionString);

        return new AppDbContext(optionsBuilder.Options, FixedOrganizationContext.None);
    }
}
