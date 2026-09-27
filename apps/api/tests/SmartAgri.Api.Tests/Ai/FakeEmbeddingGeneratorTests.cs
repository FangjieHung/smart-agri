using Microsoft.Extensions.AI;
using Shouldly;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Tests.Ai;

public class FakeEmbeddingGeneratorTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_text_and_model_always_give_the_same_unit_vector()
    {
        using var generator = new FakeEmbeddingGenerator("fake-a");

        var first = await generator.GenerateAsync(["收到商品後七天內可以退貨", "Refunds within 5 days"], cancellationToken: CancellationToken);
        var again = await new FakeEmbeddingGenerator("fake-a").GenerateAsync(["收到商品後七天內可以退貨"], cancellationToken: CancellationToken);

        first.Count.ShouldBe(2);
        first[0].Vector.ToArray().ShouldBe(again[0].Vector.ToArray());
        first.ShouldAllBe(embedding => embedding.Vector.Length == FakeEmbeddingGenerator.Dimensions && embedding.ModelId == "fake-a");
        foreach (var embedding in first)
        {
            Math.Sqrt(embedding.Vector.ToArray().Sum(value => (double)value * value)).ShouldBe(1, 1e-5);
        }
    }

    [Fact]
    public void Texts_sharing_characters_are_closer_and_another_model_is_unrelated()
    {
        var generator = new FakeEmbeddingGenerator("fake-a");
        var question = generator.Embed("幾天內可以退貨？");

        Cosine(question, generator.Embed("收到商品後七天內可以退貨。")).ShouldBeGreaterThan(Cosine(question, generator.Embed("本島運費 100 元，離島 150 元。")));
        Cosine(generator.Embed("退貨"), new FakeEmbeddingGenerator("fake-b").Embed("退貨")).ShouldBeLessThan(0.5);
    }

    [Fact]
    public void Even_empty_text_gets_a_usable_vector()
    {
        var vector = new FakeEmbeddingGenerator("fake-a").Embed(string.Empty);

        vector.Count(value => value != 0).ShouldBe(1);
        vector.Sum(value => value * value).ShouldBe(1, 1e-6);
    }

    [Fact]
    public async Task Usage_is_one_token_per_character()
    {
        var generated = await new FakeEmbeddingGenerator("fake-a").GenerateAsync(["退貨", "a😀"], cancellationToken: CancellationToken);

        generated.Usage.ShouldNotBeNull().InputTokenCount.ShouldBe(4);
    }

    [Fact]
    public void It_describes_itself_as_the_fake_provider()
    {
        var metadata = new FakeEmbeddingGenerator("fake-a").GetService<EmbeddingGeneratorMetadata>().ShouldNotBeNull();

        (metadata.ProviderName, metadata.DefaultModelId, metadata.DefaultModelDimensions).ShouldBe(("fake", "fake-a", (int?)FakeEmbeddingGenerator.Dimensions));
    }

    private static double Cosine(float[] left, float[] right) => left.Zip(right, (a, b) => (double)a * b).Sum();
}
