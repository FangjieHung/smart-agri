using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// An assistant's LINE channel (「LINE 官方帳號」; M5b plan §3 A and §4, table
/// <c>AssistantLineChannels</c>): at most one row per assistant, keyed by the assistant. The database
/// enforces that the assistant belongs to <see cref="OrganizationId"/> (composite foreign key) and
/// removes the row when the assistant is deleted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credentials are write-only.</b> <see cref="ChannelSecret"/> and <see cref="AccessToken"/> are
/// <see cref="ProtectedSecret"/>s (the first table to store one; purposes
/// <see cref="ChannelSecretPurpose"/> and <see cref="AccessTokenPurpose"/>): the plaintext is given to
/// <c>ISecretProtector.Protect</c> once, and no endpoint ever returns it or the ciphertext — only
/// "configured", the last four characters and when it was set.
/// </para>
/// <para>
/// <b>Changing the connection settings resets the connection test</b> (M5b plan §3 A, like the mock's
/// "saving resets the test"): when a settings save changes <see cref="OfficialAccountId"/>,
/// <see cref="ChannelId"/>, or replaces either credential, <see cref="ConnectionChecks"/> is emptied,
/// <see cref="ConnectionCheckedAt"/> and <see cref="BotUserId"/> become <see langword="null"/>, and a
/// <see cref="LineChannelState.Published"/> or <see cref="LineChannelState.Paused"/> channel goes back
/// to <see cref="LineChannelState.Draft"/> (its publication cleared, as by <see cref="Unpublish"/>):
/// the owner tests the connection again and enables it again, through the publishing gate. Changing
/// only <see cref="WelcomeMessage"/> keeps all of that.
/// </para>
/// <para>
/// <see cref="State"/> is what the owner chose. Whether the channel answers LINE users right now is
/// derived on every read (<c>SmartAgri.Application.Assistants.LineChannelServing</c>) and never
/// stored; besides the conditions shared with the website channel it needs every connection check
/// passed (<see cref="ConnectionChecksPassed"/>).
/// </para>
/// <para>
/// <see cref="Revision"/> guards the settings form against two tabs saving at once, like
/// <see cref="AssistantWebsiteChannel.Revision"/>; testing, enabling, pausing and unpublishing do not
/// change it.
/// </para>
/// </remarks>
public sealed class AssistantLineChannel : IOrganizationScoped
{
    /// <summary><see cref="ChannelSecret"/>'s <c>ISecretProtector</c> purpose.</summary>
    public const string ChannelSecretPurpose = "line.channel-secret";

    /// <summary><see cref="AccessToken"/>'s <c>ISecretProtector</c> purpose.</summary>
    public const string AccessTokenPurpose = "line.access-token";

    /// <summary><c>@</c> and at most 20 characters (the frontend's pattern).</summary>
    public const int OfficialAccountIdMaxLength = 21;

    /// <summary>Exactly 10 digits (the frontend's pattern).</summary>
    public const int ChannelIdLength = 10;

    /// <summary>Decision D: sent when someone adds the account as a friend or invites it to a group.</summary>
    public const int WelcomeMessageMaxLength = 120;

    /// <summary>A LINE user id is <c>U</c> and 32 hexadecimal digits; some room to spare.</summary>
    public const int BotUserIdMaxLength = 64;

    /// <summary>Decision D's default welcome message.</summary>
    public const string DefaultWelcomeMessage = "您好！有任何問題都可以直接問我。在群組裡請 @ 我再提問。";

    /// <summary>For EF Core materialization.</summary>
    private AssistantLineChannel()
    {
    }

    public AssistantLineChannel(
        Assistant assistant,
        string officialAccountId,
        string channelId,
        ProtectedSecret channelSecret,
        ProtectedSecret accessToken,
        string welcomeMessage,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(channelSecret);
        ArgumentNullException.ThrowIfNull(accessToken);
        AssistantId = assistant.Id;
        OrganizationId = assistant.OrganizationId;
        State = LineChannelState.Draft;
        Revision = 1;
        ChannelSecret = channelSecret;
        AccessToken = accessToken;
        Apply(officialAccountId, channelId, welcomeMessage, now);
    }

    public Guid AssistantId { get; private set; }

