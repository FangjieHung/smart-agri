using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;
using SmartAgri.Api.PublicChannels;
using SmartAgri.Api.Tests.Infrastructure;

namespace SmartAgri.Api.Tests.PublicChannels;

/// <summary>
/// The reverse-proxy misconfiguration warning (M5a plan §7 risk 4; #197): a Production host with no
/// <c>PublicChannels:TrustedProxies</c> that receives <c>X-Forwarded-For</c> logs one warning, once.
/// No database needed (<c>/health/live</c> opens no connection).
/// </summary>
public sealed class TrustedProxiesStartupTests : IDisposable
{
    private const string CertificatePassword = "test-password";

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-trusted-proxies-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_production_host_without_trusted_proxies_warns_once_when_a_request_carries_x_forwarded_for()
    {
        var log = new WarningLog();
        using var factory = ProductionHost(log, trustedProxy: null);
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).EnsureSuccessStatusCode();
        log.Warnings.ShouldBeEmpty("no X-Forwarded-For, nothing to warn about");

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            request.Headers.Add("X-Forwarded-For", "198.51.100.1");
            (await client.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode();
        }

        var warning = log.Warnings.ShouldHaveSingleItem("once, however many requests");
        warning.ShouldContain("PublicChannels:TrustedProxies");
        warning.ShouldContain("X-Forwarded-For");
    }

    [Fact]
    public async Task A_production_host_with_trusted_proxies_does_not_warn()
    {
        var log = new WarningLog();
        using var factory = ProductionHost(log, trustedProxy: "10.0.0.0/8");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1");
        (await client.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode();

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_development_host_without_trusted_proxies_does_not_warn()
    {
        var log = new WarningLog();
        using var factory = ProductionHost(log, trustedProxy: null, environment: "Development");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Forwarded-For", "198.51.100.1");
        (await client.SendAsync(request, CancellationToken)).EnsureSuccessStatusCode();

        log.Warnings.ShouldBeEmpty();
    }

    private WebApplicationFactory<Program> ProductionHost(WarningLog log, string? trustedProxy, string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Authentication:SigningCertificatePath", WritePfx("signing", X509KeyUsageFlags.DigitalSignature));
            builder.UseSetting("Authentication:SigningCertificatePassword", CertificatePassword);
            builder.UseSetting("Authentication:EncryptionCertificatePath", WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment));
            builder.UseSetting("Authentication:EncryptionCertificatePassword", CertificatePassword);
            DataProtectionTestSettings.Use(builder, _directory);
            if (trustedProxy is not null)
            {
                builder.UseSetting("PublicChannels:TrustedProxies:0", trustedProxy);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(log));
        });

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

    /// <summary>The messages of every warning the <see cref="TrustedProxies"/> logger wrote.</summary>
    private sealed class WarningLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName, Warnings);

        public void Dispose()
        {
        }

        private sealed class CategoryLogger(string category, ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && category == typeof(TrustedProxies).FullName)
                {
                    warnings.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
