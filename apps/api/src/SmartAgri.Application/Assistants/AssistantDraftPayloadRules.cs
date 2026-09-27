using System.Text;
using System.Text.Json;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// Validation shared by <c>POST</c> and <c>PUT /api/v1/assistant-drafts</c> (M3 plan §3):
/// the payload must be a JSON object within <see cref="AssistantDraft.PayloadMaxBytes"/>.
/// Nothing about its fields is checked here — the wizard's shape belongs to the frontend;
/// only "由草稿建立助理" (<see cref="AssistantDraftCreationRules"/>) looks inside it.
/// </summary>
public static class AssistantDraftPayloadRules
{
    public const string PayloadField = "payload";

    public const string PayloadNotAnObjectMessage = "草稿內容格式不正確，請重新編輯後再試一次。";

    public static readonly string PayloadTooLargeMessage =
        $"草稿內容過大，最多 {AssistantDraft.PayloadMaxBytes / 1024} KB。";

    /// <summary>The canonical JSON text to store, or the failure to refuse the request with.</summary>
    public static ValidationResult<string> ForSave(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return ValidationResult<string>.Invalid(PayloadField, PayloadNotAnObjectMessage);
        }

        var raw = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > AssistantDraft.PayloadMaxBytes)
        {
            return ValidationResult<string>.Invalid(PayloadField, PayloadTooLargeMessage);
        }

        return ValidationResult<string>.Valid(raw);
    }
}
