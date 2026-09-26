using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Seeding;

/// <summary>
/// Wires <see cref="DevelopmentSeeder"/> into the host, gated on the environment (the
/// class itself has no such check — see its remarks).
/// </summary>
public static class DevelopmentSeedingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DevelopmentSeeder"/> and <see cref="DemoKnowledgeSeeder"/> only in
    /// Development. Outside Development
    /// (including the integration-test host started with
    /// <c>ASPNETCORE_ENVIRONMENT=Production</c>) it is simply not in the container, so
    /// <see cref="SeedDevelopmentDataAsync"/> is a no-op and there is no path that seeds a
    /// production database. Requires <c>AddDbContext&lt;AppDbContext&gt;</c> to already be
    /// registered (it supplies <c>DbContextOptions&lt;AppDbContext&gt;</c>); does not
    /// itself touch the database. When <see cref="DevelopmentSeeder.PasswordConfigurationKey"/>
    /// is set, <see cref="DevelopmentSeeder.SeedAsync"/> also resolves Identity's
    /// <c>UserManager&lt;Account&gt;</c> from the current scope to validate it — already
    /// registered everywhere this runs, since <c>AddSmartAgriAuthentication</c> wires up
    /// Identity for the whole host, not just Development.
    /// </summary>
    public static IServiceCollection AddDevelopmentSeeding(this IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            services.AddScoped<DevelopmentSeeder>();
            services.AddScoped<DemoKnowledgeSeeder>();
        }

        return services;
    }

    /// <summary>
    /// Runs <see cref="DevelopmentSeeder"/>, then <see cref="DemoKnowledgeSeeder"/> (which needs
    /// its organization and account), if (and only if) they are registered for
    /// <paramref name="services"/> — i.e. in Development. A no-op everywhere else,
    /// including Production, so callers (the <c>migrate</c> subcommand) can call this
    /// unconditionally.
    /// </summary>
    public static async Task SeedDevelopmentDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (services.GetService<DevelopmentSeeder>() is { } seeder)
        {
            await seeder.SeedAsync(cancellationToken);
        }

        if (services.GetService<DemoKnowledgeSeeder>() is { } knowledge)
        {
            await knowledge.SeedAsync(cancellationToken);
        }
    }
}
