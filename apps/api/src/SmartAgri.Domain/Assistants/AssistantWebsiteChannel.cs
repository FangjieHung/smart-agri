using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// An assistant's website embedding channel (「官網嵌入」; M5a plan §4, table
/// <c>AssistantWebsiteChannels</c>): at most one row per assistant, keyed by the assistant. The
/// database enforces that the assistant belongs to <see cref="OrganizationId"/> (composite foreign
/// key) and removes the row, with its <see cref="AssistantWebsiteDomain"/>s, when the assistant is
/// deleted.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="State"/> is what the owner chose (draft / published / paused). Whether the channel
/// actually answers visitors right now is derived on every read
/// (<c>SmartAgri.Application.Assistants.WebsiteChannelServing.Evaluate</c>) and never stored: it
/// follows the acceptance status, the knowledge bases' ownership and the organization's quota as
/// they change, without the owner publishing again (M5a plan §3 C).
/// </para>
/// <para>
/// <see cref="Revision"/> guards the settings form (display name, welcome message, colour,
/// position and the allowed domains together) against two tabs saving at once, like
/// <see cref="AssistantDraft.Revision"/>; publishing, pausing and unpublishing do not change it,
/// so a settings tab left open is not refused because someone pressed 「發布」.
/// </para>
/// <para>
/// Publishing is not an <see cref="AssistantStatus"/>: an assistant may be shared within the
/// platform and on a website at the same time (M5a plan §4).
/// </para>
/// </remarks>
public sealed class AssistantWebsiteChannel : IOrganizationScoped
{
    /// <summary>Same as the frontend's <c>MAX_WEBSITE_NAME_LENGTH</c>.</summary>
    public const int DisplayNameMaxLength = 30;

    /// <summary>Same as the frontend's <c>MAX_WELCOME_LENGTH</c>.</summary>
    public const int WelcomeMessageMaxLength = 120;

    /// <summary>Same as the frontend's <c>MAX_ALLOWED_DOMAINS</c>; checked by the Application rule
    /// before any write.</summary>
    public const int MaxAllowedDomains = 5;

    /// <summary>For EF Core materialization.</summary>
    private AssistantWebsiteChannel()
    {
    }

    public AssistantWebsiteChannel(
        Assistant assistant,
        string displayName,
        string welcomeMessage,
        WebsiteBrandColor brandColor,
        WebsiteLauncherPosition position,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        AssistantId = assistant.Id;
        OrganizationId = assistant.OrganizationId;
        State = WebsiteChannelState.Draft;
        Revision = 1;
        Apply(displayName, welcomeMessage, brandColor, position, now);
    }

    public Guid AssistantId { get; private set; }

    /// <summary>Always the assistant's organization.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>The name shown in the visitor's chat window header.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    public string WelcomeMessage { get; private set; } = string.Empty;

    public WebsiteBrandColor BrandColor { get; private set; }

    public WebsiteLauncherPosition Position { get; private set; }

    public WebsiteChannelState State { get; private set; }

    /// <summary>When it was last published from <see cref="WebsiteChannelState.Draft"/>;
    /// <see langword="null"/> while it is a draft.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Who published it (always the owner: only the owner may publish).</summary>
    public Guid? PublishedByAccountId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Starts at 1 and increments by 1 on every successful <see cref="TryApplySettings"/>.</summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Applies a settings save: refuses (returns <see langword="false"/>, changing nothing) when
    /// <paramref name="expectedRevision"/> is not <see cref="Revision"/>. On success
    /// <see cref="Revision"/> increments by 1, even when the values are unchanged (the domains,
    /// saved alongside, may have changed).
    /// </summary>
    public bool TryApplySettings(
        string displayName,
        string welcomeMessage,
        WebsiteBrandColor brandColor,
        WebsiteLauncherPosition position,
        int expectedRevision,
        DateTimeOffset now)
    {
        if (expectedRevision != Revision)
        {
            return false;
        }

        Apply(displayName, welcomeMessage, brandColor, position, now);
        Revision += 1;
        return true;
    }

    /// <summary>
    /// Publishes it (the gate is checked by the caller beforehand). From
    /// <see cref="WebsiteChannelState.Draft"/> this records who and when; from
    /// <see cref="WebsiteChannelState.Paused"/> it resumes, keeping the original publication.
    /// Returns whether anything changed.
    /// </summary>
    public bool Publish(Guid accountId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account id must not be empty.", nameof(accountId));
        }

        if (State == WebsiteChannelState.Published)
        {
            return false;
        }

        if (State == WebsiteChannelState.Draft)
        {
            PublishedAt = now;
            PublishedByAccountId = accountId;
        }

        State = WebsiteChannelState.Published;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Pauses (<see cref="WebsiteChannelState.Published"/> → <see cref="WebsiteChannelState.Paused"/>)
    /// or resumes (the reverse). Returns whether anything changed; throws for a draft, which has
    /// nothing to pause or resume.
    /// </summary>
    public bool SetPaused(bool paused, DateTimeOffset now)
    {
        if (State == WebsiteChannelState.Draft)
        {
            throw new InvalidOperationException("A website channel that is not published cannot be paused or resumed.");
        }

        var target = paused ? WebsiteChannelState.Paused : WebsiteChannelState.Published;
        if (State == target)
        {
            return false;
        }

        State = target;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Back to <see cref="WebsiteChannelState.Draft"/>, keeping every setting and
    /// domain. Returns whether anything changed.</summary>
    public bool Unpublish(DateTimeOffset now)
    {
        if (State == WebsiteChannelState.Draft)
        {
            return false;
        }

        State = WebsiteChannelState.Draft;
        PublishedAt = null;
        PublishedByAccountId = null;
        UpdatedAt = now;
        return true;
    }

    private void Apply(
        string displayName,
        string welcomeMessage,
        WebsiteBrandColor brandColor,
        WebsiteLauncherPosition position,
        DateTimeOffset now)
    {
        RequireText(displayName, DisplayNameMaxLength, nameof(displayName));
        RequireText(welcomeMessage, WelcomeMessageMaxLength, nameof(welcomeMessage));
        if (!Enum.IsDefined(brandColor))
        {
            throw new ArgumentOutOfRangeException(nameof(brandColor), brandColor, "Not a declared colour.");
        }

        if (!Enum.IsDefined(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "Not a declared position.");
        }

        DisplayName = displayName;
        WelcomeMessage = welcomeMessage;
        BrandColor = brandColor;
        Position = position;
        UpdatedAt = now;
    }

    private static void RequireText(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Must be at most {maxLength} characters.", parameterName);
        }
    }
}
