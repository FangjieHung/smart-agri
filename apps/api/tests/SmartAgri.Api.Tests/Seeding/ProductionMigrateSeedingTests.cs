using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Seeding;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Seeding;

namespace SmartAgri.Api.Tests.Seeding;

/// <summary>
/// Acceptance criterion from the M1 skeleton plan, Slice 6: starting the integration-test
/// host with <c>ASPNETCORE_ENVIRONMENT=Production</c> and running the same steps the
/// <c>migrate</c> subcommand runs leaves the database with zero accounts —
/// <see cref="DevelopmentSeeder"/> is never registered outside Development
/// (<see cref="DevelopmentSeedingTests.DevelopmentSeeder_is_registered_only_in_development"/>
/// covers that directly without Docker; this test additionally proves the effect
/// end-to-end against a real database).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public class ProductionMigrateSeedingTests : IAsyncLifetime, IDisposable
{
    private const string CertificatePassword = "test-password";

    private readonly PostgresFixture _postgres = new();
    private readonly string _certificateDirectory = Directory.CreateTempSubdirectory("smartagri-prod-seed-").FullName;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _postgres.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public void Dispose()
    {
        Directory.Delete(_certificateDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Production_host_running_migrate_seeds_nothing()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", _postgres.ConnectionString);
            builder.UseSetting("Authentication:SigningCertificatePath", WritePfx("signing", X509KeyUsageFlags.DigitalSignature));
            builder.UseSetting("Authentication:SigningCertificatePassword", CertificatePassword);
            builder.UseSetting("Authentication:EncryptionCertificatePath", WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment));
            builder.UseSetting("Authentication:EncryptionCertificatePassword", CertificatePassword);
            // Deliberately NOT setting SEED_DEMO_PASSWORD: a Production deployment has no
            // reason to, and DevelopmentSeeder must never be reachable there regardless.
        });

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // The exact steps Program.cs's `migrate` subcommand runs.
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(CancellationToken);
            await scope.ServiceProvider.GetRequiredService<AdminSpaClientRegistrar>().EnsureAsync(CancellationToken);

            scope.ServiceProvider.GetService<DevelopmentSeeder>().ShouldBeNull();
            await scope.ServiceProvider.SeedDevelopmentDataAsync(CancellationToken);
        }

        await using var dbContext = _postgres.CreateDbContext();
        (await dbContext.Accounts.IgnoreQueryFilters([AppDbContext.OrganizationFilter]).CountAsync(CancellationToken)).ShouldBe(0);
        (await dbContext.Organizations.CountAsync(CancellationToken)).ShouldBe(0);
    }

    private string WritePfx(string name, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=smartagri-test-{name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(_certificateDirectory, $"{name}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        return path;
    }
}
