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
        ModelInvocationAttribution.From(null).ShouldBeNull();
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
        ModelInvocationAttribution.Strip(null).ShouldBeNull();

        options.Dimensions = 512;
        options.AdditionalProperties!["user"] = "kept";
        var stripped = ModelInvocationAttribution.Strip(options).ShouldNotBeNull();
        stripped.Dimensions.ShouldBe(512);
        stripped.AdditionalProperties.ShouldNotBeNull().Keys.ShouldBe(["user"]);
        options.AdditionalProperties.Keys.ShouldBe([ModelInvocationAttribution.PropertyName, "user"], ignoreOrder: true);
    }
}
