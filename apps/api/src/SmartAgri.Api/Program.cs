using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Observability;
using SmartAgri.Api.Tenancy;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddSmartAgriObservability();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddOrganizationTenancy();
builder.AddSmartAgriAuthentication();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "SmartAgri API";
    document.Info.Version = "v1";
    return Task.CompletedTask;
}));

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

    // The admin-spa OAuth client row is deployment state like the schema: applied here,
    // never on web startup (see AdminSpaClientRegistrar).
    await scope.ServiceProvider.GetRequiredService<AdminSpaClientRegistrar>().EnsureAsync();
    return;
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok()).AllowAnonymous().ExcludeFromDescription();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous().ExcludeFromDescription();

app.MapConnectEndpoints();
app.MapAuthEndpoints();
app.MapMeEndpoints();

// Only in Development: the committed apps/api/openapi/v1.json (generated at build time,
// see SmartAgri.Api.csproj) is the source frontend types are generated from, so the API
// never needs to serve its own document in production.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

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
