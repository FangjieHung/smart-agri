namespace SmartAgri.Domain.Knowledge;

/// <summary>
/// Which chunking rules cut a processed version's chunks
/// (<see cref="KnowledgeDocumentVersion.ChunkFormat"/>; pre-launch plan §3 A, #301). Raised
/// whenever the chunker changes what it produces for a file already stored, so <c>rechunk</c>
/// can find the versions still cut the old way and cut them again from their original files.
/// </summary>
public static class KnowledgeChunkFormat
{
    /// <summary>Before #301: a Markdown or DOCX table was part of its section's text, chunked
    /// with it. What the migration gives every version processed before the column existed.</summary>
    public const int SectionTables = 1;

    /// <summary>#301: every data row of a Markdown pipe table or a DOCX table is a chunk of its
    /// own, one 「欄名：值」 line per non-empty cell; the rest of the section is chunked as before.</summary>
    public const int TableRows = 2;

    /// <summary>The format processing writes now: the one constant to raise.</summary>
    public const int Current = TableRows;
}
