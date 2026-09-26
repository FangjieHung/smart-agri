using Shouldly;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Ai;

/// <summary><c>Ai:Embedding</c> rules (M2 plan §3); no host, no database.</summary>
public class EmbeddingOptionsTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("development")]
    public void Fake_is_allowed_in_development_and_testing(string environment)
    {
        new EmbeddingOptions { Provider = "Fake", Model = "fake-a" }.Validate(environment).ShouldBeNull();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Demo")]
    public void Fake_anywhere_else_is_refused_with_the_reason(string environment)
    {
        var error = new EmbeddingOptions { Provider = "fake", Model = "fake-a" }.Validate(environment).ShouldNotBeNull();

        error.ShouldContain("Ai:Embedding:Provider=Fake is only allowed in the Development and Testing environments");
        error.ShouldContain($"'{environment}'");
    }

    [Fact]
    public void No_provider_is_allowed_everywhere_and_embeds_nothing()
    {
        var options = new EmbeddingOptions();

        options.Validate("Production").ShouldBeNull();
        options.ProviderKind.ShouldBeNull();
        options.ToSettings().Model.ShouldBeEmpty();
        EmbeddingProvider.Create(options).IsConfigured.ShouldBeFalse();
        new EmbeddingOptions { Provider = "  " }.Validate("Production").ShouldBeNull();
    }

    [Theory]
    [InlineData("OpenAI", null, "sk-test", null)]
    [InlineData("openai", "https://api.openai.com/v1", "sk-test", null)]
    [InlineData("AzureOpenAI", "https://contoso.openai.azure.com/openai/v1/", "key", null)]
    [InlineData("OpenAICompatible", "http://vllm:8000/v1", null, null)]
    [InlineData("OpenAICompatible", "http://vllm:8000/v1", "token", null)]
    [InlineData("OpenAI", null, null, "Ai:Embedding:ApiKey is required for OpenAI")]
    [InlineData("AzureOpenAI", null, "key", "Ai:Embedding:Endpoint is required for AzureOpenAI")]
    [InlineData("AzureOpenAI", "https://contoso.openai.azure.com/openai/v1/", null, "Ai:Embedding:ApiKey is required for AzureOpenAI")]
    [InlineData("OpenAICompatible", null, null, "Ai:Embedding:Endpoint is required for OpenAICompatible")]
    [InlineData("OpenAICompatible", "vllm:8000", null, "must be an absolute http or https address")]
    [InlineData("OpenAICompatible", "ftp://vllm/v1", null, "must be an absolute http or https address")]
    [InlineData("Anthropic", null, "key", "must be OpenAI, AzureOpenAI, OpenAICompatible or Fake, not 'Anthropic'")]
    [InlineData("3", null, "key", "must be OpenAI, AzureOpenAI, OpenAICompatible or Fake")]
    public void Each_provider_needs_what_its_client_needs(string provider, string? endpoint, string? apiKey, string? expectedError)
    {
        var error = new EmbeddingOptions { Provider = provider, Endpoint = endpoint, ApiKey = apiKey, Model = "m" }.Validate("Production");

        if (expectedError is null)
        {
            error.ShouldBeNull();
        }
        else
        {
            error.ShouldNotBeNull().ShouldContain(expectedError);
        }
    }

    [Fact]
    public void A_provider_needs_a_model_and_batches_must_fit_the_providers()
    {
        new EmbeddingOptions { Provider = "Fake" }.Validate("Development").ShouldNotBeNull().ShouldContain("Ai:Embedding:Model is required");
        new EmbeddingOptions { Provider = "Fake", Model = new string('m', 201) }.Validate("Development").ShouldNotBeNull();
        new EmbeddingOptions { BatchSize = 0 }.Validate("Development").ShouldNotBeNull().ShouldContain("BatchSize");
        new EmbeddingOptions { BatchSize = 2049 }.Validate("Development").ShouldNotBeNull();
        new EmbeddingOptions().BatchSize.ShouldBe(64);
    }

    [Fact]
    public void Settings_carry_the_trimmed_model_and_the_prefixes()
    {
        var settings = new EmbeddingOptions { Provider = "Fake", Model = " multilingual-e5 ", QueryPrefix = "query: ", DocumentPrefix = "passage: ", BatchSize = 16 }.ToSettings();

        (settings.Model, settings.QueryPrefix, settings.DocumentPrefix, settings.BatchSize).ShouldBe(("multilingual-e5", "query: ", "passage: ", 16));
    }

    [Fact]
    public void Each_provider_builds_its_client_and_names_itself()
    {
        Describe(new EmbeddingOptions { Provider = "Fake", Model = "fake-a" }).ShouldBe(("fake", "smartagri.fake", "fake-a", null));
        Describe(new EmbeddingOptions { Provider = "OpenAI", Model = "text-embedding-3-small", ApiKey = "sk" })
            .ShouldBe(("openai", "openai", "text-embedding-3-small", "api.openai.com"));
        Describe(new EmbeddingOptions { Provider = "AzureOpenAI", Model = "embed", ApiKey = "k", Endpoint = "https://contoso.openai.azure.com/openai/v1/" })
            .ShouldBe(("azure-openai", "azure.ai.openai", "embed", "contoso.openai.azure.com"));
        Describe(new EmbeddingOptions { Provider = "OpenAICompatible", Model = "intfloat/multilingual-e5-large", Endpoint = "http://vllm:8000/v1" })
            .ShouldBe(("openai-compatible", "openai", "intfloat/multilingual-e5-large", "vllm"));
    }

    private static (string Name, string TelemetryName, string Model, string? Host) Describe(EmbeddingOptions options)
    {
        using var provider = EmbeddingProvider.Create(options);
        provider.IsConfigured.ShouldBeTrue();
        return (provider.Name, provider.TelemetryName, provider.Model, provider.Endpoint?.Host);
    }
}
