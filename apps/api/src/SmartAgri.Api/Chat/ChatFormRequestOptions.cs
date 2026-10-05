using Microsoft.Extensions.Options;

namespace SmartAgri.Api.Chat;

/// <summary>How a conversation turn becomes a form request (M4 #164).</summary>
public enum ChatFormRequestTrigger
{
    /// <summary>The server's keyword gate decides, with no model call (#148's behavior; the default).</summary>
    Keyword,

    /// <summary>The model decides whether to call <c>request_database_form</c> among the forms the
    /// server offers; the keyword gate is the fallback when the model call fails.</summary>
    Model,
}

/// <summary>
/// Configuration section <c>Chat:FormRequests</c> (M4 #164): <see cref="Trigger"/> is
/// <c>Keyword</c> (default) or <c>Model</c>, case-insensitive; anything else fails startup.
/// </summary>
public sealed class ChatFormRequestOptions
{
    public const string SectionName = "Chat:FormRequests";

    /// <summary><c>Keyword</c> or <c>Model</c>; empty means <c>Keyword</c>.</summary>
    public string? Trigger { get; set; }

    /// <summary>The parsed <see cref="Trigger"/>; <see langword="null"/> when it is not a known name.</summary>
    public ChatFormRequestTrigger? TriggerKind =>
        string.IsNullOrWhiteSpace(Trigger) ? ChatFormRequestTrigger.Keyword
        : Enum.TryParse<ChatFormRequestTrigger>(Trigger.Trim(), ignoreCase: true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(Trigger, out _) ? kind
        : null;

    internal sealed class Validator : IValidateOptions<ChatFormRequestOptions>
    {
        public ValidateOptionsResult Validate(string? name, ChatFormRequestOptions options) =>
            options.TriggerKind is null
                ? ValidateOptionsResult.Fail($"{SectionName}:{nameof(Trigger)} must be Keyword or Model, not '{options.Trigger}'.")
                : ValidateOptionsResult.Success;
    }
}
