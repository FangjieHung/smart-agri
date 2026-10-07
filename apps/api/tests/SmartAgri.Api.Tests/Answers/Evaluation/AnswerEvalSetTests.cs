using Shouldly;
using SmartAgri.Api.Answers.Evaluation;

namespace SmartAgri.Api.Tests.Answers.Evaluation;

/// <summary>
/// The committed answer evaluation set (<c>apps/api/eval/answers/</c>, M3 plan Slice 13; ticket
/// #83) loads, covers both reply kinds, and every <c>expectedCitedDocuments</c> and
/// <c>followUpOf</c> reference is consistent. No database needed.
/// </summary>
public sealed class AnswerEvalSetTests
{
    private static readonly AnswerEvalSet Set = AnswerEvalSet.Load(AnswerEvalSet.DefaultDirectory);

    [Fact]
    public void The_committed_set_has_both_reply_kinds_a_follow_up_and_a_prompt_injection_question()
    {
        Set.Questions.Count.ShouldBeGreaterThanOrEqualTo(10);
        Set.Questions.Count(question => question.ExpectedKind == AnswerEvalExpectedKind.CompanyData).ShouldBeGreaterThanOrEqualTo(5);
        Set.Questions.Count(question => question.ExpectedKind == AnswerEvalExpectedKind.NoResult).ShouldBeGreaterThanOrEqualTo(3);
        Set.Questions.Count(question => question.FollowUpOf is not null).ShouldBeGreaterThanOrEqualTo(2, "the bank covers multi-turn follow-up (M3 plan §7 decision D)");
        Set.Fingerprint.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData("store-07", "門市星期日有開嗎？", AnswerEvalExpectedKind.CompanyData)]
    [InlineData("store-08", "星期三可以去門市買東西嗎？", AnswerEvalExpectedKind.CompanyData)]
    [InlineData("none-05", "員工請特休要怎麼申請？", AnswerEvalExpectedKind.NoResult)]
    [InlineData("none-06", "統一編號是多少？", AnswerEvalExpectedKind.NoResult)]
    public void The_set_has_the_questions_304_tried_on_the_side(string id, string text, AnswerEvalExpectedKind kind)
    {
        // #324: the weekday paraphrases a table row alone could not answer, and the refusals
        // whose closest passage is a table row.
        var question = Set.Questions.Single(candidate => candidate.Id == id);
        (question.Question, question.ExpectedKind).ShouldBe((text, kind));
        question.ExpectedCitedDocuments.ShouldBe(kind == AnswerEvalExpectedKind.CompanyData ? ["門市資訊.md"] : []);
        question.Note.ShouldNotBeNull();
    }

    [Fact]
    public void The_set_has_a_prompt_injection_sample_document_and_a_question_about_it()
    {
        Set.Documents.ShouldContain(document => document.Name == "系統公告.md");
        var question = Set.Questions.Single(candidate => candidate.ExpectedCitedDocuments.Contains("系統公告.md"));
        question.Note.ShouldNotBeNull();
    }

    [Fact]
    public void Every_company_data_question_names_a_document_that_exists_and_every_no_result_question_names_none()
    {
        var documentNames = Set.Documents.Select(document => document.Name).ToHashSet();
        foreach (var question in Set.Questions)
        {
            switch (question.ExpectedKind)
            {
                case AnswerEvalExpectedKind.CompanyData:
                    question.ExpectedCitedDocuments.ShouldNotBeEmpty(question.Id);
                    question.ExpectedCitedDocuments.ShouldAllBe(document => documentNames.Contains(document), question.Id);
                    break;
                case AnswerEvalExpectedKind.NoResult:
                    question.ExpectedCitedDocuments.ShouldBeEmpty(question.Id);
                    break;
            }
        }
    }

    [Fact]
    public void Every_follow_up_points_at_an_earlier_question_in_the_bank()
    {
        var seen = new HashSet<string>();
        foreach (var question in Set.Questions)
        {
            if (question.FollowUpOf is { } parentId)
            {
                seen.ShouldContain(parentId, $"{question.Id}'s followUpOf must be an earlier question");
            }

            seen.Add(question.Id);
        }
    }

    [Fact]
    public void A_set_with_bad_questions_reports_every_problem_at_once()
    {
        var directory = CopySet();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, AnswerEvalSet.QuestionsFileName),
                """
                {
                  "questions": [
                    { "id": "a", "question": "", "expectedKind": "company-data", "expectedCitedDocuments": [] },
                    { "id": "a", "question": "重複的 id", "expectedKind": "no-result", "expectedCitedDocuments": ["不存在.md"] },
                    { "id": "c", "question": "沒有 expectedKind", "expectedCitedDocuments": [] },
                    { "id": "d", "question": "指向不存在的題目", "expectedKind": "no-result", "followUpOf": "zzz" }
                  ]
                }
                """);

            var problems = Should.Throw<AnswerEvalSetException>(() => AnswerEvalSet.Load(directory)).Problems;

            problems.ShouldContain(problem => problem.Contains("question 必須是 1", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("id 重複", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("company-data 的題目至少要有一個 expectedCitedDocuments", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("不在 documents.json 裡", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("expectedKind 必須是", StringComparison.Ordinal));
            problems.ShouldContain(problem => problem.Contains("followUpOf「zzz」必須是題庫裡在它之前的題目 id", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A scratch copy of the committed set, to break.</summary>
    private static string CopySet()
    {
        var directory = Path.Combine(Path.GetTempPath(), "smartagri-answer-eval-set-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(Set.Directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory, Path.GetRelativePath(Set.Directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return directory;
    }
}
