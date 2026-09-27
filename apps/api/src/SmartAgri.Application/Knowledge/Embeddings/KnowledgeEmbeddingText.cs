using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge.Embeddings;

/// <summary>
/// What is embedded for a chunk (before the configured document prefix). Processing and
/// <c>reindex</c> both use this, so a chunk re-embedded with the same model gets the same vector.
/// </summary>
public static class KnowledgeEmbeddingText
{
    /// <summary>
    /// A section's heading path (「2 退換貨 › 2.1 退貨條件」) or a worksheet's name and rows
    /// (「工作表『配送時間』第 2–30 列」) says what the passage is about — often more than the
    /// passage itself, e.g. a bare table of fees under 「2.2 運費」 — so it goes on the first
    /// line. A page label (「第 3 頁」) says nothing about the content and is left out, and so is
    /// an FAQ entry's 「FAQ」: its text already starts with the question.
    /// </summary>
    public static string For(KnowledgeUnitLocationKind kind, string locationLabel, string text)
    {
        ArgumentNullException.ThrowIfNull(locationLabel);
        ArgumentNullException.ThrowIfNull(text);

        return kind switch
        {
            KnowledgeUnitLocationKind.Page or KnowledgeUnitLocationKind.Faq => text,
            KnowledgeUnitLocationKind.Section or KnowledgeUnitLocationKind.Sheet => string.Concat(locationLabel, "\n", text),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
