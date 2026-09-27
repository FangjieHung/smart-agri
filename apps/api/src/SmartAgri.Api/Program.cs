using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Accounts;
using SmartAgri.Api.Ai;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Jobs;
using SmartAgri.Api.Knowledge;
using SmartAgri.Api.Knowledge.Evaluation;
using SmartAgri.Api.Observability;
using SmartAgri.Api.Setup;
using SmartAgri.Api.Seeding;
using SmartAgri.Api.Team;
using SmartAgri.Api.Tenancy;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.AddSmartAgriObservability();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"), npgsql => npgsql.UseVector()));
builder.Services.AddOrganizationTenancy();
builder.Services.AddBackgroundJobs(builder.Configuration);
builder.Services.AddKnowledge(builder.Configuration);
builder.Services.AddEmbeddings(builder.Configuration);
builder.AddSmartAgriAuthentication();
builder.Services.AddInitialSetup();
builder.Services.AddDevelopmentSeeding(builder.Environment);
// Numbers are JSON numbers only. ASP.NET Core's web defaults also accept "12" for an int,
// which makes the OpenAPI document describe every integer as `integer | string` — and the
// generated frontend types `number | string`.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
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

    // No-op outside Development, where DevelopmentSeeder is never registered (Slice 6).
    await scope.ServiceProvider.SeedDevelopmentDataAsync();
    return;
}

// `setup` is a one-shot subcommand too: create the first organization and administrator
// on an empty, migrated database, print the one-time password once, and exit
// (M1 plan, Slice 11; see SetupCommand).
if (args is [SmartAgriCommands.Setup, .. var setupArgs])
{
    Environment.ExitCode = await SetupCommand.RunAsync(app.Services, setupArgs, SystemSetupConsole.Instance);
    return;
}

// `reindex` is one-shot too: re-embed chunks whose vectors are from another model than
// Ai:Embedding:Model, per organization, printing progress, and exit (M2 plan, Slice 7).
if (args is [SmartAgriCommands.Reindex, .. var reindexArgs])
{
    Environment.ExitCode = await ReindexCommand.RunAsync(app.Services, reindexArgs, Console.Out, Console.Error);
    return;
}

// `eval-retrieval` is one-shot too, Development only: import the retrieval evaluation set into an
// organization of its own, run every question through KnowledgeRetriever with the configured
// embedding model and write a Markdown report, then exit (M2 plan, Slice 16).
if (args is [SmartAgriCommands.EvalRetrieval, .. var evalArgs])
{
    Environment.ExitCode = await EvalRetrievalCommand.RunAsync(app.Services, evalArgs, Console.Out, Console.Error);
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
app.MapTeamEndpoints();
app.MapKnowledgeBaseEndpoints();
app.MapKnowledgeDocumentEndpoints();
app.MapKnowledgeFaqEndpoints();
app.MapKnowledgeExtractionEndpoints();
app.MapKnowledgeReviewEndpoints();
app.MapKnowledgeRetrievalEndpoints();

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
    public const string Setup = "setup";
    public const string Reindex = "reindex";
    public const string EvalRetrieval = "eval-retrieval";
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
