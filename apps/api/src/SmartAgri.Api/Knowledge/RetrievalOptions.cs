using System.Globalization;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Api.Knowledge;

/// <summary>Configuration section <c>Retrieval</c> (see apps/api/README.md, "Retrieval preview");
/// the defaults are written out in <c>appsettings.json</c> too, so operators see them.</summary>
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>The relevance threshold, a cosine similarity of 0–1: a <b>placeholder</b> until
    /// the retrieval evaluation calibrates it (<see cref="KnowledgeRetrievalSettings.DefaultMinScore"/>).
    /// It depends on the embedding model: set it again whenever <c>Ai:Embedding:Model</c> changes.</summary>
    public double MinScore { get; set; } = KnowledgeRetrievalSettings.DefaultMinScore;

    /// <summary>Passages per search when the caller does not say, 1–<see cref="KnowledgeRetrievalSettings.MaxTop"/>.</summary>
    public int Top { get; set; } = KnowledgeRetrievalSettings.DefaultTop;

    /// <summary>The candidate threshold (pre-launch plan §3 B, #302), a cosine similarity of 0 up
    /// to <see cref="MinScore"/>; unset (the default, or an empty value) for none, which behaves
    /// exactly as before it existed. When no passage reaches <see cref="MinScore"/> but some reach
    /// this, a <c>company-data-only</c> assistant still asks the model with those passages and
    /// refuses (<c>cannot-answer</c>) if the model says they do not answer the question.
    /// <c>appsettings.json</c> sets 0.35 (the P5 evaluation, #304); <c>appsettings.Development.json</c>
    /// turns it off, because it is above Development's <see cref="MinScore"/>. Like
    /// <see cref="MinScore"/>, it depends on the embedding model.</summary>
    public double? CandidateMinScore { get; set; }

    /// <summary>Why these options are unusable, or <see langword="null"/>.</summary>
    public string? Validate()
    {
        if (!double.IsFinite(MinScore) || MinScore is < 0 or > 1)
        {
            return $"{SectionName}:{nameof(MinScore)} must be a cosine similarity of 0-1 (got {MinScore.ToString(CultureInfo.InvariantCulture)}).";
        }

        if (Top is < 1 or > KnowledgeRetrievalSettings.MaxTop)
        {
            return $"{SectionName}:{nameof(Top)} must be 1-{KnowledgeRetrievalSettings.MaxTop}.";
        }

        return CandidateMinScore is { } candidate && !(double.IsFinite(candidate) && candidate >= 0 && candidate <= MinScore)
            ? $"{SectionName}:{nameof(CandidateMinScore)} must be 0 up to {SectionName}:{nameof(MinScore)} " +
              $"({MinScore.ToString(CultureInfo.InvariantCulture)}), or unset (got {candidate.ToString(CultureInfo.InvariantCulture)})."
            : null;
    }

    public KnowledgeRetrievalSettings ToSettings() => new(MinScore, Top, CandidateMinScore);
}
