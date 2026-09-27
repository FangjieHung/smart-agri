using System.Text.Json;
using Shouldly;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Tests.Assistants;

public class AssistantDraftPayloadRulesTests
{
    [Fact]
    public void A_json_object_within_the_limit_is_accepted_and_re_serialized()
    {
        var payload = Parse("""{"name":"客服助理","sources":[]}""");

        var result = AssistantDraftPayloadRules.ForSave(payload);

        result.IsValid.ShouldBeTrue();
        result.Value.ShouldBe("""{"name":"客服助理","sources":[]}""");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("123")]
    [InlineData("null")]
    public void Anything_other_than_a_json_object_is_rejected(string json)
    {
        var result = AssistantDraftPayloadRules.ForSave(Parse(json));

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftPayloadRules.PayloadField);
    }

    [Fact]
    public void A_payload_over_the_byte_limit_is_rejected()
    {
        var big = new string('字', AssistantDraft.PayloadMaxBytes);
        var payload = Parse($$"""{"name":"{{big}}"}""");

        var result = AssistantDraftPayloadRules.ForSave(payload);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(failure => failure.Field == AssistantDraftPayloadRules.PayloadField);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
