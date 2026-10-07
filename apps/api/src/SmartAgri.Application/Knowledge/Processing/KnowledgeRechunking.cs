using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Processing;

/// <summary>A unit as stored (<see cref="KnowledgeExtractedUnit"/>).</summary>
public sealed record StoredUnit(int Ordinal, KnowledgeUnitLocationKind Kind, string LocationLabel, string Text, bool Readable);

/// <summary>A chunk as stored (<see cref="KnowledgeChunk"/>), with the owner's exclusion.</summary>
public sealed record StoredChunk(Guid Id, int UnitOrdinal, int Ordinal, string LocationLabel, string Text, bool Excluded);

/// <summary>A chunk <c>rechunk</c> writes, and whether it inherits an exclusion.</summary>
public sealed record RechunkedChunk(int Ordinal, string LocationLabel, string Text, bool Excluded);

/// <summary>One unit whose chunks change: the stored ones it deletes (and which of those were
/// excluded when planned) and the ones it writes.</summary>
public sealed record RechunkedUnit(
    int UnitOrdinal,
    KnowledgeUnitLocationKind Kind,
    IReadOnlyList<Guid> OldChunkIds,
    IReadOnlyList<Guid> ExcludedOldChunkIds,
    IReadOnlyList<RechunkedChunk> Chunks);

/// <summary>
/// What cutting a processed version again does (<see cref="KnowledgeRechunking.Plan"/>).
/// </summary>
/// <param name="Mismatch">Why the version cannot be cut again (what was read from its file now
/// is not what is stored: other units, or another status); then nothing else is set.</param>
/// <param name="Units">Only the units whose chunks change; the others keep theirs, ids,
/// vectors and exclusions included.</param>
/// <param name="UnmappedExclusions">Excluded chunks that no new chunk inherits the exclusion
/// from: the owner should check those passages again.</param>
public sealed record RechunkPlan(
    string? Mismatch,
    IReadOnlyList<RechunkedUnit> Units,
    IReadOnlyList<StoredChunk> UnmappedExclusions,
    int OldChunkCount,
    int NewChunkCount)
{
    /// <summary>Chunks to embed: every chunk of a changed unit.</summary>
    public int ChunksToEmbed => Units.Sum(unit => unit.Chunks.Count);

    /// <summary>Exclusions the new chunks inherit.</summary>
    public int ExclusionsKept => Units.Sum(unit => unit.Chunks.Count(chunk => chunk.Excluded));
}

