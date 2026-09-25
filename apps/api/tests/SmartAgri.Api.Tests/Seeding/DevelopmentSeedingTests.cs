using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Seeding;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Accounts;
using SmartAgri.Infrastructure.Seeding;
using SmartAgri.Infrastructure.Tenancy;

namespace SmartAgri.Api.Tests.Seeding;

/// <summary>
/// <see cref="DevelopmentSeedingServiceCollectionExtensions"/> and the parts of
/// <see cref="DevelopmentSeeder"/> that don't need a database: whether it is registered,
/// and that it skips seeding (with a warning, and without ever touching the database)
/// when <c>SEED_DEMO_PASSWORD</c> is not set. No Docker needed — this never opens a
/// database connection (see the assertion in
/// <see cref="SeedAsync_without_the_password_configured_skips_seeding_and_logs_a_warning"/>).
/// </summary>
public class DevelopmentSeedingTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void DevelopmentSeeder_is_registered_only_in_development(string environmentName, bool expectRegistered)
    {
        using var provider = BuildProvider(environmentName);

        (provider.GetService<DevelopmentSeeder>() is not null).ShouldBe(expectRegistered);
    }

    [Fact]
    public async Task SeedDevelopmentDataAsync_is_a_no_op_outside_development()
    {
        using var provider = BuildProvider("Production");

        // Would throw connecting to the unreachable database if it ever tried to seed.
        await provider.SeedDevelopmentDataAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SeedAsync_without_the_password_configured_skips_seeding_and_logs_a_warning()
    {
        var configuration = new ConfigurationBuilder().Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var logger = new ListLogger<DevelopmentSeeder>();

        // An empty container: the unset-password path must never need Identity's
        // UserManager<Account> (or anything else) resolved from it.
        var services = new ServiceCollection().BuildServiceProvider();
        var seeder = new DevelopmentSeeder(configuration, dbContextOptions, logger, services);

        // Would throw connecting to the unreachable database if this ever tried to seed.
        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("SEED_DEMO_PASSWORD");
        warning.Message.ShouldContain(".env.example");
    }

    [Fact]
    public async Task SeedAsync_with_a_blank_password_also_skips_seeding_and_logs_a_warning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(DevelopmentSeeder.PasswordConfigurationKey, "   ")])
            .Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var logger = new ListLogger<DevelopmentSeeder>();
        var services = new ServiceCollection().BuildServiceProvider();
        var seeder = new DevelopmentSeeder(configuration, dbContextOptions, logger, services);

        await seeder.SeedAsync(TestContext.Current.CancellationToken);

        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    /// <summary>
    /// Issue #29: a <c>SEED_DEMO_PASSWORD</c> that fails Identity's password rules must
    /// fail <c>migrate</c> loudly, before anything is written, with a message naming the
    /// failing rules and never the password. No Docker needed: both the seeder's own
    /// <see cref="AppDbContext"/> and the one behind the <see cref="UserManager{TUser}"/>
    /// used to validate point at <see cref="PostgresFixture.UnreachableConnectionString"/>,
    /// so an attempt to touch either would fail with a connection error instead of the
    /// expected <see cref="InvalidOperationException"/>.
    /// </summary>
    [Theory]
    [InlineData("1234", "PasswordTooShort")]
    [InlineData("password", "PasswordTooShort")]
    public async Task SeedAsync_with_a_weak_password_throws_before_writing_anything(string weakPassword, string expectedErrorCode)
    {
        var seeder = BuildSeederWithIdentityValidation(weakPassword, out var services);
        await using var disposable = services;

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => seeder.SeedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(DevelopmentSeeder.PasswordConfigurationKey);
        exception.Message.ShouldContain(expectedErrorCode);
        // Case.Sensitive: Shouldly's default is case-insensitive, which would false-positive
        // here purely because "password" (the literal test value) is a case-insensitive
        // substring of rule codes like "PasswordTooShort" — not an actual leak. The real
        // requirement is that the exact password value never appears.
        exception.Message.ShouldNotContain(weakPassword, Case.Sensitive);
    }

    [Fact]
    public async Task SeedAsync_with_an_11_character_password_is_rejected_for_being_too_short()
    {
        var weakPassword = "Aa1!" + new string('a', 7); // 11 characters: every class present, one short of the minimum.
        weakPassword.Length.ShouldBe(11);
        var seeder = BuildSeederWithIdentityValidation(weakPassword, out var services);
        await using var disposable = services;

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => seeder.SeedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("PasswordTooShort");
        // Case.Sensitive: Shouldly's default is case-insensitive, which would false-positive
        // here purely because "password" (the literal test value) is a case-insensitive
        // substring of rule codes like "PasswordTooShort" — not an actual leak. The real
        // requirement is that the exact password value never appears.
        exception.Message.ShouldNotContain(weakPassword, Case.Sensitive);
    }

    [Fact]
    public async Task SeedAsync_with_a_password_missing_a_symbol_is_rejected()
    {
        var weakPassword = "Aa1" + new string('a', 9); // 12 characters, upper/lower/digit, no symbol.
        weakPassword.Length.ShouldBe(12);
        var seeder = BuildSeederWithIdentityValidation(weakPassword, out var services);
        await using var disposable = services;

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => seeder.SeedAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("PasswordRequiresNonAlphanumeric");
        // Case.Sensitive: Shouldly's default is case-insensitive, which would false-positive
        // here purely because "password" (the literal test value) is a case-insensitive
        // substring of rule codes like "PasswordTooShort" — not an actual leak. The real
        // requirement is that the exact password value never appears.
        exception.Message.ShouldNotContain(weakPassword, Case.Sensitive);
    }

    [Fact]
    public async Task A_password_meeting_every_rule_is_never_rejected_by_validation()
    {
        // AuthHostFixture.SeedDemoPassword, the password the Docker-backed seeding tests
        // use: this must keep satisfying the rules validation now enforces.
        const string strongPassword = "Seed-Demo-Password-1!";
        var services = BuildIdentityServices(out var userManager);
        await using var disposable = services;

        var probeOrganization = new Organization(Guid.CreateVersion7(), "probe", "probe");
        var probeAccount = Account.Create(probeOrganization, "probe", "Probe", AccountRole.SmbAdmin);

        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, probeAccount, strongPassword);
            result.Succeeded.ShouldBeTrue(string.Join(
                "; ", result.Errors.Select(error => $"{error.Code}: {error.Description}")));
        }
    }

    /// <summary>Builds a <see cref="DevelopmentSeeder"/> wired the way <c>migrate</c> wires
    /// it (Identity registered, so <c>SEED_DEMO_PASSWORD</c> validation can resolve
    /// <see cref="UserManager{TUser}"/>) but pointed at an unreachable database, so a bug
    /// that let this seeder actually query or write shows up as a connection failure
    /// instead of quietly passing.</summary>
    private static DevelopmentSeeder BuildSeederWithIdentityValidation(string password, out ServiceProvider services)
    {
        services = BuildIdentityServices(out _);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(DevelopmentSeeder.PasswordConfigurationKey, password)])
            .Build();
        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options;
        var logger = new ListLogger<DevelopmentSeeder>();
        return new DevelopmentSeeder(configuration, dbContextOptions, logger, services);
    }

    /// <summary>The same <c>AddIdentityCore&lt;Account&gt;</c> configuration
    /// <c>AuthenticationServiceCollectionExtensions.AddSmartAgriAuthentication</c> uses for
    /// password rules, minus everything (OpenIddict, cookies) unrelated to password
    /// validation, backed by <see cref="PostgresFixture.UnreachableConnectionString"/> so
    /// resolving it never needs a real database.</summary>
    private static ServiceProvider BuildIdentityServices(out UserManager<Account> userManager)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOrganizationContext>(FixedOrganizationContext.None);
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(PostgresFixture.UnreachableConnectionString));
        services
            .AddIdentityCore<Account>(identity => identity.Password.RequiredLength = SmartAgri.Api.Authentication.AuthenticationServiceCollectionExtensions.MinimumPasswordLength)
            .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
            .AddEntityFrameworkStores<AppDbContext>();

        var provider = services.BuildServiceProvider();
        userManager = provider.GetRequiredService<UserManager<Account>>();
        return provider;
    }

    /// <summary>Every entry <see cref="DevelopmentSeeder"/> logs, in order — used to prove
    /// it warns instead of throwing when <c>SEED_DEMO_PASSWORD</c> is unset.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static ServiceProvider BuildProvider(string environmentName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresFixture.UnreachableConnectionString)
            .Options);
        services.AddDevelopmentSeeding(new HostingEnvironment { EnvironmentName = environmentName });
        return services.BuildServiceProvider();
    }
}
