using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// The one-shot <c>set-token-limit</c> subcommand (M5a plan §3 F, issue #195), run from the services
/// of a <b>Production</b> host (it must work in the container operators run it in) against a real
/// database: it writes the limit only for an organization that exists and a valid number, and says
/// so with its exit code.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class SetTokenLimitCommandTests : IAsyncLifetime, IDisposable
{
    private const string CertificatePassword = "test-password";

    private readonly PostgresFixture _postgres = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-token-limit-").FullName;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.Database.MigrateAsync(CancellationToken);
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", _postgres.ConnectionString);
            builder.UseSetting("Authentication:SigningCertificatePath", WritePfx("signing", X509KeyUsageFlags.DigitalSignature));
            builder.UseSetting("Authentication:SigningCertificatePassword", CertificatePassword);
            builder.UseSetting("Authentication:EncryptionCertificatePath", WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment));
            builder.UseSetting("Authentication:EncryptionCertificatePassword", CertificatePassword);
            DataProtectionTestSettings.Use(builder, _directory);
        });
    }

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
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task It_sets_a_limit_in_production_and_default_clears_it_again()
    {
        var organization = await CreateOrganizationAsync();

        var set = await RunAsync("--organization", organization.Code.ToUpperInvariant(), "--tokens", "5000000");
        set.Exit.ShouldBe(SetTokenLimitCommand.ExitSuccess, set.Error);
        set.Output.ShouldContain(organization.Code);
        set.Output.ShouldContain("5,000,000");
        (await LimitOfAsync(organization)).ShouldBe(5_000_000);

        var zero = await RunAsync("--organization=" + organization.Code, "--tokens=0");
        zero.Exit.ShouldBe(SetTokenLimitCommand.ExitSuccess, zero.Error);
        (await LimitOfAsync(organization)).ShouldBe(0);

        var reset = await RunAsync("--organization", organization.Code, "--tokens", "default");
        reset.Exit.ShouldBe(SetTokenLimitCommand.ExitSuccess, reset.Error);
        (await LimitOfAsync(organization)).ShouldBeNull();
    }

    [Theory]
    [InlineData("no-such-org")]
    [InlineData("Bad Code/../")]
    public async Task An_unknown_organization_code_is_a_non_zero_exit_and_nothing_is_written(string code)
    {
        var organization = await CreateOrganizationAsync();

        var result = await RunAsync("--organization", code, "--tokens", "123");

        result.Exit.ShouldNotBe(SetTokenLimitCommand.ExitSuccess);
        result.Exit.ShouldBe(SetTokenLimitCommand.ExitFailed);
        result.Error.ShouldContain("找不到組織代碼");
        await using var dbContext = _postgres.CreateDbContext();
        (await dbContext.Organizations.Select(candidate => candidate.MonthlyTokenLimit).ToListAsync(CancellationToken))
            .ShouldAllBe(limit => limit == null);
        (await LimitOfAsync(organization)).ShouldBeNull();
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public async Task An_invalid_number_is_exit_2_and_nothing_is_written(string tokens)
    {
        var organization = await CreateOrganizationAsync();
        await SetLimitDirectlyAsync(organization, 777);

        var result = await RunAsync("--organization", organization.Code, "--tokens=" + tokens);

        result.Exit.ShouldBe(SetTokenLimitCommand.ExitUsage);
        result.Error.ShouldContain("--tokens");
        result.Error.ShouldContain("用法：set-token-limit");
        (await LimitOfAsync(organization)).ShouldBe(777);
    }

    [Fact]
    public async Task Missing_or_unknown_arguments_are_exit_2_and_help_is_exit_0()
    {
        foreach (var args in new[] { Array.Empty<string>(), ["--organization", "x"], ["--tokens", "5"], ["--organization"], ["--tokens"], ["--nope"], ["5"] })
        {
            (await RunAsync(args)).Exit.ShouldBe(SetTokenLimitCommand.ExitUsage, string.Join(' ', args));
        }

        var help = await RunAsync("--help");
        help.Exit.ShouldBe(SetTokenLimitCommand.ExitSuccess);
        help.Output.ShouldStartWith("用法：set-token-limit");
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SetTokenLimitCommand.RunAsync(_factory!.Services, args, output, error, CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<Organization> CreateOrganizationAsync()
    {
        var organization = new Organization(Guid.CreateVersion7(), "安心商行", "t" + Guid.NewGuid().ToString("N")[..12]);
        await using var dbContext = _postgres.CreateDbContext();
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(CancellationToken);
        return organization;
    }

    private async Task SetLimitDirectlyAsync(Organization organization, long limit)
    {
        await using var dbContext = _postgres.CreateDbContext();
        (await dbContext.Organizations.SingleAsync(candidate => candidate.Id == organization.Id, CancellationToken)).SetMonthlyTokenLimit(limit);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task<long?> LimitOfAsync(Organization organization)
    {
        await using var dbContext = _postgres.CreateDbContext();
        return await dbContext.Organizations.AsNoTracking()
            .Where(candidate => candidate.Id == organization.Id)
            .Select(candidate => candidate.MonthlyTokenLimit)
            .SingleAsync(CancellationToken);
    }

    private string WritePfx(string name, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=smartagri-test-{name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_directory, $"{name}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        return path;
    }
}
