using Microsoft.EntityFrameworkCore;
using Shouldly;
using SmartAgri.Api.Answers.Evaluation;
using SmartAgri.Api.Tests.Authentication;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Domain.Ai;

namespace SmartAgri.Api.Tests.Answers.Evaluation;

/// <summary>
/// The answer evaluation set against real PostgreSQL (M3 plan Slice 13; ticket #83): <c>eval-answers</c>
/// runs end to end with the <c>Fake</c> embedding and chat models — import through the upload,
/// processing and approval pipeline, every question answered through
/// <see cref="SmartAgri.Application.Answers.GroundedAnswerService"/>, follow-up questions given
/// their parent's actual reply as history — and a report with every section is written. Issue #83's
/// acceptance criterion ("用假模型時，報告可以重現") is the second half: the same set answered twice
/// gives byte-for-byte identical result sections.
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class EvalAnswersIntegrationTests : IClassFixture<AuthHostFixture>, IDisposable
{
    private readonly AuthHostFixture _host;
    private readonly DirectoryInfo _reports = Directory.CreateTempSubdirectory("smartagri-answer-eval-report-");

    public EvalAnswersIntegrationTests(AuthHostFixture host)
    {
        _host = host;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static AnswerEvalSet Set { get; } = AnswerEvalSet.Load(AnswerEvalSet.DefaultDirectory);

    public void Dispose() => _reports.Delete(recursive: true);

    [Fact]
    public async Task Eval_answers_with_the_fake_models_runs_end_to_end_and_the_report_is_reproducible()
    {
        var reportPath = Path.Combine(_reports.FullName, "nested", "report.md");
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await EvalAnswersCommand.RunAsync(_host.Factory.Services, ["--report", reportPath, "--timeout", "60"], output, error, CancellationToken);

        exit.ShouldBe(EvalAnswersCommand.ExitSuccess, error.ToString());
        output.ToString().ShouldContain($"報告：{reportPath}");
        output.ToString().ShouldContain("回覆類型正確率 ");
        var report = await File.ReadAllTextAsync(reportPath, CancellationToken);
        report.ShouldStartWith($"# 回答評測：{AuthHostFixture.ChatModel}（");
        foreach (var section in AnswerEvalReport.Sections)
        {
            report.ShouldContain("\n" + section + "\n", Case.Sensitive);
        }

        report.ShouldContain("這是 `Fake` 模型的結果", Case.Sensitive, "the Fake warning");
        report.ShouldContain($"（{Set.Questions.Count} 題）", Case.Sensitive);
        foreach (var question in Set.Questions)
        {
            report.ShouldContain($"| {question.Id} | ", Case.Sensitive, question.Id);
        }

        // Every generate-answer call this run made was recorded, attributed to the eval account.
        // How many there are depends on how many questions' retrieval cleared the threshold —
        // with Fake (hash) embeddings that is not predictable question by question, only bounded:
        // never more than one call per question (company-data-only never calls the model below
        // the threshold), and never for a question with no company-data expectation at all... a
        // no-result question's retrieval can still clear the threshold by coincidence, so only the
        // upper bound (every question) is asserted, not which ones.
        var organizationId = await OrganizationIdAsync();
        await using (var dbContext = _host.Postgres.CreateDbContext(organizationId))
        {
            var calls = await dbContext.ModelInvocations.AsNoTracking()
                .Where(invocation => invocation.Purpose == ModelInvocationPurpose.GenerateAnswer)
                .ToListAsync(CancellationToken);
            calls.Count.ShouldBeInRange(1, Set.Questions.Count, "at least one company-data question should clear the threshold");
            calls.ShouldAllBe(call => call.Model == AuthHostFixture.ChatModel && call.Succeeded);
        }

        // Again: the organization is reset first, so the same set gives the same judgement byte for byte.
        var againPath = Path.Combine(_reports.FullName, "again.md");
        (await EvalAnswersCommand.RunAsync(_host.Factory.Services, ["--report", againPath], TextWriter.Null, error, CancellationToken))
            .ShouldBe(EvalAnswersCommand.ExitSuccess, error.ToString());
        var again = await File.ReadAllTextAsync(againPath, CancellationToken);
        Section(again, "## 結果摘要").ShouldBe(Section(report, "## 結果摘要"));
        Section(again, "## 拒絕原因分布").ShouldBe(Section(report, "## 拒絕原因分布"));
        Section(again, "## 逐題結果").ShouldBe(Section(report, "## 逐題結果"));
    }

    [Fact]
    public async Task A_follow_up_question_is_answered_with_its_parents_question_and_reply_as_history()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var reportPath = Path.Combine(_reports.FullName, "history.md");

        (await EvalAnswersCommand.RunAsync(_host.Factory.Services, ["--report", reportPath], output, error, CancellationToken))
            .ShouldBe(EvalAnswersCommand.ExitSuccess, error.ToString());

        var followUp = Set.Questions.First(question => question.FollowUpOf is not null);
        var organizationId = await OrganizationIdAsync();
        await using var dbContext = _host.Postgres.CreateDbContext(organizationId);
        // The retrieval query for a follow-up joins the previous question's text (M3 plan §7
        // decision D): both questions were embedded, not just the follow-up's own text.
        (await dbContext.ModelInvocations.CountAsync(invocation => invocation.Purpose == ModelInvocationPurpose.EmbedQuery, CancellationToken))
            .ShouldBe(Set.Questions.Count, "one recorded question embedding per question, follow-ups included");
        followUp.ShouldNotBeNull();
    }

    private async Task<Guid> OrganizationIdAsync()
    {
        await using var dbContext = _host.Postgres.CreateDbContext();
        return await dbContext.Organizations.Where(organization => organization.Code == EvalAnswersCommand.OrganizationCode)
            .Select(organization => organization.Id).SingleAsync(CancellationToken);
    }

    /// <summary>A report's section from its heading to the next one.</summary>
    private static string Section(string report, string heading)
    {
        var start = report.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, heading);
        var end = report.IndexOf("\n## ", start + heading.Length + 2, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }
}
