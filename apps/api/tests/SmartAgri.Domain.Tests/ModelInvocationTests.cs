using Shouldly;
using SmartAgri.Domain.Ai;

namespace SmartAgri.Domain.Tests;

public class ModelInvocationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Account = Guid.CreateVersion7();

    [Fact]
    public void A_call_is_recorded_with_who_what_and_how_much_and_nothing_of_its_content()
    {
        var invocation = ModelInvocation.Record(Organization, Account, null, ModelInvocationPurpose.EmbedDocument, "openai", "text-embedding-3-small", 1234, 87, true, Now);

        invocation.Id.ShouldNotBe(Guid.Empty);
        (invocation.OrganizationId, invocation.AccountId, invocation.AssistantId).ShouldBe((Organization, (Guid?)Account, (Guid?)null));
        (invocation.Purpose, invocation.Provider, invocation.Model).ShouldBe((ModelInvocationPurpose.EmbedDocument, "openai", "text-embedding-3-small"));
        (invocation.InputTokens, invocation.DurationMs, invocation.Succeeded, invocation.At).ShouldBe(((long?)1234, 87L, true, Now));

        // The entity has no property that could hold text other than the provider and model names.
        typeof(ModelInvocation).GetProperties().Where(property => property.PropertyType == typeof(string)).Select(property => property.Name)
            .ShouldBe(["Provider", "Model"], ignoreOrder: true);
    }

    [Fact]
    public void Usage_may_be_unknown_but_nothing_else_may_be_missing()
    {
        ModelInvocation.Record(Organization, null, null, ModelInvocationPurpose.EmbedQuery, "fake", "m", null, 0, false, Now).InputTokens.ShouldBeNull();

        Should.Throw<ArgumentException>(() => ModelInvocation.Record(Guid.Empty, null, null, ModelInvocationPurpose.EmbedQuery, "fake", "m", null, 0, true, Now));
        Should.Throw<ArgumentException>(() => ModelInvocation.Record(Organization, Guid.Empty, null, ModelInvocationPurpose.EmbedQuery, "fake", "m", null, 0, true, Now));
        Should.Throw<ArgumentException>(() => ModelInvocation.Record(Organization, null, Guid.Empty, ModelInvocationPurpose.EmbedQuery, "fake", "m", null, 0, true, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => ModelInvocation.Record(Organization, null, null, (ModelInvocationPurpose)9, "fake", "m", null, 0, true, Now));
        Should.Throw<ArgumentException>(() => ModelInvocation.Record(Organization, null, null, ModelInvocationPurpose.EmbedQuery, " ", "m", null, 0, true, Now));
        Should.Throw<ArgumentException>(() => ModelInvocation.Record(Organization, null, null, ModelInvocationPurpose.EmbedQuery, "fake", new string('m', ModelInvocation.ModelMaxLength + 1), null, 0, true, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => ModelInvocation.Record(Organization, null, null, ModelInvocationPurpose.EmbedQuery, "fake", "m", -1, 0, true, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => ModelInvocation.Record(Organization, null, null, ModelInvocationPurpose.EmbedQuery, "fake", "m", null, -1, true, Now));
    }

    [Fact]
    public void Purposes_have_the_plans_wire_names()
    {
        WireNames<ModelInvocationPurpose>.All.ShouldBe(["embed-document", "embed-query"]);
    }
}
