using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One retrievable passage of a <see cref="KnowledgeDocumentVersion"/> (table
/// <c>KnowledgeChunks</c>, M2 plan §4): a piece of one readable
/// <see cref="KnowledgeExtractedUnit"/>, never more than one. The owner can exclude it
/// (a cover page, an appendix, outdated terms) so it is never retrieved.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DocumentId"/> and <see cref="KnowledgeBaseId"/> repeat the version's so
/// retrieval can filter without joins; the foreign key to the version includes both (and
/// <c>OrganizationId</c>), so the database never lets them disagree. Deleted with its version
/// and with its unit (database cascades).
/// </para>
/// <para>
/// Written only by version processing, together with the units. Slice 7 (#41) adds the
/// embedding and the model that produced it to this same row.
/// </para>
/// </remarks>
public sealed class KnowledgeChunk : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private KnowledgeChunk()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid VersionId { get; private set; }

    /// <summary>The <see cref="KnowledgeExtractedUnit.Ordinal"/> of the unit it was cut from.</summary>
    public int UnitOrdinal { get; private set; }

    /// <summary>0, 1, 2, … within its unit.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Where the passage is, for citations: usually its unit's label; for a
    /// worksheet, the rows it covers (「工作表『配送時間』第 2–30 列」).</summary>
    public string LocationLabel { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    /// <summary>Set by the owner: an excluded chunk is never retrieved.</summary>
    public bool Excluded { get; private set; }

    public static KnowledgeChunk Create(
        KnowledgeDocumentVersion version,
        int unitOrdinal,
        int ordinal,
        string locationLabel,
        string text)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfNegative(unitOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        KnowledgeExtractedUnit.RequireLocationLabel(locationLabel);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A chunk needs text.", nameof(text));
        }

        return new KnowledgeChunk
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = version.OrganizationId,
            KnowledgeBaseId = version.KnowledgeBaseId,
            DocumentId = version.DocumentId,
            VersionId = version.Id,
            UnitOrdinal = unitOrdinal,
            Ordinal = ordinal,
            LocationLabel = locationLabel,
            Text = text,
            Excluded = false,
        };
    }

    /// <summary>Excludes or includes the chunk; false when it already was.</summary>
    public bool SetExcluded(bool excluded)
    {
        if (Excluded == excluded)
        {
            return false;
        }

        Excluded = excluded;
        return true;
    }
}
