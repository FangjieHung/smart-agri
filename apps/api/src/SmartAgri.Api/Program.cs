using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

// `migrate` is a one-shot subcommand: apply pending migrations and exit without
// starting the web server. The Api never migrates automatically on normal startup
// (see M1 skeleton plan, Slice 2) — deployment automation (the customer compose file's
// entrypoint script) calls this explicitly before running the app.
if (args is [SmartAgriCommands.Migrate, ..])
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
    return;
}

app.MapGet("/health/live", () => Results.Ok());

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.Run();

internal static class SmartAgriCommands
{
    public const string Migrate = "migrate";
}

namespace SmartAgri.Api
{
    /// <summary>
    /// Exposes the generated <c>Program</c> class so integration tests can
    /// bootstrap the API with <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
    /// </summary>
    public partial class Program
    {
    }
}
