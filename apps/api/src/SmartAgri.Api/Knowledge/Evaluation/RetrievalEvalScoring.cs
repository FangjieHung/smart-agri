using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Api.Knowledge.Evaluation;

/// <summary>One passage a question got back, as the evaluation looks at it.</summary>
public sealed record EvalPassage(
    string DocumentName,
    int VersionNumber,
    KnowledgeVersionState VersionState,
    string LocationLabel,
    string Text,
    double Score)
{
    public static EvalPassage From(RetrievedKnowledgePassage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);
        return new(passage.DocumentName, passage.VersionNumber, passage.VersionState, passage.LocationLabel, passage.Text, passage.Score);
    }
}

/// <summary>One question's top passages (closest first) and where the first expected one ranks.</summary>
/// <param name="HitRank">1-based rank of the first passage matching an expected one
/// (<see cref="RetrievalEvalScoring.Matches"/>); <see langword="null"/> when none is in the top
/// passages, and always for a question that should find nothing.</param>
public sealed record EvalQuestionResult(EvalQuestion Question, IReadOnlyList<EvalPassage> Passages, int? HitRank)
{
    public bool Hit => HitRank is not null;

    /// <summary>The expected passage's score, for a hit.</summary>
    public double? HitScore => HitRank is { } rank ? Passages[rank - 1].Score : null;

    /// <summary>The closest passage's score; <see langword="null"/> when nothing was found.</summary>
    public double? TopScore => Passages.Count > 0 ? Passages[0].Score : null;

    /// <summary>Passages of an expected document but another version than expected: an archived
    /// version leaking into retrieval would show up here.</summary>
    public int OtherVersionPassages => Passages.Count(passage =>
        Question.Expected.Any(expected => expected.Document == passage.DocumentName)
        && !Question.Expected.Any(expected => expected.Document == passage.DocumentName && expected.Version == passage.VersionNumber));
}

/// <summary>Hits of the questions of one category.</summary>
/// <param name="MaxTopScore">For <see cref="RetrievalEvalSet.Unanswerable"/>: the highest score
/// any of them got.</param>
public sealed record EvalCategorySummary(string Category, int Questions, int Hits5, int Hits1, double? MaxTopScore);

/// <summary>A threshold and how many questions it judges correctly: an answerable question whose
/// expected passage scores at least the threshold, and a question that should find nothing whose
/// closest passage scores below it.</summary>
/// <param name="Low">The bottom of the score gap the threshold was taken from.</param>
/// <param name="High">The top of that gap.</param>
public sealed record EvalThreshold(double Value, int Correct, int Total, double Low, double High);

/// <param name="MaxUnanswerable">The highest score among questions that should find nothing, and
/// which question got it.</param>
/// <param name="MinHit">The lowest score of an expected passage among the hits, and which question.</param>
/// <param name="Separable">Every hit scores above every question that should find nothing: a
/// threshold between them judges every question with a hit correctly.</param>
/// <param name="Suggested">The threshold judging the most questions correctly (<see cref="RetrievalEvalScoring.SuggestThreshold"/>).</param>
/// <param name="AtCurrent">How the deployment's current <c>Retrieval:MinScore</c> judges them.</param>
/// <param name="NonEffectivePassages">Passages of a version that is not in effect (always 0
/// unless the eligibility rule is broken).</param>
/// <param name="QuestionsWithOtherVersions">Questions that got a passage of an expected document
/// in a version other than the expected one.</param>
public sealed record RetrievalEvalSummary(
    int Answerable,
    int Hits5,
    int Hits1,
    int Unanswerable,
    IReadOnlyList<EvalCategorySummary> Categories,
    (string QuestionId, double Score)? MaxUnanswerable,
    (string QuestionId, double Score)? MinHit,
    bool Separable,
    EvalThreshold? Suggested,
    EvalThreshold AtCurrent,
    int NonEffectivePassages,
    int QuestionsWithOtherVersions)
{
    public double? HitRate5 => Answerable == 0 ? null : (double)Hits5 / Answerable;

    public double? HitRate1 => Answerable == 0 ? null : (double)Hits1 / Answerable;

    /// <summary>Ticket #50's bar for the chosen model: hit@5 of at least 90%.</summary>
    public bool MeetsTarget => HitRate5 >= RetrievalEvalScoring.TargetHitRate;
}

/// <summary>
/// How the retrieval evaluation (M2 plan Slice 16) judges and sums up what
/// <see cref="KnowledgeRetriever"/> returned: pure, so every rule is unit tested.
/// </summary>
public static class RetrievalEvalScoring
{
    /// <summary>Passages per question: the evaluation measures the top-5 hit rate.</summary>
    public const int Top = 5;

    /// <summary>Ticket #50's acceptance bar for the chosen model.</summary>
    public const double TargetHitRate = 0.9;

