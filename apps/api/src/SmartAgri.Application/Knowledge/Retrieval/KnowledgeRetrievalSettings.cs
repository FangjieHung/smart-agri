namespace SmartAgri.Application.Knowledge.Retrieval;

/// <summary>
/// The deployment's retrieval defaults (section <c>Retrieval</c>, M2 plan Slice 9; the Api builds
/// this from its options): how many passages a search returns, and the relevance threshold
/// below which an assistant that may only use the organization's data answers 「查無結果」
/// without calling a model (grounded-answers ADR). A caller may override both per search
/// (<see cref="KnowledgeRetrievalQuery"/>) — M3 will, with each assistant's own threshold.
/// </summary>
/// <param name="MinScore">The lowest cosine similarity (0–1) that counts as relevant.</param>
/// <param name="Top">How many passages a search returns, 1–<see cref="MaxTop"/>.</param>
public sealed record KnowledgeRetrievalSettings(double MinScore, int Top)
{
    /// <summary>
    /// A <b>placeholder</b>, not a calibrated value: the retrieval evaluation (M2 Slice 16,
    /// #50) measures it for the chosen embedding model and replaces it, in
    /// <c>appsettings.json</c> and here. Similarities are model-specific: 0.3 is in the range
    /// where OpenAI's <c>text-embedding-3</c> models separate related from unrelated text, but
    /// e.g. the e5 family scores almost everything above 0.7, so a deployment that changes model
    /// must set its own.
    /// </summary>
    public const double DefaultMinScore = 0.3;

    /// <summary>Five passages: what the evaluation's "top-5 hit rate" measures, and enough
    /// context for an answer without drowning it.</summary>
    public const int DefaultTop = 5;

    /// <summary>The most passages one search may return (settings and request alike).</summary>
    public const int MaxTop = 20;

    public double MinScore { get; } = MinScore is >= 0 and <= 1
        ? MinScore
        : throw new ArgumentOutOfRangeException(nameof(MinScore), MinScore, "A minimum score is a cosine similarity of 0-1.");

    public int Top { get; } = Top is >= 1 and <= MaxTop
        ? Top
        : throw new ArgumentOutOfRangeException(nameof(Top), Top, $"A search returns 1-{MaxTop} passages.");

    /// <summary><see cref="DefaultMinScore"/> and <see cref="DefaultTop"/>.</summary>
    public static KnowledgeRetrievalSettings Default { get; } = new(DefaultMinScore, DefaultTop);
}
