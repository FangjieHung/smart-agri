using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// An assistant (助理): a configured answering agent, owned by one account of one
/// organization (M3 plan §4). Only the owner may open or change it; see
/// <c>SmartAgri.Application.Assistants.AssistantAccess</c>. Who may <b>use</b> it to chat is a
/// separate question (<c>AssistantUseAccess</c>), decided by ownership now and, from #73, also
/// by <c>AssistantShare</c>.
/// </summary>
/// <remarks>
/// A plain POCO: EF Core mapping lives in <c>SmartAgri.Infrastructure.Assistants</c>. The
/// guards here are invariants (a caller bug if violated); user-facing validation with
/// messages is <c>SmartAgri.Application.Assistants.AssistantSettingsRules</c>, which always
/// runs first. Building one from a wizard draft (<c>AssistantDraft</c>) is #72; this slice
/// only needs <see cref="Create"/> for tests and for #72 to call.
/// </remarks>
public sealed class Assistant : IOrganizationScoped
{
    public const int NameMaxLength = 100;

    public const int PurposeMaxLength = 500;

    public const int RoleInstructionsMaxLength = 2000;

    public const int RefusalMessageMaxLength = 500;

    /// <summary>For EF Core materialization.</summary>
    private Assistant()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The account that created it. The database also enforces that the owner
    /// belongs to <see cref="OrganizationId"/> (composite foreign key).</summary>
    public Guid OwnerAccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>What the assistant is for (用途).</summary>
    public string Purpose { get; private set; } = string.Empty;

    /// <summary>The wizard template it was created from, if any; purely informational (never
    /// re-applies template defaults). <see langword="null"/> for a blank template.</summary>
    public string? TemplateId { get; private set; }

    public AssistantTone Tone { get; private set; }

    /// <summary>Free-text role/persona instructions (角色設定); may be empty.</summary>
    public string RoleInstructions { get; private set; } = string.Empty;

    public AssistantKnowledgeScope KnowledgeScope { get; private set; }

    /// <summary>Shown (as <c>no-result</c>) when retrieval is below threshold and
    /// <see cref="KnowledgeScope"/> is <see cref="AssistantKnowledgeScope.CompanyDataOnly"/>.</summary>
    public string RefusalMessage { get; private set; } = string.Empty;

    /// <summary>Whether answers show clickable citations to the caller.</summary>
    public bool ShowCitations { get; private set; }

    /// <summary>Whether a using account's conversations are saved as <c>ChatThread</c>s
    /// (M3 plan §4); when false, nothing is persisted for any user of this assistant.</summary>
    public bool KeepConversations { get; private set; }

    /// <summary>The retrieval score threshold below which an answer is refused
    /// (grounded-answers ADR); <see langword="null"/> uses the deployment default
    /// (<c>Retrieval:MinScore</c>, 0.3 as of M3 plan §7).</summary>
    public double? MinScore { get; private set; }

    public AssistantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The last change to this row (settings only; a source connection or
    /// disconnection does not touch it).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A new, <see cref="AssistantStatus.Ready"/> assistant owned by
    /// <paramref name="ownerAccountId"/>.</summary>
    public static Assistant Create(
        Guid organizationId,
        Guid ownerAccountId,
        string name,
        string purpose,
        string? templateId,
        AssistantTone tone,
        string roleInstructions,
        AssistantKnowledgeScope knowledgeScope,
        string refusalMessage,
        bool showCitations,
        bool keepConversations,
        DateTimeOffset now)
    {
        RequireId(organizationId, nameof(organizationId));
        RequireId(ownerAccountId, nameof(ownerAccountId));

        var assistant = new Assistant
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            OwnerAccountId = ownerAccountId,
            TemplateId = templateId,
            Status = AssistantStatus.Ready,
            CreatedAt = now,
        };
        assistant.ApplySettings(
            name, purpose, tone, roleInstructions, knowledgeScope, refusalMessage, showCitations, keepConversations, now);
        assistant.UpdatedAt = now;
        return assistant;
    }