/// <summary>
/// Plans <c>rechunk</c> for one processed version (#301): compares the chunks the current rules
/// cut from its original file with the stored ones, unit by unit. Pure, so every rule is unit
/// tested.
/// </summary>
/// <remarks>
/// <para>
/// The units must be the same (count, kind, label, text, readability) and so must the status
/// and issue: only the chunking rules changed, not what the file says. Anything else — e.g.
/// <c>Knowledge:MaxExtractedUnits</c> changed since — is a <see cref="RechunkPlan.Mismatch"/>
/// and the version is left alone.
/// </para>
/// <para>
/// A unit whose new chunks have the same labels and texts as the stored ones, in order, is
/// left as it is (a PDF page, a worksheet, a section without tables): no model call, and its
/// chunk ids, which citations point back to, stay. A changed unit's new chunks inherit
/// exclusions from its stored ones:
/// </para>
/// <list type="number">
/// <item>a new chunk with the same text as a stored one is excluded when that one was;</item>
/// <item>otherwise, it is excluded when an excluded stored chunk of the unit contains all of it
/// — every line, or for a table row's 「欄名：值」 line at least the value, since the stored
/// chunk had the row as 「值 | 值」 — so excluding a whole section keeps its rows excluded;</item>
/// <item>an excluded stored chunk that passes its exclusion to no new chunk is reported
/// (<see cref="RechunkPlan.UnmappedExclusions"/>), never guessed.</item>
/// </list>
/// </remarks>
public static class KnowledgeRechunking
{
    public static RechunkPlan Plan(
        KnowledgeDocumentStatus storedStatus,
        string? storedIssue,
        IReadOnlyList<StoredUnit> storedUnits,
        IReadOnlyList<StoredChunk> storedChunks,
        ProcessedVersion processed)
    {
        ArgumentNullException.ThrowIfNull(storedUnits);
        ArgumentNullException.ThrowIfNull(storedChunks);
        ArgumentNullException.ThrowIfNull(processed);

        if (processed.Status != storedStatus || processed.Issue != storedIssue)
        {
            return Mismatched($"重新讀取後的狀態（{processed.Status}）與保存的（{storedStatus}）不同");
        }

        var units = storedUnits.OrderBy(unit => unit.Ordinal).ToList();
        if (units.Count != processed.Units.Count)
        {
            return Mismatched($"重新讀取後有 {processed.Units.Count} 個單元，保存的是 {units.Count} 個");
        }

        for (var i = 0; i < units.Count; i++)
        {
            var (stored, read) = (units[i], processed.Units[i]);
            if (stored.Ordinal != read.Ordinal || stored.Kind != read.Kind || stored.LocationLabel != read.LocationLabel
                || stored.Text != read.Text || stored.Readable != read.Readable)
            {
                return Mismatched($"重新讀取後「{read.LocationLabel}」的內容與保存的不同");
            }
        }

        var chunksByUnit = storedChunks
            .GroupBy(chunk => chunk.UnitOrdinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(chunk => chunk.Ordinal).ToList());
        var changed = new List<RechunkedUnit>();
        var unmapped = new List<StoredChunk>();
        foreach (var unit in processed.Units)
        {
            var old = chunksByUnit.TryGetValue(unit.Ordinal, out var list) ? list : [];
            if (old.Count == unit.Chunks.Count
                && old.Zip(unit.Chunks).All(pair => pair.First.LocationLabel == pair.Second.LocationLabel && pair.First.Text == pair.Second.Text))
            {
                continue;
            }

            var excluded = MapExclusions(old, unit.Chunks, out var lost);
            unmapped.AddRange(lost);
            changed.Add(new RechunkedUnit(
                unit.Ordinal,
                unit.Kind,
                [.. old.Select(chunk => chunk.Id)],
                [.. old.Where(chunk => chunk.Excluded).Select(chunk => chunk.Id)],
                [.. unit.Chunks.Select((chunk, ordinal) => new RechunkedChunk(ordinal, chunk.LocationLabel, chunk.Text, excluded[ordinal]))]));
        }

        return new RechunkPlan(null, changed, unmapped, storedChunks.Count, processed.Units.Sum(unit => unit.Chunks.Count));

        static RechunkPlan Mismatched(string reason) => new(reason, [], [], 0, 0);
    }

    /// <summary>Whether each new chunk inherits an exclusion (see the remarks), and the
    /// excluded stored chunks none inherits from.</summary>
    private static bool[] MapExclusions(List<StoredChunk> old, IReadOnlyList<KnowledgeTextChunk> chunks, out List<StoredChunk> unmapped)
    {
        var excluded = new bool[chunks.Count];
        unmapped = [];
        var excludedOld = old.Where(chunk => chunk.Excluded).ToList();
        if (excludedOld.Count == 0)
        {
            return excluded;
        }

        var used = new HashSet<Guid>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var text = chunks[i].Text;
            var same = old.Where(chunk => chunk.Text == text).ToList();
            if (same.Count > 0)
            {
                excluded[i] = same.Any(chunk => chunk.Excluded);
                used.UnionWith(same.Where(chunk => chunk.Excluded).Select(chunk => chunk.Id));
                continue;
            }

            foreach (var candidate in excludedOld.Where(candidate => Contains(candidate.Text, text)))
            {
                excluded[i] = true;
                used.Add(candidate.Id);
            }
        }

        unmapped.AddRange(excludedOld.Where(chunk => !used.Contains(chunk.Id)));
        return excluded;
    }

    /// <summary>Whether <paramref name="container"/> holds every line of <paramref name="text"/>
    /// — a table row's 「欄名：值」 line by its value.</summary>
    private static bool Contains(string container, string text) =>
        text.Split('\n').Where(line => line.Length > 0).All(line =>
            container.Contains(line, StringComparison.Ordinal)
            || (line.IndexOf(ExtractedTable.NameValueSeparator, StringComparison.Ordinal) is var separator and >= 0
                && container.Contains(line[(separator + ExtractedTable.NameValueSeparator.Length)..], StringComparison.Ordinal)));
}
