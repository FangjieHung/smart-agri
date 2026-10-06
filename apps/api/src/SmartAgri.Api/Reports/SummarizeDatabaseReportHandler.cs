using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Reports;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Reports;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Ai;

namespace SmartAgri.Api.Reports;

/// <summary>
/// Handles <see cref="SummarizeDatabaseReportJob.Kind"/> (M4 #150): asks the chat model for a report's
/// AI summary, from the statistics already saved on the report and nothing else
/// (<see cref="ReportSummaryPrompt"/>), and saves the text only if every number in it is one of the
/// statistics (<see cref="ReportSummaryGuard"/>). The statistics columns are never written here.
/// </summary>
/// <remarks>
/// <para>
/// A model failure — any exception from the call, an unconfigured provider included — is recorded on the
/// report as <see cref="ReportSummaryStatus.Failed"/> and the job succeeds: the report stays viewable with
/// its statistics and charts, and a person can retry. A text with a number the statistics lack, or
/// one that is too long, becomes <see cref="ReportSummaryStatus.Discarded"/> and is dropped, not stored.
/// Either way the call is in <c>ModelInvocations</c> with purpose <c>generate-report-summary</c>
/// (organization, owner, assistant, model, tokens, success — no content), like every model call.
/// </para>
/// <para>
/// Idempotent: only a report whose summary is still <see cref="ReportSummaryStatus.Pending"/> is worked on.
/// Delivered again after it finished, it does nothing and makes no model call.
/// </para>
/// </remarks>
internal sealed class SummarizeDatabaseReportHandler : IJobHandler
{
    private readonly AppDbContext _dbContext;
    private readonly IChatClient _chat;
    private readonly IOrganizationChatModelResolver _chatModel;
    private readonly TimeProvider _clock;
    private readonly ILogger<SummarizeDatabaseReportHandler> _logger;

    public SummarizeDatabaseReportHandler(
        AppDbContext dbContext,
        IChatClient chat,
        IOrganizationChatModelResolver chatModel,
        TimeProvider clock,
        ILogger<SummarizeDatabaseReportHandler> logger)
    {
        _dbContext = dbContext;
        _chat = chat;
        _chatModel = chatModel;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var reportId = job.ReadPayload<SummarizeDatabaseReportJob>().ReportId;
        var report = await _dbContext.DatabaseReports
            .SingleOrDefaultAsync(candidate => candidate.Id == reportId, cancellationToken);
        if (report is null || report.Status != ReportStatus.Generated || report.SummaryStatus != ReportSummaryStatus.Pending)
        {
            return;
        }

        var statistics = JsonSerializer.Deserialize<DatabasePeriodSummaryResult>(report.StatisticsJson!, JsonSerializerOptions.Web)
            ?? throw new PermanentJobFailure($"Report {report.Id}: the saved statistics are empty.");
        var facts = ReportSummaryPrompt.Facts(statistics);

        var ownerId = await _dbContext.Assistants.AsNoTracking()
            .Where(assistant => assistant.Id == report.AssistantId)
            .Select(assistant => (Guid?)assistant.OwnerAccountId)
            .SingleOrDefaultAsync(cancellationToken);
        var attribution = new ModelInvocationAttribution(ModelInvocationPurpose.GenerateReportSummary, ownerId, report.AssistantId);

        string text;
        string model;
        try
        {
            var response = await _chat.GetResponseAsync(ReportSummaryPrompt.Messages(facts), attribution.ToChatOptions(), cancellationToken);
            text = response.Text?.Trim() ?? string.Empty;
            // Without a model name in the response, the model the organization's call went to.
            model = string.IsNullOrWhiteSpace(response.ModelId)
                ? (await _chatModel.ResolveAsync(cancellationToken)).Entry.Model
                : response.ModelId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The AI summary of report {ReportId} failed.", report.Id);
            await SaveAsync(() => report.SummaryFailed(ReportDataRules.SummaryFailedNote, _clock.GetUtcNow()), cancellationToken);
            return;
        }

        if (text.Length == 0)
        {
            await SaveAsync(() => report.SummaryFailed(ReportDataRules.SummaryFailedNote, _clock.GetUtcNow()), cancellationToken);
            return;
        }

        if (text.Length > DatabaseReport.SummaryTextMaxLength)
        {
            await SaveAsync(() => report.SummaryDiscarded(ReportDataRules.SummaryTooLongNote, _clock.GetUtcNow()), cancellationToken);
            return;
        }

        var check = ReportSummaryGuard.Check(text, facts);
        if (!check.IsAcceptable)
        {
            _logger.LogWarning(
                "The AI summary of report {ReportId} was discarded: {Count} number(s) not in the statistics.",
                report.Id,
                check.Unverified.Count);
            await SaveAsync(() => report.SummaryDiscarded(ReportDataRules.SummaryDiscardedNote, _clock.GetUtcNow()), cancellationToken);
            return;
        }

        await SaveAsync(() => report.SummaryReady(text, model, _clock.GetUtcNow()), cancellationToken);
    }

    private async Task SaveAsync(Action change, CancellationToken cancellationToken)
    {
        change();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