    /// <summary>Always the assistant's organization.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>The official account's basic id, <c>@</c> first (e.g. <c>@anxin-demo</c>).</summary>
    public string OfficialAccountId { get; private set; } = string.Empty;

    /// <summary>The Messaging API channel id: 10 digits.</summary>
    public string ChannelId { get; private set; } = string.Empty;

    /// <summary>Verifies webhook signatures (M5b #231). Write-only.</summary>
    public ProtectedSecret ChannelSecret { get; private set; } = null!;

    /// <summary>The long-lived channel access token LINE's API is called with. Write-only.</summary>
    public ProtectedSecret AccessToken { get; private set; } = null!;

    /// <summary>The bot's own LINE user id, recorded by a passing connection test (a webhook's
    /// <c>destination</c> must equal it); <see langword="null"/> until then.</summary>
    public string? BotUserId { get; private set; }

    /// <summary>Sent on <c>follow</c> and <c>join</c> (decision D).</summary>
    public string WelcomeMessage { get; private set; } = string.Empty;

    public LineChannelState State { get; private set; }

    /// <summary>When the connection was last tested; <see langword="null"/> while
    /// <see cref="ConnectionChecks"/> is empty.</summary>
    public DateTimeOffset? ConnectionCheckedAt { get; private set; }

    /// <summary>The last connection test's results (<c>jsonb</c>), in
    /// <see cref="LineConnectionCheck.All"/>'s order; empty until a test runs, and again whenever the
    /// connection settings change.</summary>
    public IReadOnlyList<LineConnectionCheck> ConnectionChecks { get; private set; } = [];

    /// <summary>When it was last enabled from <see cref="LineChannelState.Draft"/>;
    /// <see langword="null"/> while it is a draft.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Who enabled it (always the owner: only the owner may publish).</summary>
    public Guid? PublishedByAccountId { get; private set; }

    /// <summary>The month (<c>YYYY-MM</c>, <c>Statistics:TimeZone</c>) <see cref="PushFallbackCount"/>
    /// counts; <see langword="null"/> before the first push fallback.</summary>
    public string? PushFallbackMonth { get; private set; }

