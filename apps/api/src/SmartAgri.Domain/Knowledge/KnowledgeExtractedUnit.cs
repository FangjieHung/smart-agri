using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// One unit of text read from a <see cref="KnowledgeDocumentVersion"/>'s file (table
/// <c>KnowledgeExtractedUnits</c>, M2 plan §4): a PDF page, a DOCX/Markdown section, a whole
/// plain-text file or an XLSX worksheet — what the extraction preview lists, in
/// <see cref="Ordinal"/> order. Its <see cref="KnowledgeChunk"/>s never cross it.
/// </summary>
/// <remarks>
/// Written only by version processing, all of a version's units together and in the same
/// transaction as the version's status; reprocessing replaces them. Deleted with the version
/// (database cascade through a foreign key that includes <c>OrganizationId</c>).
/// </remarks>
public sealed class KnowledgeExtractedUnit : IOrganizationScoped
{
    /// <summary>Location labels are short: 「第 3 頁」, a heading path, a worksheet name.</summary>
    public const int LocationLabelMaxLength = 300;

    /// <summary>For EF Core materialization.</summary>
    private KnowledgeExtractedUnit()
    {
    }

    public Guid VersionId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>0, 1, 2, … in reading order within the version.</summary>
    public int Ordinal { get; private set; }

    public KnowledgeUnitLocationKind LocationKind { get; private set; }

    /// <summary>Where this is in the file, shown to people as is: 「第 3 頁」,
    /// 「2 退換貨 › 2.1 退貨條件」, 「工作表『配送時間』」.</summary>
    public string LocationLabel { get; private set; } = string.Empty;

    /// <summary>Everything read from the unit, including what was judged unreadable, so the
    /// owner can see what the system saw.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Whether the unit's text can be used: only readable units have chunks.</summary>
    public bool Readable { get; private set; }

    public KnowledgeUnitIssue? IssueCode { get; private set; }

    /// <param name="issueCode">Required, and one that makes a unit unreadable, when
    /// <paramref name="readable"/> is false; otherwise absent or
    /// <see cref="KnowledgeUnitIssue.RowsTruncated"/>.</param>
    public static KnowledgeExtractedUnit Create(
        KnowledgeDocumentVersion version,
        int ordinal,
        KnowledgeUnitLocationKind locationKind,
        string locationLabel,
        string text,
        bool readable,
        KnowledgeUnitIssue? issueCode)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        RequireLocationLabel(locationLabel);

        var unreadableIssue = issueCode is { } issue && KnowledgeUnitIssues.MakesUnreadable(issue);
        if (readable == unreadableIssue)
        {
            throw new ArgumentException(
                "An unreadable unit needs an issue that makes it unreadable; a readable one cannot have such an issue.",
                nameof(issueCode));
        }

        return new KnowledgeExtractedUnit
        {
            VersionId = version.Id,
            OrganizationId = version.OrganizationId,
            Ordinal = ordinal,
            LocationKind = locationKind,
            LocationLabel = locationLabel,
            Text = text,
            Readable = readable,
            IssueCode = issueCode,
        };
    }

    internal static void RequireLocationLabel(string locationLabel)
    {
        ArgumentNullException.ThrowIfNull(locationLabel);
        if (locationLabel.Length is 0 or > LocationLabelMaxLength || string.IsNullOrWhiteSpace(locationLabel))
        {
            throw new ArgumentException(
                $"A location label must be 1-{LocationLabelMaxLength} characters and not blank.",
                nameof(locationLabel));
        }
    }
}
