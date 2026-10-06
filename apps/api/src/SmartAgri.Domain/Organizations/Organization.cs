namespace SmartAgri.Domain.Organizations;

/// <summary>
/// A company or group using the platform (glossary: 組織). Not itself
/// <see cref="IOrganizationScoped"/>: it is the tenant boundary.
/// </summary>
public sealed class Organization
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 32;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    public Organization(Guid id, string name, string code)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An organization needs a non-empty id.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException($"An organization name must be 1-{NameMaxLength} characters.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        Code = NormalizeCode(code);
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// Globally unique short code used at login to tell apart same-named accounts in
    /// different organizations (glossary: 組織代碼). Always stored normalized (see
    /// <see cref="NormalizeCode"/>), so the database's unique index is effectively
    /// case-insensitive. Immutable after creation (authentication ADR): it is embedded in
    /// every account's internal Identity user name.
    /// </summary>
    public string Code { get; private set; }

    /// <summary>
    /// When this organization's team permissions (<c>PUT
    /// /api/v1/team/members/{id}/permissions</c>) were last successfully changed, or
    /// <see langword="null"/> before the first change. Whole-organization, not per member —
    /// mirrors the mock's single <c>TeamView.savedAt</c> (<c>mock-demo-repository.ts</c>'s
    /// <c>StoredTeamPermissions.savedAt</c>), which is one timestamp for the whole team, not
    /// one per row. A dedicated column rather than reusing account or permission-grant rows:
    /// those describe "what is granted now", and seeding also writes them, so their own
    /// timestamps would not stay null the way the mock's does before any team-panel edit.
    /// </summary>
    public DateTimeOffset? TeamPermissionsSavedAt { get; private set; }

    /// <summary>Records that the team's permissions were just saved (called from
    /// <c>SmartAgri.Api.Team.TeamEndpoints</c>).</summary>
    public void RecordTeamPermissionsSaved(DateTimeOffset when) => TeamPermissionsSavedAt = when;

    /// <summary>
    /// The most conversation-model tokens (input + output) the organization may use in a calendar
    /// month (M5a plan §3 F), or <see langword="null"/> for the deployment default
    /// (<c>PublicChannels:DefaultMonthlyTokenLimit</c>). <c>0</c> is allowed and means "none":
    /// the website channel is suspended for the whole month. Set by operators only
    /// (<c>set-token-limit</c>); no endpoint changes it.
    /// </summary>
    public long? MonthlyTokenLimit { get; private set; }

    /// <summary>Sets <see cref="MonthlyTokenLimit"/>; <see langword="null"/> goes back to the
    /// deployment default.</summary>
    public void SetMonthlyTokenLimit(long? limit)
    {
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A token limit is never negative.");
        }

        MonthlyTokenLimit = limit;
    }

    /// <summary>Maximum length of <see cref="ChatModelId"/>.</summary>
    public const int ChatModelIdMaxLength = 64;

    /// <summary>
    /// The id of the chat model the organization chose from the deployment's list
    /// (<c>Ai:Chat</c> and <c>Ai:Chat:Models</c>, M6 plan §3 A–D), or <see langword="null"/> for the
    /// deployment default. No foreign key: the list is deployment configuration, so an id the
    /// operator later removes stays here and the organization falls back to the default
    /// (<c>source: removed</c>) until a manager chooses again.
    /// </summary>
    public string? ChatModelId { get; private set; }

    /// <summary>
    /// Bumped by every change made through the organization settings APIs (the chat model and the
    /// conversation retention) and when a pending retention takes effect. A settings <c>PUT</c> sends
    /// the revision it read; a different one is a <c>409</c>, so two tabs cannot overwrite each
    /// other. Starts at <c>0</c>.
    /// </summary>
    public int SettingsRevision { get; private set; }

    /// <summary>
    /// How many days conversations are kept (M6 plan §3 F; one of
    /// <see cref="OrganizationRetention.Options"/>), or <see langword="null"/> for 「永久」, the
    /// default. A thread whose last message is older than the cutoff is deleted whole by the daily
    /// cleanup, and so is every <c>AnswerOutcome</c> older than it (decision B).
    /// </summary>
    public int? RetentionDays { get; private set; }

    /// <summary>
    /// A shorter <see cref="RetentionDays"/> a manager chose, waiting out
    /// <see cref="OrganizationRetention.BufferPeriod"/> until <see cref="PendingRetentionEffectiveAt"/>;
    /// <see langword="null"/> when nothing is pending. Nothing is deleted under it before then.
    /// </summary>
    public int? PendingRetentionDays { get; private set; }

    /// <summary>When <see cref="PendingRetentionDays"/> becomes <see cref="RetentionDays"/> (the first
    /// cleanup at or after it makes the switch); set exactly when a value is pending.</summary>
    public DateTimeOffset? PendingRetentionEffectiveAt { get; private set; }

    /// <summary>
    /// The <c>runAt</c> of the organization's next daily cleanup job, or <see langword="null"/>
    /// while there is no cleanup chain (retention forever, nothing pending). Only ever moved with a
    /// compare-and-set (<c>UPDATE … WHERE "RetentionCleanupNextRunAt" = @seen</c>, in the Api), so a
    /// job delivered twice runs once and queues one next job.
    /// </summary>
    public DateTimeOffset? RetentionCleanupNextRunAt { get; private set; }

    /// <summary>
    /// Changes the conversation retention when <paramref name="expectedRevision"/> is
    /// <see cref="SettingsRevision"/> (M6 plan §3 F). <paramref name="days"/> is one of
    /// <see cref="OrganizationRetention.Options"/> or <see langword="null"/> (forever); the caller has
    /// checked it.
    /// <list type="bullet">
    /// <item><b>Shorter</b> than the current retention (forever to a number included): pending until
    /// <paramref name="now"/> + <see cref="OrganizationRetention.BufferPeriod"/>; the current value
    /// keeps applying meanwhile. Another shorter value restarts the buffer.</item>
    /// <item><b>Longer</b> (a number to forever included): applies at once and drops any pending
    /// value.</item>
    /// <item><b>The current value</b> while something is pending: drops it (「改回」).</item>
    /// </list>
    /// The current value with nothing pending, or the pending value again, is
    /// <see cref="OrganizationSettingsChange.Unchanged"/> (the buffer does not restart).
    /// </summary>
    public OrganizationSettingsChange ChangeRetention(int? days, int expectedRevision, DateTimeOffset now)
    {
        if (days is { } value && !OrganizationRetention.IsOffered(value))
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Not one of the offered retention periods.");
        }

        if (expectedRevision != SettingsRevision)
        {
            return OrganizationSettingsChange.RevisionConflict;
        }

        if (days == RetentionDays)
        {
            if (PendingRetentionDays is null)
            {
                return OrganizationSettingsChange.Unchanged;
            }

            ClearPendingRetention();
        }
        else if (PendingRetentionDays is not null && days == PendingRetentionDays)
        {
            return OrganizationSettingsChange.Unchanged;
        }
        else if (OrganizationRetention.IsShorter(days, RetentionDays))
        {
            PendingRetentionDays = days;
            PendingRetentionEffectiveAt = now + OrganizationRetention.BufferPeriod;
        }
        else
        {
            RetentionDays = days;
            ClearPendingRetention();
        }

        SettingsRevision += 1;
        return OrganizationSettingsChange.Changed;
    }

    /// <summary>
    /// Makes a pending retention whose buffer is over (<see cref="PendingRetentionEffectiveAt"/> at or
    /// before <paramref name="asOf"/>) the current one and bumps <see cref="SettingsRevision"/>.
    /// Returns the retention before the switch, or <see langword="null"/> (and changes nothing) when
    /// nothing is due.
    /// </summary>
    public RetentionSwitch? ApplyDueRetention(DateTimeOffset asOf)
    {
        if (PendingRetentionDays is not { } pending || PendingRetentionEffectiveAt is not { } effectiveAt || effectiveAt > asOf)
        {
            return null;
        }

        var from = RetentionDays;
        RetentionDays = pending;
        ClearPendingRetention();
        SettingsRevision += 1;
        return new RetentionSwitch(from, pending);
    }

    /// <summary>Whether the daily cleanup has anything to do: a retention in days, current or pending.</summary>
    public bool HasRetentionLimit => RetentionDays is not null || PendingRetentionDays is not null;

    private void ClearPendingRetention()
    {
        PendingRetentionDays = null;
        PendingRetentionEffectiveAt = null;
    }

    /// <summary>
    /// Sets <see cref="ChatModelId"/> (<see langword="null"/> or blank: the deployment default) when
    /// <paramref name="expectedRevision"/> is <see cref="SettingsRevision"/>. The caller has already
    /// checked the id against the deployment's list and passes its canonical spelling. The same
    /// value again is <see cref="OrganizationSettingsChange.Unchanged"/>: nothing changes, not even
    /// the revision.
    /// </summary>
    public OrganizationSettingsChange ChangeChatModel(string? chatModelId, int expectedRevision)
    {
        var normalized = string.IsNullOrWhiteSpace(chatModelId) ? null : chatModelId.Trim();
        if (normalized is { Length: > ChatModelIdMaxLength })
        {
            throw new ArgumentException($"A chat model id is at most {ChatModelIdMaxLength} characters.", nameof(chatModelId));
        }

        if (expectedRevision != SettingsRevision)
        {
            return OrganizationSettingsChange.RevisionConflict;
        }

        if (string.Equals(normalized, ChatModelId, StringComparison.Ordinal))
        {
            return OrganizationSettingsChange.Unchanged;
        }

        ChatModelId = normalized;
        SettingsRevision += 1;
        return OrganizationSettingsChange.Changed;
    }

    /// <summary>
    /// Trims and lower-cases a code, and rejects anything outside
    /// <c>[a-z0-9-]</c>. In particular <c>/</c> is never allowed: account user names are
    /// stored as <c>{code}/{loginName}</c>, and a <c>/</c> inside the code would make two
    /// different (code, login name) pairs collide on the same user name.
    /// </summary>
    public static string NormalizeCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > CodeMaxLength)
        {
            throw new ArgumentException($"An organization code must be 1-{CodeMaxLength} characters.", nameof(code));
        }

        foreach (var character in normalized)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                throw new ArgumentException(
                    "An organization code may only contain letters a-z, digits and '-'.",
                    nameof(code));
            }
        }

        return normalized;
    }
}
