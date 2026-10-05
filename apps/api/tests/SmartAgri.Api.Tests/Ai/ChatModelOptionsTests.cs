using Shouldly;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Ai;

/// <summary><c>Ai:Chat</c> rules (M3 plan, Slice 4); no host, no database.</summary>
public class ChatModelOptionsTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("development")]
    public void Fake_is_allowed_in_development_and_testing(string environment)
    {
        new ChatModelOptions { Provider = "Fake", Model = "fake-a" }.Validate(environment).ShouldBeNull();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Demo")]
    public void Fake_anywhere_else_is_refused_with_the_reason(string environment)
    {
        var error = new ChatModelOptions { Provider = "fake", Model = "fake-a" }.Validate(environment).ShouldNotBeNull();

        error.ShouldContain("Ai:Chat:Provider=Fake is only allowed in the Development and Testing environments");
        error.ShouldContain($"'{environment}'");
    }

    [Fact]
    public void No_provider_is_allowed_everywhere_and_answers_nothing()
    {
        var options = new ChatModelOptions();

        options.Validate("Production").ShouldBeNull();
        options.ProviderKind.ShouldBeNull();
        ChatClientProvider.Create(options).IsConfigured.ShouldBeFalse();
        new ChatModelOptions { Provider = "  " }.Validate("Production").ShouldBeNull();
    }

    [Theory]
    [InlineData("OpenAI", null, "sk-test", null)]
    [InlineData("openai", "https://api.openai.com/v1", "sk-test", null)]
    [InlineData("AzureOpenAI", "https://contoso.openai.azure.com/openai/v1/", "key", null)]
    [InlineData("OpenAICompatible", "http://vllm:8000/v1", null, null)]
    [InlineData("OpenAICompatible", "http://vllm:8000/v1", "token", null)]
    [InlineData("OpenAI", null, null, "Ai:Chat:ApiKey is required for OpenAI")]
    [InlineData("AzureOpenAI", null, "key", "Ai:Chat:Endpoint is required for AzureOpenAI")]
    [InlineData("AzureOpenAI", "https://contoso.openai.azure.com/openai/v1/", null, "Ai:Chat:ApiKey is required for AzureOpenAI")]
    [InlineData("OpenAICompatible", null, null, "Ai:Chat:Endpoint is required for OpenAICompatible")]
    [InlineData("OpenAICompatible", "vllm:8000", null, "must be an absolute http or https address")]
    [InlineData("OpenAICompatible", "ftp://vllm/v1", null, "must be an absolute http or https address")]
    [InlineData("Anthropic", null, "key", "must be OpenAI, AzureOpenAI, OpenAICompatible or Fake, not 'Anthropic'")]
    [InlineData("3", null, "key", "must be OpenAI, AzureOpenAI, OpenAICompatible or Fake")]
    public void Each_provider_needs_what_its_client_needs(string provider, string? endpoint, string? apiKey, string? expectedError)
    {
        var error = new ChatModelOptions { Provider = provider, Endpoint = endpoint, ApiKey = apiKey, Model = "m" }.Validate("Production");

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
    public void A_provider_needs_a_model()
    {
        new ChatModelOptions { Provider = "Fake" }.Validate("Development").ShouldNotBeNull().ShouldContain("Ai:Chat:Model is required");
        new ChatModelOptions { Provider = "Fake", Model = new string('m', 201) }.Validate("Development").ShouldNotBeNull();
    }

    [Fact]
    public void Max_output_tokens_and_timeout_must_be_positive_when_set()
    {
        new ChatModelOptions { MaxOutputTokens = 0 }.Validate("Development").ShouldNotBeNull().ShouldContain("MaxOutputTokens");
        new ChatModelOptions { MaxOutputTokens = -1 }.Validate("Development").ShouldNotBeNull();
        new ChatModelOptions { MaxOutputTokens = 512 }.Validate("Development").ShouldBeNull();
        new ChatModelOptions { TimeoutSeconds = 0 }.Validate("Development").ShouldNotBeNull().ShouldContain("TimeoutSeconds");
        new ChatModelOptions { TimeoutSeconds = -1 }.Validate("Development").ShouldNotBeNull();
        new ChatModelOptions { TimeoutSeconds = 30 }.Validate("Development").ShouldBeNull();
    }

    [Fact]
    public void Each_provider_builds_its_client_and_names_itself()
    {
        Describe(new ChatModelOptions { Provider = "Fake", Model = "fake-a" }).ShouldBe(("fake", "smartagri.fake", "fake-a", null));
        Describe(new ChatModelOptions { Provider = "OpenAI", Model = "gpt-test", ApiKey = "sk" })
            .ShouldBe(("openai", "openai", "gpt-test", "api.openai.com"));
        Describe(new ChatModelOptions { Provider = "AzureOpenAI", Model = "chat", ApiKey = "k", Endpoint = "https://contoso.openai.azure.com/openai/v1/" })
            .ShouldBe(("azure-openai", "azure.ai.openai", "chat", "contoso.openai.azure.com"));
        Describe(new ChatModelOptions { Provider = "OpenAICompatible", Model = "meta/llama", Endpoint = "http://vllm:8000/v1" })
            .ShouldBe(("openai-compatible", "openai", "meta/llama", "vllm"));
    }

    [Fact]
    public void Max_output_tokens_configures_the_client_without_needing_a_call_option()
    {
        using var provider = ChatClientProvider.Create(new ChatModelOptions { Provider = "OpenAI", Model = "gpt-test", ApiKey = "sk", MaxOutputTokens = 256 });

        provider.IsConfigured.ShouldBeTrue();
    }

    [Theory]
    [InlineData("None", Microsoft.Extensions.AI.ReasoningEffort.None)]
    [InlineData(" low ", Microsoft.Extensions.AI.ReasoningEffort.Low)]
    [InlineData("extrahigh", Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh)]
    public void Reasoning_effort_is_a_known_level_when_set(string value, Microsoft.Extensions.AI.ReasoningEffort expected)
    {
        var options = new ChatModelOptions { ReasoningEffort = value };

        options.Validate("Development").ShouldBeNull();
        options.ReasoningEffortKind.ShouldBe(expected);
        new ChatModelOptions().ReasoningEffortKind.ShouldBeNull();
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("0")]
    [InlineData("off")]
    public void Any_other_reasoning_effort_is_refused(string value) =>
        new ChatModelOptions { ReasoningEffort = value }.Validate("Development")
            .ShouldNotBeNull().ShouldContain("Ai:Chat:ReasoningEffort must be None, Low, Medium, High or ExtraHigh");

    [Fact]
    public void Reasoning_effort_configures_the_client_without_needing_a_call_option()
    {
        using var provider = ChatClientProvider.Create(new ChatModelOptions { Provider = "OpenAI", Model = "gpt-test", ApiKey = "sk", ReasoningEffort = "None" });

        provider.IsConfigured.ShouldBeTrue();
    }

    private static (string Name, string TelemetryName, string Model, string? Host) Describe(ChatModelOptions options)
    {
        using var provider = ChatClientProvider.Create(options);
        provider.IsConfigured.ShouldBeTrue();
        return (provider.Name, provider.TelemetryName, provider.Model, provider.Endpoint?.Host);
    }
}
