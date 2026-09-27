using System.Globalization;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>One unit as it will be stored, with the chunks cut from it (none when unreadable).</summary>
public sealed record ProcessedUnit(
    int Ordinal,
    KnowledgeUnitLocationKind Kind,
    string LocationLabel,
    string Text,
    bool Readable,
    KnowledgeUnitIssue? IssueCode,
    IReadOnlyList<KnowledgeTextChunk> Chunks);

/// <summary>The outcome of processing one version: its status and issue, and its units.</summary>
public sealed record ProcessedVersion(KnowledgeDocumentStatus Status, string? Issue, IReadOnlyList<ProcessedUnit> Units);

/// <summary>
/// Turns what an <see cref="IDocumentTextExtractor"/> read into what is stored (M2 plan §4):
/// each unit judged by <see cref="KnowledgeReadability"/>, readable ones chunked by
/// <see cref="KnowledgeChunker"/>, and the version's status —
/// <list type="bullet">
/// <item><c>ready</c>: every unit readable and nothing left unread;</item>
/// <item><c>partially-readable</c>: some units unreadable (the issue lists the pages, or the
/// sections/worksheets), or a limit cut the file short (the issue says which);</item>
/// <item><c>failed</c>: no readable unit at all, with <see cref="KnowledgeProcessingIssues.NoReadableText"/>.</item>
/// </list>
/// Pure: no I/O, so every rule is unit tested.
/// </summary>
public static class KnowledgeVersionProcessing
{
    private const string IssueSeparator = "；";

    public static ProcessedVersion Process(ExtractedDocument document, ExtractionLimits limits, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(options);

        var units = new List<ProcessedUnit>(document.Units.Count);
        foreach (var unit in document.Units)
        {
            var unreadable = KnowledgeReadability.Judge(unit.Kind, unit.Text);
            var issue = unreadable ?? (unit.Sheet is { RowsTruncated: true } ? KnowledgeUnitIssue.RowsTruncated : null);
            units.Add(new ProcessedUnit(
                units.Count,
                unit.Kind,
                unit.LocationLabel,
                unit.Text,
                Readable: unreadable is null,
                issue,
                unreadable is null ? KnowledgeChunker.Chunk(unit, options) : []));
        }

        if (!units.Any(unit => unit.Readable))
        {
            return new ProcessedVersion(KnowledgeDocumentStatus.Failed, KnowledgeProcessingIssues.NoReadableText, units);
        }

        var problems = Problems(document, units, limits).ToList();
        return problems.Count == 0
            ? new ProcessedVersion(KnowledgeDocumentStatus.Ready, null, units)
            : new ProcessedVersion(KnowledgeDocumentStatus.PartiallyReadable, Limit(string.Join(IssueSeparator, problems)), units);
    }

    private static IEnumerable<string> Problems(ExtractedDocument document, List<ProcessedUnit> units, ExtractionLimits limits)
    {
        var unreadablePages = document.Units
            .Where((unit, ordinal) => !units[ordinal].Readable && unit.PageNumber is not null)
            .Select(unit => unit.PageNumber!.Value)
            .ToList();
        if (unreadablePages.Count > 0)
        {
            yield return $"第 {KnowledgeLocationLabels.PageList(unreadablePages)} 頁找不到可讀文字，可能是掃描頁；目前不支援 OCR，這些頁不會用於回答";
        }

        var otherUnreadable = document.Units
            .Where((unit, ordinal) => !units[ordinal].Readable && unit.PageNumber is null)
            .Select(unit => unit.LocationLabel)
            .ToList();
        if (otherUnreadable.Count > 0)
        {
            yield return $"{string.Join('、', otherUnreadable)} 的文字無法辨識，不會用於回答";
        }

        foreach (var sheet in document.Units.Select(unit => unit.Sheet).OfType<ExtractedSheet>().Where(sheet => sheet.RowsTruncated))
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"{KnowledgeLocationLabels.Sheet(sheet.Name)}超過 {limits.MaxSheetRows} 列，只讀取前 {limits.MaxSheetRows} 列");
        }

        if (document.UnitsTruncated)
        {
            var unitName = units.Count > 0 && units[0].Kind == KnowledgeUnitLocationKind.Page ? "頁"
                : units.Count > 0 && units[0].Kind == KnowledgeUnitLocationKind.Sheet ? "個工作表"
                : "個章節";
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"檔案超過 {limits.MaxUnits} {unitName}，只讀取前 {limits.MaxUnits} {unitName}");
        }
    }

    /// <summary>At most <see cref="KnowledgeDocumentVersion.IssueMaxLength"/> characters.</summary>
    private static string Limit(string issue)
    {
        if (issue.Length <= KnowledgeDocumentVersion.IssueMaxLength)
        {
            return issue;
        }

        var cut = KnowledgeDocumentVersion.IssueMaxLength - 1;
        if (char.IsHighSurrogate(issue[cut - 1]))
        {
            cut--;
        }

        return string.Concat(issue.AsSpan(0, cut), "…");
    }
}
