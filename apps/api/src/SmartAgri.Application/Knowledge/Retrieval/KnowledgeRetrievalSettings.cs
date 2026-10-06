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
    /// Calibrated for OpenAI's <c>text-embedding-3-small</c> by the retrieval evaluation
    /// (<c>docs/evals/2026-10-06-retrieval-text-embedding-3-small.md</c>, #191/#192): the
    /// suggested threshold, judging 32 of 34 questions correctly (it misses one answerable
    /// question scoring 0.379 and one should-find-nothing question scoring 0.458). Kept equal to
    /// <c>appsettings.json</c>. Similarities are model-specific — the e5 family scores almost
    /// everything above 0.7, and the <c>Fake</c> model the Development environment uses scores a
    /// matching passage around 0.32, so <c>appsettings.Development.json</c> sets 0.3 — and a
    /// deployment that changes model must set its own.
    /// </summary>
    public const double DefaultMinScore = 0.406;

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
