using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Ai;

/// <summary>
/// The audit record of one call to a model (table <c>ModelInvocations</c>, M2 plan §4;
/// llm-providers ADR: organization, account, assistant, model, time and token usage). Written
/// for every call, successful or not, by the middleware every model client is wrapped in.
/// </summary>
/// <remarks>
/// It never holds content: no input, no output, no document or question text, not even a
/// hash of it. Whether content is also kept is an organization setting of a later milestone,
/// and would be a separate table.
/// </remarks>
public sealed class ModelInvocation : IOrganizationScoped
{
    public const int ProviderMaxLength = 64;

    public const int ModelMaxLength = 200;

    /// <summary>For EF Core materialization.</summary>
    private ModelInvocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The account the call was made for: the caller of a request, the uploader of a
    /// version being processed; <see langword="null"/> for maintenance such as <c>reindex</c>.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>The assistant the call was made for, if any (M3; always
    /// <see langword="null"/> in M2).</summary>
    public Guid? AssistantId { get; private set; }

    public ModelInvocationPurpose Purpose { get; private set; }

    /// <summary>Which configured provider served the call, e.g. <c>openai</c>, <c>azure-openai</c>,
    /// <c>openai-compatible</c>, <c>fake</c>.</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>The model (for Azure OpenAI, the deployment) that was requested.</summary>
    public string Model { get; private set; } = string.Empty;

    /// <summary>Input tokens as the provider reported them; <see langword="null"/> when it did
    /// not report usage (or the call failed).</summary>
    public long? InputTokens { get; private set; }

    public long DurationMs { get; private set; }

    public bool Succeeded { get; private set; }

    /// <summary>When the call started.</summary>
    public DateTimeOffset At { get; private set; }

    public static ModelInvocation Record(
        Guid organizationId,
        Guid? accountId,
        Guid? assistantId,
        ModelInvocationPurpose purpose,
        string provider,
        string model,
        long? inputTokens,
        long durationMs,
        bool succeeded,
        DateTimeOffset at)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A model call is always made for an organization.", nameof(organizationId));
        }

        if (accountId == Guid.Empty || assistantId == Guid.Empty)
        {
            throw new ArgumentException("Pass null, not Guid.Empty, for no account or assistant.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null);
        }

        RequireName(provider, ProviderMaxLength, nameof(provider));
        RequireName(model, ModelMaxLength, nameof(model));
        if (inputTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), inputTokens, "Token counts are never negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);

        return new ModelInvocation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AccountId = accountId,
            AssistantId = assistantId,
            Purpose = purpose,
            Provider = provider,
            Model = model,
            InputTokens = inputTokens,
            DurationMs = durationMs,
            Succeeded = succeeded,
            At = at,
        };
    }

    private static void RequireName(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"At most {maxLength} characters.", parameterName);
        }
    }
}
