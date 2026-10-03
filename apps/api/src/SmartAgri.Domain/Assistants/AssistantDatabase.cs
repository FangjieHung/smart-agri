using SmartAgri.Domain.Databases;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One database connected to one assistant (table <c>AssistantDatabases</c>, M4 #148) — the
/// database counterpart of <see cref="AssistantKnowledgeBase"/>. The database enforces that the
/// assistant and the database both belong to <see cref="OrganizationId"/> (two composite foreign
/// keys) and removes the row when either side is deleted (cascade).
/// </summary>
/// <remarks>
/// <para>
/// A row is necessary but not sufficient for the assistant to use the database: every form
/// request and every submission re-checks, for <b>that</b> request, that the assistant's owner may
/// still use the database (<c>AssistantDatabaseAccess.ConnectableBy</c>). A revoked designation or
/// permission therefore takes effect immediately, even though the row stays (the owner sees it as
/// "needs attention" and may disconnect it).
/// </para>
/// <para>
/// <see cref="CollectsForms"/> marks the one connected database the assistant may ask members to
/// fill in (the frontend's <c>rules.dataWriteDatabaseId</c>); <see cref="CollectionPurpose"/> is
/// what members are told before they consent (<c>rules.dataWritePurpose</c>). At most one row per
/// assistant collects forms (partial unique index), and a collecting row always has a purpose
/// (check constraint). Disconnecting the row drops the form target with it.
/// </para>
/// </remarks>
public sealed class AssistantDatabase : IOrganizationScoped
{
    /// <summary>The longest collection purpose, like a database's own purpose.</summary>
    public const int CollectionPurposeMaxLength = Database.PurposeMaxLength;

    /// <summary>For EF Core materialization.</summary>
    private AssistantDatabase()
    {
    }

    public AssistantDatabase(Assistant assistant, Database database, DateTimeOffset connectedAt)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(database);
        if (assistant.OrganizationId != database.OrganizationId)
        {
            throw new ArgumentException(
                "An assistant may only connect to a database of its own organization.", nameof(database));
        }

        AssistantId = assistant.Id;
        DatabaseId = database.Id;
        OrganizationId = assistant.OrganizationId;
        ConnectedAt = connectedAt;
    }

    public Guid AssistantId { get; private set; }

    public Guid DatabaseId { get; private set; }

    public DateTimeOffset ConnectedAt { get; private set; }

    /// <summary>Whether this is the database the assistant's form requests fill in.</summary>
    public bool CollectsForms { get; private set; }

    /// <summary>Shown to members before they consent; empty unless <see cref="CollectsForms"/>.</summary>
    public string CollectionPurpose { get; private set; } = string.Empty;

    /// <summary>Always the assistant's (and the database's) organization.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Makes this the form target with <paramref name="purpose"/> (already validated:
    /// non-blank, at most <see cref="CollectionPurposeMaxLength"/> characters).</summary>
    public void CollectForms(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        var trimmed = purpose.Trim();
        if (trimmed.Length is 0 or > CollectionPurposeMaxLength)
        {
            throw new ArgumentException(
                $"A collection purpose must be 1-{CollectionPurposeMaxLength} characters.", nameof(purpose));
        }

        CollectsForms = true;
        CollectionPurpose = trimmed;
    }

    /// <summary>No longer the form target; the purpose is cleared with it.</summary>
    public void StopCollectingForms()
    {
        CollectsForms = false;
        CollectionPurpose = string.Empty;
    }
}
