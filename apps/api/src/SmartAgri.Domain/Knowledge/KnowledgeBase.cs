using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// A knowledge base (知識庫): a named collection of documents and FAQ entries, owned by one
/// account of one organization (M2 plan §4). Only the owner can read or change it; see
/// <c>SmartAgri.Application.Knowledge.KnowledgeBaseAccess</c>. Names do not have to be
/// unique within an organization.
/// </summary>
/// <remarks>
/// A plain POCO: EF Core mapping lives in <c>SmartAgri.Infrastructure.Knowledge</c>. The
/// guards here are invariants (a caller bug if violated); user-facing validation with
/// messages is <c>SmartAgri.Application.Knowledge.KnowledgeBaseDetailsRules</c>, which
/// always runs first.
/// </remarks>
public sealed class KnowledgeBase : IOrganizationScoped
{
    public const int NameMaxLength = 100;

    public const int PurposeMaxLength = 500;

    /// <summary>For EF Core materialization.</summary>
    private KnowledgeBase()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The account that created it. The database also enforces that the owner
    /// belongs to <see cref="OrganizationId"/> (composite foreign key).</summary>
    public Guid OwnerAccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>What the knowledge base is for (用途); may be empty.</summary>
    public string Purpose { get; private set; } = string.Empty;

    public KnowledgeSharingScope SharingScope { get; private set; }

    /// <summary>Whether viewers may download original files. Only ever true when
    /// <see cref="SharingScope"/> is <see cref="KnowledgeSharingScope.Public"/>.</summary>
    public bool AllowOriginalDownload { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The last change to this row (name, purpose or sharing).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>A new, private knowledge base owned by <paramref name="ownerAccountId"/>.</summary>
    public static KnowledgeBase Create(
        Guid organizationId,
        Guid ownerAccountId,
        string name,
        string purpose,
        DateTimeOffset now)
    {
        RequireId(organizationId, nameof(organizationId));
        RequireId(ownerAccountId, nameof(ownerAccountId));

        var knowledgeBase = new KnowledgeBase
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            OwnerAccountId = ownerAccountId,
            SharingScope = KnowledgeSharingScope.Private,
            AllowOriginalDownload = false,
            CreatedAt = now,
        };
        knowledgeBase.ChangeDetails(name, purpose, now);
        knowledgeBase.UpdatedAt = now;
        return knowledgeBase;
    }

    /// <summary>
    /// Sets <see cref="Name"/> and <see cref="Purpose"/> (both trimmed). Returns the names of
    /// the fields that actually changed (<c>"name"</c>, <c>"purpose"</c>); when none did,
    /// nothing is touched, <see cref="UpdatedAt"/> included.
    /// </summary>
    public IReadOnlyList<string> ChangeDetails(string name, string purpose, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(purpose);

        var trimmedName = name.Trim();
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"A knowledge base name must be 1-{NameMaxLength} characters.", nameof(name));
        }

        var trimmedPurpose = purpose.Trim();
        if (trimmedPurpose.Length > PurposeMaxLength)
        {
            throw new ArgumentException($"A knowledge base purpose must be at most {PurposeMaxLength} characters.", nameof(purpose));
        }

        var changed = new List<string>(2);
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

        if (changed.Count > 0)
        {
            UpdatedAt = now;
        }

        return changed;
    }

    /// <summary>
    /// Sets the sharing scope and download flag. The share targets themselves are
    /// <see cref="KnowledgeBaseShare"/> rows, replaced by the caller in the same save.
    /// </summary>
    public void ChangeSharing(KnowledgeSharingScope scope, bool allowOriginalDownload, DateTimeOffset now)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Not a declared sharing scope.");
        }

        if (allowOriginalDownload && scope != KnowledgeSharingScope.Public)
        {
            throw new ArgumentException(
                "Original files can only be downloadable when the knowledge base is shared publicly.",
                nameof(allowOriginalDownload));
        }

        SharingScope = scope;
        AllowOriginalDownload = allowOriginalDownload;
        UpdatedAt = now;
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
