using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Application.Answers;

/// <summary>What <see cref="KnowledgeTableExpansion.Expand"/> did to one answer's passages (#324).</summary>
/// <param name="Tables">Tables that a selected row brought other rows of.</param>
/// <param name="AddedRows">Rows sent that retrieval did not select.</param>
/// <param name="AddedCharacters">Their length, in Unicode scalars.</param>
/// <param name="Truncated">Some not-excluded rows of a selected row's table were left out for
/// <see cref="GroundedAnswerPrompt.TableRowsMaxCharacters"/>.</param>
public sealed record GroundedTableExpansion(int Tables, int AddedRows, int AddedCharacters, bool Truncated)
{
    /// <summary>No passage was a table row, or none of their tables had another row.</summary>
    public static GroundedTableExpansion None { get; } = new(0, 0, 0, false);
}

/// <summary>
/// Small passages to retrieve, the whole table to answer from (#324; owner's decision of
/// 2026-10-07): after <see cref="GroundedAnswerService"/> has decided which passages to send
/// (threshold and candidates unchanged, so which questions reach the model is unchanged), every
/// selected table-row passage brings the other rows of its table. Pure, so every rule is unit
/// tested.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A table is the same version, unit and <see cref="KnowledgeTablePosition.TableIndex"/>.
/// Its rows are the not-excluded ones <see cref="IKnowledgeTableRows"/> returned; any other
/// passage is sent exactly as before.</item>
/// <item>Each table is one passage, at the place of its first (closest) selected row: the
/// selected rows and the added ones in their original (<see cref="KnowledgeTableRow.Ordinal"/>)
/// order, joined by <see cref="RowSeparator"/>. Later selected rows of the same table are part
/// of it, not passages of their own, so a table is expanded once.</item>
/// <item>The passage keeps that first row's chunk, location, score and document, so its
/// citation points at the row that was retrieved and the reply's cited documents are the same
/// as without the expansion.</item>
/// <item>Added rows share one budget per answer, <c>maxAddedCharacters</c>, in passage order.
/// Within a table they are taken nearest a selected row first (the earlier on a tie); the first
/// that does not fit ends that table's rows and marks the expansion
/// <see cref="GroundedTableExpansion.Truncated"/>.</item>
/// </list>
/// </remarks>
public static class KnowledgeTableExpansion
{
    /// <summary>Between two rows of an expanded table's text.</summary>
    public const string RowSeparator = "\n\n";

    /// <summary>The tables of <paramref name="passages"/>' table rows, in passage order; empty
    /// when none is a table row (then nothing needs to be read).</summary>
    public static IReadOnlyList<KnowledgeTableKey> TablesOf(IReadOnlyList<RetrievedKnowledgePassage> passages)
    {
        ArgumentNullException.ThrowIfNull(passages);
        return [.. passages.Select(KeyOf).OfType<KnowledgeTableKey>().Distinct()];
    }

    /// <summary>The passages to send, with each selected row's table (see the remarks).</summary>
    public static (IReadOnlyList<RetrievedKnowledgePassage> Passages, GroundedTableExpansion Expansion) Expand(
        IReadOnlyList<RetrievedKnowledgePassage> passages,
        IReadOnlyList<KnowledgeTableRow> rows,
        int maxAddedCharacters)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAddedCharacters);

        var rowsByTable = rows.GroupBy(row => row.Table).ToDictionary(group => group.Key, group => group.ToList());
        var selectedByTable = passages
            .Where(passage => passage.Table is not null)
            .GroupBy(passage => KeyOf(passage)!)
            .ToDictionary(group => group.Key, group => group.ToList());

        var budget = maxAddedCharacters;
        var (tables, addedRows, addedCharacters, truncated) = (0, 0, 0, false);
        var expanded = new List<RetrievedKnowledgePassage>(passages.Count);
        var done = new HashSet<KnowledgeTableKey>();
        foreach (var passage in passages)
        {
            if (KeyOf(passage) is not { } table)
            {
                expanded.Add(passage);
                continue;
            }

            if (!done.Add(table))
            {
                continue;
            }

            var selected = selectedByTable[table];
            var selectedOrdinals = selected.Select(row => row.Table!.Ordinal).ToHashSet();
            var others = rowsByTable.TryGetValue(table, out var tableRows)
                ? tableRows.Where(row => !selectedOrdinals.Contains(row.Ordinal)).DistinctBy(row => row.Ordinal).ToList()
                : [];

            var added = new List<KnowledgeTableRow>();
            foreach (var row in others
                .OrderBy(row => selectedOrdinals.Min(ordinal => Math.Abs(row.Ordinal - ordinal)))
                .ThenBy(row => row.Ordinal))
            {
                var length = Length(row.Text);
                if (length > budget)
                {
                    truncated = true;
                    break;
                }

                added.Add(row);
                budget -= length;
                addedCharacters += length;
            }

            if (added.Count == 0 && selected.Count == 1)
            {
                expanded.Add(passage);
                continue;
            }

            if (added.Count > 0)
            {
                tables++;
                addedRows += added.Count;
            }

            var text = string.Join(
                RowSeparator,
                selected.Select(row => (row.Table!.Ordinal, row.Text))
                    .DistinctBy(row => row.Ordinal)
                    .Concat(added.Select(row => (row.Ordinal, row.Text)))
                    .OrderBy(row => row.Ordinal)
                    .Select(row => row.Text.Trim()));
            expanded.Add(passage with { Text = text });
        }

        return (expanded, tables == 0 && !truncated ? GroundedTableExpansion.None : new GroundedTableExpansion(tables, addedRows, addedCharacters, truncated));
    }

    private static KnowledgeTableKey? KeyOf(RetrievedKnowledgePassage passage) =>
        passage.Table is { } position ? new KnowledgeTableKey(passage.VersionId, position.UnitOrdinal, position.TableIndex) : null;

    private static int Length(string text) => text.EnumerateRunes().Count();
}