    /// <summary>
    /// Sets every settings field at once (all validated and trimmed by the caller — see
    /// <c>AssistantSettingsRules</c>). Returns the names of the fields that actually changed;
    /// when none did, nothing is touched, <see cref="UpdatedAt"/> included.
    /// </summary>
    public IReadOnlyList<string> ApplySettings(
        string name,
        string purpose,
        AssistantTone tone,
        string roleInstructions,
        AssistantKnowledgeScope knowledgeScope,
        string refusalMessage,
        bool showCitations,
        bool keepConversations,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(roleInstructions);
        ArgumentNullException.ThrowIfNull(refusalMessage);

        var trimmedName = name.Trim();
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"An assistant name must be 1-{NameMaxLength} characters.", nameof(name));
        }

        var trimmedPurpose = purpose.Trim();
        if (trimmedPurpose.Length is 0 or > PurposeMaxLength)
        {
            throw new ArgumentException($"An assistant purpose must be 1-{PurposeMaxLength} characters.", nameof(purpose));
        }

        var trimmedInstructions = roleInstructions.Trim();
        if (trimmedInstructions.Length > RoleInstructionsMaxLength)
        {
            throw new ArgumentException(
                $"Role instructions must be at most {RoleInstructionsMaxLength} characters.", nameof(roleInstructions));
        }

        var trimmedRefusal = refusalMessage.Trim();
        if (trimmedRefusal.Length is 0 or > RefusalMessageMaxLength)
        {
            throw new ArgumentException($"A refusal message must be 1-{RefusalMessageMaxLength} characters.", nameof(refusalMessage));
        }

        if (!Enum.IsDefined(tone))
        {
            throw new ArgumentOutOfRangeException(nameof(tone), tone, "Not a declared tone.");
        }

        if (!Enum.IsDefined(knowledgeScope))
        {
            throw new ArgumentOutOfRangeException(nameof(knowledgeScope), knowledgeScope, "Not a declared knowledge scope.");
        }

        var changed = new List<string>(8);

        if (!string.Equals(Name, trimmedName, StringComparison.Ordinal))
        {
            Name = trimmedName;
            changed.Add("name");
        }

        if (!string.Equals(Purpose, trimmedPurpose, StringComparison.Ordinal))
        {
            Purpose = trimmedPurpose;
            changed.Add("purpose");
        }

        if (Tone != tone)
        {
            Tone = tone;
            changed.Add("tone");
        }

        if (!string.Equals(RoleInstructions, trimmedInstructions, StringComparison.Ordinal))
        {
            RoleInstructions = trimmedInstructions;
            changed.Add("roleInstructions");
        }

        if (KnowledgeScope != knowledgeScope)
        {
            KnowledgeScope = knowledgeScope;
            changed.Add("knowledgeScope");
        }

        if (!string.Equals(RefusalMessage, trimmedRefusal, StringComparison.Ordinal))
        {
            RefusalMessage = trimmedRefusal;
            changed.Add("refusalMessage");
        }

        if (ShowCitations != showCitations)
        {
            ShowCitations = showCitations;
            changed.Add("showCitations");
        }

        if (KeepConversations != keepConversations)
        {
            KeepConversations = keepConversations;
            changed.Add("keepConversations");
        }

        if (changed.Count > 0)
        {
            UpdatedAt = now;
        }

        return changed;
    }

    /// <summary>
    /// Pauses or resumes the assistant (<c>PUT .../publishing/platform/paused</c>, M3 plan §5
    /// Slice 3): while <see cref="AssistantStatus.Paused"/>, only the owner may use it
    /// (<c>AssistantUseAccess.UsableBy</c>). Returns whether anything changed; an unchanged
    /// request touches neither <see cref="Status"/> nor <see cref="UpdatedAt"/>.
    /// </summary>
    public bool SetStatus(AssistantStatus status, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Not a declared status.");
        }

        if (Status == status)
        {
            return false;
        }

        Status = status;
        UpdatedAt = now;
        return true;
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
