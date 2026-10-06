using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Ai;

/// <summary>
/// The list of chat models at startup (M6 plan §3 A, Slice 1; issue #238): the settings CI and
/// the deployment compose file already use start as before, with one model; more models are
/// validated before the host starts, and the startup report names every model without its key.
/// No database needed (nothing here opens a connection).
/// </summary>
public sealed partial class ChatModelCatalogStartupTests : IDisposable
{
    private const string CertificatePassword = "test-password";
    private const string DefaultKey = "sk-default-never-logged";
    private const string SecondKey = "sk-second-never-logged";

    private readonly string _directory = Directory.CreateTempSubdirectory("smartagri-chat-models-").FullName;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // --- The settings in use today --------------------------------------------------------------

    [Fact]
    public async Task The_ci_workflows_chat_settings_start_the_api_with_one_model()
    {
        // The e2e-api job's own values (Ai__Chat__Provider: Fake, …), in its environment.
        var settings = EnvironmentSettings(File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml")), _ => null);
        settings.Keys.ShouldBe(["Ai:Chat:Provider", "Ai:Chat:Model"], ignoreOrder: true);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            Apply(builder, settings);
        });
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var only = factory.Services.GetRequiredService<ChatModelCatalog>().Entries.ShouldHaveSingleItem();
        (only.Id, only.Model, only.Provider.Name, only.IsDeploymentDefault).ShouldBe((settings["Ai:Chat:Model"], settings["Ai:Chat:Model"], "fake", true));
    }

    [Fact]
    public async Task The_deployment_compose_files_chat_settings_start_the_api_with_one_model()
    {
        // deploy/docker-compose.yml's Ai__Chat__* variables as a customer's .env fills them: a
        // provider, model and key; every other ${CHAT_…:-} left blank.
        var settings = ComposeSettings();
        string[] expected =
            ["Ai:Chat:Provider", "Ai:Chat:Endpoint", "Ai:Chat:Model", "Ai:Chat:ApiKey", "Ai:Chat:MaxOutputTokens", "Ai:Chat:TimeoutSeconds", "Ai:Chat:ReasoningEffort"];
        expected.ShouldBeSubsetOf(settings.Keys);
        settings["Ai:Chat:Endpoint"].ShouldBe(string.Empty, "blank, as an .env without CHAT_ENDPOINT binds it");

        using var factory = ProductionHost(builder => Apply(builder, settings));
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var catalog = factory.Services.GetRequiredService<ChatModelCatalog>();
        var only = catalog.Entries.ShouldHaveSingleItem();
        (only.Id, only.DisplayName, only.Model, only.Provider.Name).ShouldBe(("gpt-compose", "gpt-compose", "gpt-compose", "openai"));
        catalog.DeploymentDefault.Provider.ShouldBeSameAs(factory.Services.GetRequiredService<ChatClientProvider>());
    }

    [Fact]
    public async Task A_blank_second_model_is_skipped()
    {
        // As a compose file that reserves a second model's variables, all unset, binds them.
        using var factory = ProductionHost(builder =>
        {
            Apply(builder, ComposeSettings());
            foreach (var key in new[] { "Provider", "Endpoint", "Model", "ApiKey", "Id", "DisplayName", "MaxOutputTokens", "TimeoutSeconds", "ReasoningEffort" })
            {
                builder.UseSetting($"Ai:Chat:Models:0:{key}", string.Empty);
            }
        });
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Services.GetRequiredService<ChatModelCatalog>().Entries.ShouldHaveSingleItem().Id.ShouldBe("gpt-compose");
    }

    // --- More models ------------------------------------------------------------------------------

    [Fact]
    public async Task Two_models_start_with_their_own_settings_and_the_report_names_both_without_their_keys()
    {
        var log = new MessageLog();
        using var factory = ProductionHost(builder =>
        {
            builder.UseSetting("Ai:Chat:Provider", "OpenAICompatible");
            builder.UseSetting("Ai:Chat:Endpoint", "http://vllm:8000/v1");
            builder.UseSetting("Ai:Chat:Model", "vllm-chat");
            builder.UseSetting("Ai:Chat:ApiKey", DefaultKey);
            builder.UseSetting("Ai:Chat:Models:0:Provider", "OpenAICompatible");
            builder.UseSetting("Ai:Chat:Models:0:Endpoint", "http://vllm:8001/v1");
            builder.UseSetting("Ai:Chat:Models:0:Model", "vllm-reasoning");
            builder.UseSetting("Ai:Chat:Models:0:ApiKey", SecondKey);
            builder.UseSetting("Ai:Chat:Models:0:Id", "reasoning");
            builder.UseSetting("Ai:Chat:Models:0:DisplayName", "推理模型");
            builder.UseSetting("Ai:Chat:Models:0:MaxOutputTokens", "2048");
            builder.UseSetting("Ai:Chat:Models:0:ReasoningEffort", "High");
            builder.UseSetting("Ai:Chat:Models:0:TimeoutSeconds", "120");
            builder.ConfigureLogging(logging => logging.AddProvider(log));
        });
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var catalog = factory.Services.GetRequiredService<ChatModelCatalog>();
        catalog.Entries.Select(entry => (entry.Id, entry.DisplayName, entry.Model, entry.Provider.Endpoint!.Port, entry.IsDeploymentDefault))
            .ShouldBe([("vllm-chat", "vllm-chat", "vllm-chat", 8000, true), ("reasoning", "推理模型", "vllm-reasoning", 8001, false)]);
        var second = factory.Services.GetRequiredService<IOptions<ChatModelOptions>>().Value.Models.ShouldHaveSingleItem();
        (second.MaxOutputTokens, second.ReasoningEffortKind, second.TimeoutSeconds)
            .ShouldBe(((int?)2048, (Microsoft.Extensions.AI.ReasoningEffort?)Microsoft.Extensions.AI.ReasoningEffort.High, (int?)120));

        var report = log.Messages.Where(message => message.StartsWith("Chat: model", StringComparison.Ordinal)).ToList();
        report.ShouldBe(
        [
            "Chat: model vllm-chat (deployment default): provider openai-compatible, model vllm-chat.",
            "Chat: model reasoning: provider openai-compatible, model vllm-reasoning.",
        ]);
        log.Messages.ShouldAllBe(message => !message.Contains(DefaultKey) && !message.Contains(SecondKey));
    }

    [Fact]
    public void More_models_without_a_default_refuse_to_start_and_say_why()
    {
        using var factory = ProductionHost(builder =>
        {
            builder.UseSetting("Ai:Chat:Models:0:Provider", "OpenAICompatible");
            builder.UseSetting("Ai:Chat:Models:0:Endpoint", "http://vllm:8001/v1");
            builder.UseSetting("Ai:Chat:Models:0:Model", "vllm-second");
        });

        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain("Ai:Chat:Provider is required when Ai:Chat:Models lists more chat models");
    }

    [Fact]
    public void Two_models_with_one_id_refuse_to_start_and_say_which()
    {
        using var factory = ProductionHost(builder =>
        {
            Apply(builder, ComposeSettings());
            builder.UseSetting("Ai:Chat:Models:0:Provider", "OpenAI");
            builder.UseSetting("Ai:Chat:Models:0:Model", "gpt-compose");
            builder.UseSetting("Ai:Chat:Models:0:ApiKey", SecondKey);
            builder.UseSetting("Ai:Chat:Models:0:MaxOutputTokens", "64");
        });

        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain("Ai:Chat:Models:0:Id 'gpt-compose' is already the id of Ai:Chat");
    }

    [Fact]
    public void A_fake_second_model_in_production_refuses_to_start_and_says_why()
    {
        using var factory = ProductionHost(builder =>
        {
            Apply(builder, ComposeSettings());
            builder.UseSetting("Ai:Chat:Models:0:Provider", "Fake");
            builder.UseSetting("Ai:Chat:Models:0:Model", "fake-second");
        });

        StartupFailure.Of(factory).ShouldBeOfType<OptionsValidationException>().Message
            .ShouldContain("Ai:Chat:Models:0:Provider=Fake is only allowed in the Development and Testing environments, not in 'Production'");
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    /// <summary>deploy/docker-compose.yml's <c>Ai__Chat__*</c> settings with CHAT_PROVIDER,
    /// CHAT_MODEL and CHAT_API_KEY set and every other variable at its compose default.</summary>
    private static Dictionary<string, string> ComposeSettings()
    {
        var env = new Dictionary<string, string> { ["CHAT_PROVIDER"] = "OpenAI", ["CHAT_MODEL"] = "gpt-compose", ["CHAT_API_KEY"] = DefaultKey };
        return EnvironmentSettings(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "docker-compose.yml")),
            variable => env.GetValueOrDefault(variable));
    }

    /// <summary>
    /// The <c>Ai__Chat__*</c> environment variables a YAML file sets, as configuration keys — the
    /// environment variables provider's own mapping (<c>__</c> is <c>:</c>). A <c>${VAR:-default}</c>
    /// value is <paramref name="environment"/>'s value for VAR, or the default.
    /// </summary>
    private static Dictionary<string, string> EnvironmentSettings(string yaml, Func<string, string?> environment)
    {
        var settings = new Dictionary<string, string>();
        foreach (Match match in ChatVariable().Matches(yaml))
        {
            var value = match.Groups["value"].Value.Trim().Trim('\'', '"');
            var substitution = Substitution().Match(value);
            if (substitution.Success)
            {
                value = environment(substitution.Groups["name"].Value) ?? substitution.Groups["default"].Value;
            }

            settings[match.Groups["key"].Value.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return settings;
    }

    private static void Apply(IWebHostBuilder builder, Dictionary<string, string> settings)
    {
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nx.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (nx.json) above " + AppContext.BaseDirectory);
    }

    private WebApplicationFactory<Program> ProductionHost(Action<IWebHostBuilder> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Default", PostgresFixture.UnreachableConnectionString);
            builder.UseSetting("Authentication:SigningCertificatePath", WritePfx("signing", X509KeyUsageFlags.DigitalSignature));
            builder.UseSetting("Authentication:SigningCertificatePassword", CertificatePassword);
            builder.UseSetting("Authentication:EncryptionCertificatePath", WritePfx("encryption", X509KeyUsageFlags.KeyEncipherment));
            builder.UseSetting("Authentication:EncryptionCertificatePassword", CertificatePassword);
            DataProtectionTestSettings.Use(builder, _directory);
            configure(builder);
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

    [GeneratedRegex(@"^[ \t]+(?<key>Ai__Chat__[A-Za-z0-9_]+):[ \t]*(?<value>[^\r\n#]*)", RegexOptions.Multiline)]
    private static partial Regex ChatVariable();

    [GeneratedRegex(@"^\$\{(?<name>[A-Z0-9_]+):-(?<default>[^}]*)\}$")]
    private static partial Regex Substitution();

    /// <summary>Every log message of the host, formatted (the startup report's lines among them).</summary>
    private sealed class MessageLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
