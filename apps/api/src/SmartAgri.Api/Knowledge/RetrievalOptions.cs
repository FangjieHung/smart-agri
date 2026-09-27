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

    /// <summary>Why these options are unusable, or <see langword="null"/>.</summary>
    public string? Validate()
    {
        if (!double.IsFinite(MinScore) || MinScore is < 0 or > 1)
        {
            return $"{SectionName}:{nameof(MinScore)} must be a cosine similarity of 0-1 (got {MinScore.ToString(CultureInfo.InvariantCulture)}).";
        }

        return Top is < 1 or > KnowledgeRetrievalSettings.MaxTop
            ? $"{SectionName}:{nameof(Top)} must be 1-{KnowledgeRetrievalSettings.MaxTop}."
            : null;
    }

    public KnowledgeRetrievalSettings ToSettings() => new(MinScore, Top);
}
