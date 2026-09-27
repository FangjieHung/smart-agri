using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One in-progress wizard draft (M3 plan §3 "精靈草稿存成 jsonb", §4). An account may keep
/// several named drafts at once (assistant-workspace-model ADR): each is identified by its
/// own <see cref="Id"/>, not by the owner alone.
/// </summary>
/// <remarks>
/// <para>
/// The wizard's shape is entirely a frontend concern and changes with the screen, so this
/// entity does not model it: <see cref="Payload"/> is the frontend's <c>AssistantDraft</c>
/// object, stored verbatim as <c>jsonb</c> (<see cref="SmartAgri.Infrastructure.Assistants.AssistantDraftConfiguration"/>),
/// tagged with a <see cref="SchemaVersion"/>. Only "由草稿建立助理" (ticket #72) parses it, and
/// only far enough to validate the handful of fields that become an <see cref="Assistant"/>
/// row (<c>SmartAgri.Application.Assistants.AssistantDraftCreationRules</c>).
/// </para>
/// <para>
/// <see cref="Revision"/> detects two tabs saving the same draft (M3 plan Slice 2
/// acceptance): a <c>PUT</c> must send the revision it last read, and a mismatch is refused
/// with <c>409</c> — nothing about who is "right", just "reload and retry".
/// </para>
/// </remarks>
public sealed class AssistantDraft : IOrganizationScoped
{
    /// <summary>The stored JSON text must not exceed this many UTF-8 bytes (M3 plan §3:
    /// "只驗證是合法 JSON object 且大小有上限，例如 64 KB").</summary>
    public const int PayloadMaxBytes = 64 * 1024;

    /// <summary>For EF Core materialization.</summary>
    private AssistantDraft()
    {
    }

    public AssistantDraft(Guid organizationId, Guid ownerAccountId, string payload, int schemaVersion, DateTimeOffset now)
    {
        RequireId(organizationId, nameof(organizationId));
        RequireId(ownerAccountId, nameof(ownerAccountId));
        RequireValidPayload(payload);

        Id = Guid.CreateVersion7();
        OrganizationId = organizationId;
        OwnerAccountId = ownerAccountId;
        Payload = payload;
        SchemaVersion = schemaVersion;
        Revision = 1;
        SavedAt = now;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The account this draft belongs to; only it may read, change or delete this
    /// row (<c>SmartAgri.Application.Assistants.AssistantDraftAccess</c>).</summary>
    public Guid OwnerAccountId { get; private set; }

    /// <summary>The frontend's <c>AssistantDraft</c> object, as JSON text. Never parsed by
    /// this type; callers that need its fields go through
    /// <c>AssistantDraftCreationRules</c>.</summary>
    public string Payload { get; private set; } = "{}";

    /// <summary>Tags which shape <see cref="Payload"/> follows, so a future frontend change
    /// can tell old drafts apart without guessing from the JSON alone.</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>Starts at 1 and increments by 1 on every successful <see cref="TrySave"/>.</summary>
    public int Revision { get; private set; }

    /// <summary>When <see cref="Payload"/> was last written (creation counts).</summary>
    public DateTimeOffset SavedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Applies a <c>PUT</c>: refuses (returns <see langword="false"/>, writing nothing) when
    /// <paramref name="expectedRevision"/> does not match <see cref="Revision"/> — the
    /// caller turns that into a <c>409</c>. On success, <see cref="Revision"/> increments by
    /// 1 and <see cref="SavedAt"/> becomes <paramref name="now"/>.
    /// </summary>
    public bool TrySave(string payload, int schemaVersion, int expectedRevision, DateTimeOffset now)
    {
        RequireValidPayload(payload);
        if (expectedRevision != Revision)
        {
            return false;
        }

        Payload = payload;
        SchemaVersion = schemaVersion;
        Revision += 1;
        SavedAt = now;
        return true;
    }

    private static void RequireValidPayload(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > PayloadMaxBytes)
        {
            throw new ArgumentException($"A draft payload must be at most {PayloadMaxBytes} bytes.", nameof(payload));
        }
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