    /// <summary>
    /// Whether <paramref name="passage"/> is <paramref name="expected"/>: the same document and
    /// version number, a location label containing the expected location (so 「工作表『運費』」
    /// matches 「工作表『運費』第 2–5 列」) and, when the question gives one, text containing the
    /// expected phrase. Ordinal comparisons throughout.
    /// </summary>
    public static bool Matches(EvalExpectedPassage expected, EvalPassage passage)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(passage);
        return passage.DocumentName == expected.Document
            && passage.VersionNumber == expected.Version
            && passage.LocationLabel.Contains(expected.Location, StringComparison.Ordinal)
            && (expected.Evidence is null || passage.Text.Contains(expected.Evidence, StringComparison.Ordinal));
    }

    /// <summary>Judges one question's passages (closest first).</summary>
    public static EvalQuestionResult Judge(EvalQuestion question, IReadOnlyList<EvalPassage> passages)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(passages);
        for (var index = 0; index < passages.Count; index++)
        {
            if (question.Expected.Any(expected => Matches(expected, passages[index])))
            {
                return new EvalQuestionResult(question, passages, index + 1);
            }
        }

        return new EvalQuestionResult(question, passages, null);
    }

    /// <summary>How many of <paramref name="results"/> <paramref name="threshold"/> judges
    /// correctly (see <see cref="EvalThreshold"/>).</summary>
    public static int CorrectAt(IReadOnlyList<EvalQuestionResult> results, double threshold)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Count(result => result.Question.ExpectsNothing
            ? result.TopScore is not { } top || top < threshold
            : result.HitScore >= threshold);
    }

    /// <summary>
    /// The threshold judging the most questions correctly. Only the scores that matter are
    /// considered — each hit's expected passage and each should-find-nothing question's closest
    /// passage, clamped to 0–1 — and between two neighbouring scores every threshold judges alike,
    /// so each gap (from 0 to the lowest score, between neighbours, from the highest to 1) is
    /// tried; the best is taken, the widest on a tie (the most margin for questions to come), and
    /// its midpoint suggested. When the hits and the should-find-nothing questions separate, that
    /// is the midpoint between the lowest hit and the highest should-find-nothing score.
    /// <see langword="null"/> when there is no score to go by.
    /// </summary>
    public static EvalThreshold? SuggestThreshold(IReadOnlyList<EvalQuestionResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var scores = results
            .Select(result => result.Question.ExpectsNothing ? result.TopScore : result.HitScore)
            .Where(score => score is not null)
            .Select(score => Math.Clamp(score!.Value, 0, 1))
            .Append(0)
            .Append(1)
            .Distinct()
            .Order()
            .ToList();

        EvalThreshold? best = null;
        for (var i = 0; i + 1 < scores.Count; i++)
        {
            var (low, high) = (scores[i], scores[i + 1]);
            var threshold = (low + high) / 2;
            var correct = CorrectAt(results, threshold);
            if (best is null || correct > best.Correct || (correct == best.Correct && high - low > best.High - best.Low))
            {
                best = new EvalThreshold(threshold, correct, results.Count, low, high);
            }
        }

        return results.Any(result => (result.Question.ExpectsNothing ? result.TopScore : result.HitScore) is not null) ? best : null;
    }

    public static RetrievalEvalSummary Summarize(IReadOnlyList<EvalQuestionResult> results, double currentThreshold)
    {
        ArgumentNullException.ThrowIfNull(results);

        var answerable = results.Where(result => !result.Question.ExpectsNothing).ToList();
        var unanswerable = results.Where(result => result.Question.ExpectsNothing).ToList();
        var categories = RetrievalEvalSet.Categories.Keys
            .Select(category => results.Where(result => result.Question.Category == category).ToList())
            .Where(group => group.Count > 0)
            .Select(group => new EvalCategorySummary(
                group[0].Question.Category,
                group.Count,
                group.Count(result => result.HitRank is <= Top),
                group.Count(result => result.HitRank == 1),
                group.Max(result => result.TopScore)))
            .ToList();

        var maxUnanswerable = unanswerable
            .Where(result => result.TopScore is not null)
            .OrderByDescending(result => result.TopScore)
            .Select(result => ((string QuestionId, double Score)?)(result.Question.Id, result.TopScore!.Value))
            .FirstOrDefault();
        var minHit = answerable
            .Where(result => result.HitScore is not null)
            .OrderBy(result => result.HitScore)
            .Select(result => ((string QuestionId, double Score)?)(result.Question.Id, result.HitScore!.Value))
            .FirstOrDefault();

        return new RetrievalEvalSummary(
            answerable.Count,
            answerable.Count(result => result.HitRank is <= Top),
            answerable.Count(result => result.HitRank == 1),
            unanswerable.Count,
            categories,
            maxUnanswerable,
            minHit,
            minHit is { } lowest && (maxUnanswerable is not { } highest || highest.Score < lowest.Score),
            SuggestThreshold(results),
            new EvalThreshold(currentThreshold, CorrectAt(results, currentThreshold), results.Count, currentThreshold, currentThreshold),
            results.Sum(result => result.Passages.Count(passage => passage.VersionState != KnowledgeVersionState.Effective)),
            results.Count(result => result.OtherVersionPassages > 0));
    }
}
