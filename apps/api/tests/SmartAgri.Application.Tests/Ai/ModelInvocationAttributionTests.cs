using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Domain.Ai;

namespace SmartAgri.Application.Tests.Ai;

public class ModelInvocationAttributionTests
{
    private static readonly Guid Account = Guid.CreateVersion7();

    [Fact]
    public void Options_carry_the_attribution_to_the_middleware()
    {
        var attribution = new ModelInvocationAttribution(ModelInvocationPurpose.EmbedQuery, Account, null);

        ModelInvocationAttribution.From(attribution.ToEmbeddingOptions()).ShouldBe(attribution);
        ModelInvocationAttribution.From((EmbeddingGenerationOptions?)null).ShouldBeNull();
        ModelInvocationAttribution.From(new EmbeddingGenerationOptions()).ShouldBeNull();
        ModelInvocationAttribution.From(new EmbeddingGenerationOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ModelInvocationAttribution.PropertyName] = "forged" },
        }).ShouldBeNull();
    }

    [Fact]
    public void Stripping_removes_only_the_attribution_and_leaves_the_callers_options_alone()
    {
        var options = new ModelInvocationAttribution(ModelInvocationPurpose.EmbedDocument, Account, null).ToEmbeddingOptions();

        ModelInvocationAttribution.Strip(options).ShouldBeNull("nothing else was set");
        ModelInvocationAttribution.From(options).ShouldNotBeNull("the caller's instance is not changed");
        ModelInvocationAttribution.Strip((EmbeddingGenerationOptions?)null).ShouldBeNull();

        options.Dimensions = 512;
        options.AdditionalProperties!["user"] = "kept";
        var stripped = ModelInvocationAttribution.Strip(options).ShouldNotBeNull();
        stripped.Dimensions.ShouldBe(512);
        stripped.AdditionalProperties.ShouldNotBeNull().Keys.ShouldBe(["user"]);
        options.AdditionalProperties.Keys.ShouldBe([ModelInvocationAttribution.PropertyName, "user"], ignoreOrder: true);
    }

    [Fact]
    public void Chat_options_carry_the_attribution_to_the_middleware()
    {
        var attribution = new ModelInvocationAttribution(ModelInvocationPurpose.GenerateAnswer, Account, Guid.CreateVersion7());

        ModelInvocationAttribution.From(attribution.ToChatOptions()).ShouldBe(attribution);
        ModelInvocationAttribution.From((ChatOptions?)null).ShouldBeNull();
        ModelInvocationAttribution.From(new ChatOptions()).ShouldBeNull();
        ModelInvocationAttribution.From(new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ModelInvocationAttribution.PropertyName] = "forged" },
        }).ShouldBeNull();
    }

    [Fact]
    public void Stripping_chat_options_removes_only_the_attribution_and_keeps_every_other_setting()
    {
        var options = new ModelInvocationAttribution(ModelInvocationPurpose.GenerateAnswer, Account, null).ToChatOptions();
        options.MaxOutputTokens = 512;
        options.AdditionalProperties!["user"] = "kept";

        var stripped = ModelInvocationAttribution.Strip(options).ShouldNotBeNull();

        stripped.MaxOutputTokens.ShouldBe(512);
        stripped.AdditionalProperties.ShouldNotBeNull().Keys.ShouldBe(["user"]);
        ModelInvocationAttribution.From(options).ShouldNotBeNull("the caller's instance is not changed");
        ModelInvocationAttribution.Strip((ChatOptions?)null).ShouldBeNull();
    }
}
