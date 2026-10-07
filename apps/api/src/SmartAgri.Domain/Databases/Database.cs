using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Databases;

/// <summary>
/// A database (數據庫, <c>docs/glossary.md</c>): collects structured records that members
/// explicitly consent to submit, through a versioned form (<see cref="DatabaseFormVersion"/>),
/// owned by one account of one organization. Created from a template (#142); the owner edits
/// the form (#143) and names data managers (#144); submissions, receipts, withdrawal and fixed
/// queries come in #145–#147 as their own tables pointing here.
/// </summary>
/// <remarks>
/// <para>
/// Owning a database lets the owner manage its settings, not read what was submitted: reading
/// records needs the data-manager designation <b>and</b> the account permission
/// <c>read-consented-submissions</c> (frontend <c>canReadConsentedRecords</c>; #144). Names do
/// not have to be unique within an organization.
/// </para>
/// <para>
/// A plain POCO: EF Core mapping lives in <c>SmartAgri.Infrastructure.Databases</c>. The guards
/// here are invariants; user-facing validation with messages is
/// <c>SmartAgri.Application.Databases.DatabaseCreationRules</c>, which always runs first.
/// </para>
/// </remarks>
public sealed class Database : IOrganizationScoped
{
    /// <summary>Same limit as the mock and the create dialog's <c>maxlength</c>.</summary>
    public const int NameMaxLength = 40;

    public const int PurposeMaxLength = 500;

    /// <summary>For EF Core materialization.</summary>
    private Database()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The account that created it. The database also enforces that the owner belongs
    /// to <see cref="OrganizationId"/> (composite foreign key).</summary>
    public Guid OwnerAccountId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>What is collected and why (用途); shown to members before they consent (#145).
    /// Defaults to the template's description.</summary>
    public string Purpose { get; private set; } = string.Empty;

    /// <summary>The template it was created from; kept for display only — the form itself is
    /// copied into version 1 and never follows later template changes.</summary>
    public DatabaseTemplateId TemplateId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The last change to this row (name or purpose). A new form version is a change
    /// of its own (<see cref="DatabaseFormVersion.CreatedAt"/>).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// When the owner archived it (封存, #180); <see langword="null"/> while it is in use. Archiving
    /// is the only way a database goes away (there is no delete): nothing is removed, but it takes no
    /// new submissions — neither through its form link nor through an assistant — and is left out of
    /// the default list. Its records, withdrawals and members' own submissions stay as they were.
    /// Unarchiving clears it. Set only by the archive endpoints' conditional updates
    /// (<c>DatabaseEndpoints</c>), so archiving twice keeps the first time.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>
    /// The case type a submission to this database opens a case of (送出後自動開案, M7 plan §3 I,
    /// decision M; issue #255); <see langword="null"/> when submissions open no case. Set only by the
    /// organization's manager, and only to an active type: a type some database uses cannot be
    /// deactivated (<c>422 case-type-in-use</c>). A same-organization composite foreign key to the
    /// case type, <c>Restrict</c>.
    /// </summary>
    public Guid? AutoCaseTypeId { get; private set; }

    /// <summary>Points the auto case at <paramref name="caseTypeId"/> (or turns it off with
    /// <see langword="null"/>); <see langword="false"/> when it already was. The caller has checked
    /// that the type is active and the caller is the manager. It is not a change of the name or the
    /// purpose, so <see cref="UpdatedAt"/> stays: the change is recorded in the organization's
    /// activity instead.</summary>
    public bool SetAutoCaseType(Guid? caseTypeId)
    {
        if (caseTypeId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(caseTypeId));
        }

        if (AutoCaseTypeId == caseTypeId)
        {
            return false;
        }

        AutoCaseTypeId = caseTypeId;
        return true;
    }

    public static Database Create(
        Guid organizationId,
        Guid ownerAccountId,
        string name,
        string purpose,
        DatabaseTemplateId templateId,
        DateTimeOffset now)
    {
        RequireId(organizationId, nameof(organizationId));
        RequireId(ownerAccountId, nameof(ownerAccountId));
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(purpose);
        if (!Enum.IsDefined(templateId))
        {
            throw new ArgumentOutOfRangeException(nameof(templateId), templateId, "Not a declared template.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException($"A database name must be 1-{NameMaxLength} characters.", nameof(name));
        }

        var trimmedPurpose = purpose.Trim();
        if (trimmedPurpose.Length > PurposeMaxLength)
        {
            throw new ArgumentException($"A database purpose must be at most {PurposeMaxLength} characters.", nameof(purpose));
        }

        return new Database
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            OwnerAccountId = ownerAccountId,
            Name = trimmedName,
            Purpose = trimmedPurpose,
            TemplateId = templateId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
