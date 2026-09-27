using System.Text.Json;
using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>The handful of an <see cref="AssistantDraft"/>'s frontend fields
/// (<c>apps/admin/src/app/core/domain/assistant-draft.model.ts</c>'s <c>AssistantDraft</c>)
/// that become an <see cref="Assistant"/> row. Every property is optional on the wire: an
/// absent or wrongly-typed field is this rule's own <c>422</c>, not a deserialization
/// failure that would 500.</summary>
internal sealed class AssistantDraftPayloadDto
{
    public string? TemplateId { get; set; }

    public string? Name { get; set; }

    public string? Purpose { get; set; }

    public string? Tone { get; set; }

    public string? Audience { get; set; }

    public string? RoleInstructions { get; set; }

    public List<AssistantDraftSourceDto>? Sources { get; set; }

    public AssistantDraftRulesDto? Rules { get; set; }
}

internal sealed class AssistantDraftSourceDto
{
    public string? Id { get; set; }

    public string? Type { get; set; }
}

internal sealed class AssistantDraftRulesDto
{
    public string? KnowledgeScope { get; set; }

    public string? RefusalMessage { get; set; }

    public bool? ShowCitations { get; set; }

    public bool? KeepOwnConversations { get; set; }
}

/// <summary>A draft's fields, validated, trimmed and resolved to enums, ready to build an
/// <see cref="Assistant"/> from (<see cref="Assistant.Create"/>).</summary>
public sealed record AssistantDraftCreationDetails(
    string Name,
    string Purpose,
    string? TemplateId,
    AssistantTone Tone,
    string RoleInstructions,
    AssistantKnowledgeScope KnowledgeScope,
    string RefusalMessage,
    bool ShowCitations,
    bool KeepConversations,
    IReadOnlyList<Guid> KnowledgeBaseIds);

/// <summary>
/// Field-by-field validation of "由草稿建立助理" (M3 plan §3, Slice 2 acceptance;
/// <c>docs/handoff/mock-to-api-mapping.md</c> §2.1's <c>createAssistantFromDraft</c>). Only
/// this step parses <see cref="AssistantDraft.Payload"/>; everything it does not need
/// (<c>currentStep</c>, <c>testedQuestionIds</c>, the M4 database-write fields) is ignored.
/// </summary>
/// <remarks>
/// Knowledge-base connectability is checked by the caller, not here: it needs a database
/// query (<see cref="AssistantKnowledgeAccess.ConnectableBy"/>), so the endpoint passes in
/// the resulting set of ids the owner may connect, and this stays a pure function like
/// <see cref="AssistantSettingsRules"/>.
/// </remarks>
public static class AssistantDraftCreationRules
{
    public const string NameField = "name";

    public const string PurposeField = "purpose";

    public const string AudienceField = "audience";

    public const string SourcesField = "sources";

    public const string RefusalMessageField = "refusalMessage";

    public const string ToneField = "tone";

    public const string KnowledgeScopeField = "knowledgeScope";

    public const string RoleInstructionsField = "roleInstructions";

    public const string NameRequiredMessage = "請輸入助理名稱。";

    public static readonly string NameTooLongMessage = $"助理名稱最多 {Assistant.NameMaxLength} 個字。";

    public const string PurposeRequiredMessage = "請用一句話說明助理要幫忙完成的工作。";

    public static readonly string PurposeTooLongMessage = $"用途說明最多 {Assistant.PurposeMaxLength} 個字。";

    /// <summary>Only <c>account-members</c> (組織內) or absent — treated as organization
    /// internal — is accepted in M3; any other audience is external publishing, not built
    /// yet (M3 plan §8; issue #72's scope note).</summary>
    public const string AudienceNotAvailableMessage = "對外發布將於後續版本開放，目前僅支援組織內使用。";

    public const string SourcesRequiredMessage = "請至少加入一個可連接的知識庫。";

    public const string SourceNotConnectableMessage = "草稿裡的知識庫無法連接，可能已不存在、屬於其他組織，或已被收回分享。";

    public const string DatabaseSourceNotAvailableMessage = "資料庫來源將於後續版本開放，目前只能連接知識庫。";

    public const string RefusalMessageRequiredMessage = "請填寫找不到資料時的回覆內容。";

    public static readonly string RefusalMessageTooLongMessage =
        $"回覆內容最多 {Assistant.RefusalMessageMaxLength} 個字。";

    public const string ToneInvalidMessage = "語氣設定不正確。";

    public const string KnowledgeScopeInvalidMessage = "回答範圍設定不正確。";

    public static readonly string RoleInstructionsTooLongMessage =
        $"角色設定最多 {Assistant.RoleInstructionsMaxLength} 個字。";

    public const string PayloadMalformedMessage = "草稿內容毀損，無法建立助理，請重新編輯草稿後再試一次。";

    private const string AccountMembersAudience = "account-members";

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses <paramref name="payloadJson"/> (an <see cref="AssistantDraft.Payload"/>) and
    /// validates the fields "由草稿建立助理" needs, against
    /// <paramref name="connectableKnowledgeBaseIds"/> — the knowledge bases the draft's
    /// owner may currently connect (already re-checked for organization and sharing by the
    /// caller). A source naming any other id, or a source whose <c>type</c> is not
    /// <c>knowledge-base</c>, fails with <see cref="SourcesField"/>, so a draft naming
    /// another organization's knowledge base id is refused the same way as one that is no
    /// longer shared (M3 plan Slice 2 acceptance).
    /// </summary>
    public static ValidationResult<AssistantDraftCreationDetails> Validate(
        string payloadJson, IReadOnlySet<Guid> connectableKnowledgeBaseIds)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentNullException.ThrowIfNull(connectableKnowledgeBaseIds);

        AssistantDraftPayloadDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<AssistantDraftPayloadDto>(payloadJson, DeserializeOptions);
        }
        catch (JsonException)
        {
            return ValidationResult<AssistantDraftCreationDetails>.Invalid(NameField, PayloadMalformedMessage);
        }

        if (dto is null)
        {
            return ValidationResult<AssistantDraftCreationDetails>.Invalid(NameField, PayloadMalformedMessage);
        }

        var failures = new List<ValidationFailure>();

        var trimmedName = (dto.Name ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
        {
            failures.Add(new ValidationFailure(NameField, NameRequiredMessage));
        }
        else if (trimmedName.Length > Assistant.NameMaxLength)
        {
            failures.Add(new ValidationFailure(NameField, NameTooLongMessage));
        }

        var trimmedPurpose = (dto.Purpose ?? string.Empty).Trim();
        if (trimmedPurpose.Length == 0)
        {
            failures.Add(new ValidationFailure(PurposeField, PurposeRequiredMessage));
        }
        else if (trimmedPurpose.Length > Assistant.PurposeMaxLength)
        {
            failures.Add(new ValidationFailure(PurposeField, PurposeTooLongMessage));
        }

        if (dto.Audience is not null && !string.Equals(dto.Audience, AccountMembersAudience, StringComparison.Ordinal))
        {
            failures.Add(new ValidationFailure(AudienceField, AudienceNotAvailableMessage));
        }

        var resolvedTone = AssistantTone.Friendly;
        if (dto.Tone is not null && !TryParse(dto.Tone, out resolvedTone))
        {
            failures.Add(new ValidationFailure(ToneField, ToneInvalidMessage));
        }

        var trimmedInstructions = (dto.RoleInstructions ?? string.Empty).Trim();
        if (trimmedInstructions.Length > Assistant.RoleInstructionsMaxLength)
        {
            failures.Add(new ValidationFailure(RoleInstructionsField, RoleInstructionsTooLongMessage));
        }

        var resolvedScope = AssistantKnowledgeScope.CompanyDataOnly;
        var trimmedRefusal = string.Empty;
        bool showCitations = true;
        bool keepConversations = true;
        if (dto.Rules is null)
        {
            failures.Add(new ValidationFailure(RefusalMessageField, RefusalMessageRequiredMessage));
        }
        else
        {
            if (dto.Rules.KnowledgeScope is not null && !TryParse(dto.Rules.KnowledgeScope, out resolvedScope))
            {
                failures.Add(new ValidationFailure(KnowledgeScopeField, KnowledgeScopeInvalidMessage));
            }

            trimmedRefusal = (dto.Rules.RefusalMessage ?? string.Empty).Trim();
            if (trimmedRefusal.Length == 0)
            {
                failures.Add(new ValidationFailure(RefusalMessageField, RefusalMessageRequiredMessage));
            }
            else if (trimmedRefusal.Length > Assistant.RefusalMessageMaxLength)
            {
                failures.Add(new ValidationFailure(RefusalMessageField, RefusalMessageTooLongMessage));
            }

            showCitations = dto.Rules.ShowCitations ?? true;
            keepConversations = dto.Rules.KeepOwnConversations ?? true;
        }

        var knowledgeBaseIds = ValidateSources(dto.Sources, connectableKnowledgeBaseIds, failures);

        if (failures.Count > 0)
        {
            return ValidationResult<AssistantDraftCreationDetails>.Invalid(failures);
        }

        return ValidationResult<AssistantDraftCreationDetails>.Valid(new AssistantDraftCreationDetails(
            trimmedName,
            trimmedPurpose,
            dto.TemplateId,
            resolvedTone,
            trimmedInstructions,
            resolvedScope,
            trimmedRefusal,
            showCitations,
            keepConversations,
            knowledgeBaseIds));
    }

    private static List<Guid> ValidateSources(
        List<AssistantDraftSourceDto>? sources,
        IReadOnlySet<Guid> connectableKnowledgeBaseIds,
        List<ValidationFailure> failures)
    {
        var knowledgeBaseIds = new List<Guid>();
        if (sources is null || sources.Count == 0)
        {
            failures.Add(new ValidationFailure(SourcesField, SourcesRequiredMessage));
            return knowledgeBaseIds;
        }

        var sawInvalidSource = false;
        foreach (var source in sources)
        {
            if (!string.Equals(source.Type, "knowledge-base", StringComparison.Ordinal))
            {
                failures.Add(new ValidationFailure(SourcesField, DatabaseSourceNotAvailableMessage));
                sawInvalidSource = true;
                continue;
            }

            if (source.Id is null
                || !Guid.TryParse(source.Id, out var knowledgeBaseId)
                || !connectableKnowledgeBaseIds.Contains(knowledgeBaseId))
            {
                failures.Add(new ValidationFailure(SourcesField, SourceNotConnectableMessage));
                sawInvalidSource = true;
                continue;
            }

            if (!knowledgeBaseIds.Contains(knowledgeBaseId))
            {
                knowledgeBaseIds.Add(knowledgeBaseId);
            }
        }

        if (knowledgeBaseIds.Count == 0 && !sawInvalidSource)
        {
            failures.Add(new ValidationFailure(SourcesField, SourcesRequiredMessage));
        }

        return knowledgeBaseIds;
    }

    private static bool TryParse<TEnum>(string wire, out TEnum value)
        where TEnum : struct, Enum
    {
        if (WireNames<TEnum>.All.Contains(wire))
        {
            value = WireNames<TEnum>.Parse(wire);
            return true;
        }

        value = default;
        return false;
    }
}