    /// <summary>How many answers were sent by push instead of reply in <see cref="PushFallbackMonth"/>
    /// (「補送」, M5b plan §3 D; counted from M5b Slice 4 on).</summary>
    public int PushFallbackCount { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Starts at 1 and increments by 1 on every successful <see cref="TryApplySettings"/>.</summary>
    public int Revision { get; private set; }

    /// <summary>Whether the last connection test passed every check (see
    /// <see cref="LineConnectionCheck.AllPassed"/>).</summary>
    public bool ConnectionChecksPassed => LineConnectionCheck.AllPassed([.. ConnectionChecks]);

    /// <summary>
    /// Applies a settings save: refuses (returns <see langword="false"/>, changing nothing) when
    /// <paramref name="expectedRevision"/> is not <see cref="Revision"/>. A <see langword="null"/>
    /// <paramref name="newChannelSecret"/> or <paramref name="newAccessToken"/> keeps the stored one;
    /// a value replaces it. On success <see cref="Revision"/> increments by 1, and when the official
    /// account id, the channel id or a credential changed, the connection test is reset (see the
    /// class remarks).
    /// </summary>
    /// <returns>Whether the save was applied.</returns>
    public bool TryApplySettings(
        string officialAccountId,
        string channelId,
        ProtectedSecret? newChannelSecret,
        ProtectedSecret? newAccessToken,
        string welcomeMessage,
        int expectedRevision,
        DateTimeOffset now)
    {
        if (expectedRevision != Revision)
        {
            return false;
        }

        var connectionChanged =
            !string.Equals(officialAccountId, OfficialAccountId, StringComparison.Ordinal)
            || !string.Equals(channelId, ChannelId, StringComparison.Ordinal)
            || newChannelSecret is not null
            || newAccessToken is not null;

        Apply(officialAccountId, channelId, welcomeMessage, now);
        ChannelSecret = newChannelSecret ?? ChannelSecret;
        AccessToken = newAccessToken ?? AccessToken;
        if (connectionChanged)
        {
            ResetConnection();
        }

        Revision += 1;
        return true;
    }

    /// <summary>
    /// Records a connection test's results (M5b #230), replacing the previous ones;
    /// <paramref name="botUserId"/> is the bot's user id from a passing token check (else
    /// <see langword="null"/>). A failing test of an enabled channel leaves <see cref="State"/> as it
    /// is: it simply stops serving until a test passes again.
    /// </summary>
    public void RecordConnectionChecks(IReadOnlyList<LineConnectionCheck> checks, string? botUserId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count == 0)
        {
            throw new ArgumentException("A connection test has at least one result.", nameof(checks));
        }

        if (checks.Any(check => check.State == LineConnectionCheckState.Pending))
        {
            throw new ArgumentException("A stored check has run (or was skipped); pending is shown only.", nameof(checks));
        }

        if (botUserId is { Length: > BotUserIdMaxLength })
        {
            throw new ArgumentException($"Must be at most {BotUserIdMaxLength} characters.", nameof(botUserId));
        }

        ConnectionChecks = [.. checks];
        ConnectionCheckedAt = now;
        BotUserId = botUserId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Enables it (the publishing gate is checked by the caller beforehand; every connection check
    /// must have passed). From <see cref="LineChannelState.Draft"/> this records who and when; from
    /// <see cref="LineChannelState.Paused"/> it resumes, keeping the original publication. Returns
    /// whether anything changed.
    /// </summary>
    public bool Publish(Guid accountId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account id must not be empty.", nameof(accountId));
        }

        if (!ConnectionChecksPassed)
        {
            throw new InvalidOperationException("A LINE channel whose connection test has not passed cannot be enabled.");
        }

        if (State == LineChannelState.Published)
        {
            return false;
        }

        if (State == LineChannelState.Draft)
        {
            PublishedAt = now;
            PublishedByAccountId = accountId;
        }

        State = LineChannelState.Published;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Pauses (<see cref="LineChannelState.Published"/> → <see cref="LineChannelState.Paused"/>) or
    /// resumes (the reverse). Returns whether anything changed; throws for a draft, which has nothing
    /// to pause or resume.
    /// </summary>
    public bool SetPaused(bool paused, DateTimeOffset now)
    {
        if (State == LineChannelState.Draft)
        {
            throw new InvalidOperationException("A LINE channel that is not enabled cannot be paused or resumed.");
        }

        var target = paused ? LineChannelState.Paused : LineChannelState.Published;
        if (State == target)
        {
            return false;
        }

        State = target;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Back to <see cref="LineChannelState.Draft"/>, keeping every setting, credential and
    /// connection check result. Returns whether anything changed.</summary>
    public bool Unpublish(DateTimeOffset now)
    {
        if (State == LineChannelState.Draft)
        {
            return false;
        }

        ClearPublication();
        UpdatedAt = now;
        return true;
    }

    /// <summary>Counts one answer sent by push instead of reply in <paramref name="month"/>
    /// (<c>YYYY-MM</c>); a new month starts again from 1.</summary>
    public void RecordPushFallback(string month)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(month);
        if (string.Equals(month, PushFallbackMonth, StringComparison.Ordinal))
        {
            PushFallbackCount += 1;
            return;
        }

        PushFallbackMonth = month;
        PushFallbackCount = 1;
    }

    /// <summary><see cref="PushFallbackCount"/> if it counts <paramref name="month"/>, else 0.</summary>
    public int PushFallbackCountIn(string month) =>
        string.Equals(month, PushFallbackMonth, StringComparison.Ordinal) ? PushFallbackCount : 0;

    private void ResetConnection()
    {
        ConnectionChecks = [];
        ConnectionCheckedAt = null;
        BotUserId = null;
        if (State != LineChannelState.Draft)
        {
            ClearPublication();
        }
    }

    private void ClearPublication()
    {
        State = LineChannelState.Draft;
        PublishedAt = null;
        PublishedByAccountId = null;
    }

    private void Apply(string officialAccountId, string channelId, string welcomeMessage, DateTimeOffset now)
    {
        RequireText(officialAccountId, OfficialAccountIdMaxLength, nameof(officialAccountId));
        RequireText(channelId, ChannelIdLength, nameof(channelId));
        RequireText(welcomeMessage, WelcomeMessageMaxLength, nameof(welcomeMessage));
        OfficialAccountId = officialAccountId;
        ChannelId = channelId;
        WelcomeMessage = welcomeMessage;
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
