using System.Text.Json;
using SmartAgri.Application.Answers;
using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Turns a draft's <see cref="AssistantDraft.Payload"/> into a <see cref="GroundedAnswerProfile"/>
/// for a trial answer (ticket #78, M3 plan Slice 8). Unlike
/// <see cref="AssistantDraftCreationRules"/> (由草稿建立助理), this never fails: a trial question
/// must work while the draft is still being edited and incomplete. Every field the profile needs
/// gets a reasonable default when the payload omits it, is the wrong shape, or does not parse —
/// the only things that can make a trial answer refuse are the question itself (validated by the
/// caller, see <see cref="ValidateQuestion"/>) and the embedding/chat provider.
/// </summary>
/// <remarks>
/// Reuses the same lenient DTOs as <see cref="AssistantDraftCreationRules"/> (internal to this
/// assembly): only the shape of the JSON matters here, not whether it would pass creation.
/// </remarks>
public static class AssistantDraftTrialAnswerRules
{
    public const string QuestionField = "question";

    /// <summary>The longest trial question, in characters after trimming (M3 plan Slice 8).</summary>
    public const int QuestionMaxLength = 2000;

    public const string QuestionRequiredMessage = "請輸入要試問的問題。";

    public static readonly string QuestionTooLongMessage = $"問題最多 {QuestionMaxLength} 個字。";

    /// <summary>Shown when the draft names no name/purpose yet — a trial should work before the
    /// wizard's "用途" step is filled in.</summary>
    public const string DefaultName = "這個助理";

    public const string DefaultPurpose = "回答與草稿知識庫相關的問題";

    public const string DefaultRefusalMessage = "目前的資料中找不到這個問題的答案。";

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A non-blank question of at most <see cref="QuestionMaxLength"/> characters
    /// (trimmed).</summary>
    public static ValidationResult<string> ValidateQuestion(string? question)
    {
        var trimmed = (question ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ValidationResult<string>.Invalid(QuestionField, QuestionRequiredMessage);
        }

        if (trimmed.Length > QuestionMaxLength)
        {
            return ValidationResult<string>.Invalid(QuestionField, QuestionTooLongMessage);
        }

        return ValidationResult<string>.Valid(trimmed);
    }

    /// <summary>
    /// The profile to answer a trial question with. <paramref name="ownerAccountId"/> is the
    /// draft's owner: whose access decides which of the named knowledge bases may still be
    /// searched — re-checked by <see cref="GroundedAnswerService"/> itself on every call, so a
    /// source no longer connectable is silently dropped here just like it would be for a real
    /// assistant.
    /// </summary>
    public static GroundedAnswerProfile ProfileFor(string payloadJson, Guid ownerAccountId)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        AssistantDraftPayloadDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AssistantDraftPayloadDto>(payloadJson, DeserializeOptions);
        }
        catch (JsonException)
        {
            dto = null;
        }

        var name = NonBlank(dto?.Name) ?? DefaultName;
        var purpose = NonBlank(dto?.Purpose) ?? DefaultPurpose;
        var tone = ParseOr(dto?.Tone, AssistantTone.Friendly);
        var roleInstructions = (dto?.RoleInstructions ?? string.Empty).Trim();
        var knowledgeScope = ParseOr(dto?.Rules?.KnowledgeScope, AssistantKnowledgeScope.CompanyDataOnly);
        var refusalMessage = NonBlank(dto?.Rules?.RefusalMessage) ?? DefaultRefusalMessage;
        var knowledgeBaseIds = KnowledgeBaseIdsOf(dto?.Sources);

        return new GroundedAnswerProfile(
            name,
            purpose,
            tone,
            roleInstructions,
            knowledgeScope,
            refusalMessage,
            MinScore: null,
            ownerAccountId,
            knowledgeBaseIds);
    }

    private static string? NonBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static TEnum ParseOr<TEnum>(string? wire, TEnum fallback)
        where TEnum : struct, Enum =>
        wire is not null && WireNames<TEnum>.All.Contains(wire) ? WireNames<TEnum>.Parse(wire) : fallback;

    /// <summary>The distinct knowledge-base ids among <paramref name="sources"/>: anything that
    /// is not a <c>knowledge-base</c> source (e.g. M4's database sources), or whose <c>id</c>
    /// does not parse as a guid, is silently skipped — a trial answer works with whatever
    /// knowledge bases the draft names, however incomplete the rest of it is.</summary>
    private static IReadOnlyCollection<Guid> KnowledgeBaseIdsOf(List<AssistantDraftSourceDto>? sources)
    {
        if (sources is null)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var source in sources)
        {
            if (string.Equals(source.Type, "knowledge-base", StringComparison.Ordinal)
                && source.Id is not null
                && Guid.TryParse(source.Id, out var id)
                && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
