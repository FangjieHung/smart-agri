using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>An assistant's settings, trimmed and within limits, ready to apply
/// (<see cref="Assistant.ApplySettings"/>).</summary>
public sealed record AssistantSettingsDetails(
    string Name,
    string Purpose,
    AssistantTone Tone,
    string RoleInstructions,
    AssistantKnowledgeScope KnowledgeScope,
    string RefusalMessage,
    bool ShowCitations,
    bool KeepConversations);

/// <summary>
/// Validation of <c>PATCH /api/v1/assistants/{id}/settings</c> (mapping §2.6): every field is
/// optional (a <see langword="null"/> one keeps its current value), but when any field is
/// invalid the whole request is refused with a <c>422</c> naming every broken field at
/// once — nothing is written, not even the fields that were valid (M3 plan Slice 1
/// acceptance).
/// </summary>
public static class AssistantSettingsRules
{
    public const string NameField = "name";

    public const string PurposeField = "purpose";

    public const string ToneField = "tone";

    public const string RoleInstructionsField = "roleInstructions";

    public const string KnowledgeScopeField = "knowledgeScope";

    public const string RefusalMessageField = "refusalMessage";

    public const string NameRequiredMessage = "請輸入助理名稱。";

    public static readonly string NameTooLongMessage = $"助理名稱最多 {Assistant.NameMaxLength} 個字。";

    public const string PurposeRequiredMessage = "請用一句話說明助理要幫忙完成的工作。";

    public static readonly string PurposeTooLongMessage = $"用途說明最多 {Assistant.PurposeMaxLength} 個字。";

    public const string ToneInvalidMessage = "語氣設定不正確。";

    public static readonly string RoleInstructionsTooLongMessage =
        $"角色設定最多 {Assistant.RoleInstructionsMaxLength} 個字。";

    public const string KnowledgeScopeInvalidMessage = "回答範圍設定不正確。";

    public const string RefusalMessageRequiredMessage = "請填寫找不到資料時的回覆內容。";

    public static readonly string RefusalMessageTooLongMessage =
        $"回覆內容最多 {Assistant.RefusalMessageMaxLength} 個字。";

    /// <summary>For a partial update: a <see langword="null"/> field keeps its
    /// <paramref name="current"/> value.</summary>
    public static ValidationResult<AssistantSettingsDetails> ForUpdate(
        AssistantSettingsDetails current,
        string? name,
        string? purpose,
        string? tone,
        string? roleInstructions,
        string? knowledgeScope,
        string? refusalMessage,
        bool? showCitations,
        bool? keepConversations)
    {
        ArgumentNullException.ThrowIfNull(current);

        var failures = new List<ValidationFailure>();

        var trimmedName = (name ?? current.Name).Trim();
        if (trimmedName.Length == 0)
        {
            failures.Add(new ValidationFailure(NameField, NameRequiredMessage));
        }
        else if (trimmedName.Length > Assistant.NameMaxLength)
        {
            failures.Add(new ValidationFailure(NameField, NameTooLongMessage));
        }

        var trimmedPurpose = (purpose ?? current.Purpose).Trim();
        if (trimmedPurpose.Length == 0)
        {
            failures.Add(new ValidationFailure(PurposeField, PurposeRequiredMessage));
        }
        else if (trimmedPurpose.Length > Assistant.PurposeMaxLength)
        {
            failures.Add(new ValidationFailure(PurposeField, PurposeTooLongMessage));
        }

        var resolvedTone = current.Tone;
        if (tone is not null && !TryParse(tone, out resolvedTone))
        {
            failures.Add(new ValidationFailure(ToneField, ToneInvalidMessage));
        }

        var trimmedInstructions = (roleInstructions ?? current.RoleInstructions).Trim();
        if (trimmedInstructions.Length > Assistant.RoleInstructionsMaxLength)
        {
            failures.Add(new ValidationFailure(RoleInstructionsField, RoleInstructionsTooLongMessage));
        }

        var resolvedScope = current.KnowledgeScope;
        if (knowledgeScope is not null && !TryParse(knowledgeScope, out resolvedScope))
        {
            failures.Add(new ValidationFailure(KnowledgeScopeField, KnowledgeScopeInvalidMessage));
        }

        var trimmedRefusal = (refusalMessage ?? current.RefusalMessage).Trim();
        if (trimmedRefusal.Length == 0)
        {
            failures.Add(new ValidationFailure(RefusalMessageField, RefusalMessageRequiredMessage));
        }
        else if (trimmedRefusal.Length > Assistant.RefusalMessageMaxLength)
        {
            failures.Add(new ValidationFailure(RefusalMessageField, RefusalMessageTooLongMessage));
        }

        if (failures.Count > 0)
        {
            return ValidationResult<AssistantSettingsDetails>.Invalid(failures);
        }

        return ValidationResult<AssistantSettingsDetails>.Valid(new AssistantSettingsDetails(
            trimmedName,
            trimmedPurpose,
            resolvedTone,
            trimmedInstructions,
            resolvedScope,
            trimmedRefusal,
            showCitations ?? current.ShowCitations,
            keepConversations ?? current.KeepConversations));
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
