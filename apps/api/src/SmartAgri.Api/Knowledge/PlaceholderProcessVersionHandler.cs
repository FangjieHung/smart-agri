using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Jobs;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Knowledge;

/// <summary>
/// <b>TEMPORARY stand-in (M2 Slice 5, #39) — Slice 6 (#40) replaces this class with real text
/// extraction and readability checks; delete it then.</b> Handles
/// <see cref="ProcessKnowledgeVersionJob.Kind"/> by moving the version from
/// <c>queued</c> to <c>processing</c> and then to <c>failed</c> with
/// <see cref="NotYetAvailableIssue"/>, so uploads, statuses and retries can be exercised
/// end to end before any parser exists.
/// </summary>
/// <remarks>
/// Already follows the rules the real handler must keep: a version that no longer exists
/// (its document was deleted after the job was queued) is nothing to do, and running the
/// same job twice (at-least-once delivery) changes nothing the second time.
/// </remarks>
internal sealed class PlaceholderProcessVersionHandler : IJobHandler
{
    public const string NotYetAvailableIssue = "解析功能尚未啟用";

    public const string FinalFailureIssue = "處理時發生錯誤，請重試。";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;

    public PlaceholderProcessVersionHandler(AppDbContext dbContext, TimeProvider clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task HandleAsync(JobContext job, CancellationToken cancellationToken)
    {
        var version = await FindAsync(job, cancellationToken);
        if (version is null)
        {
            return;
        }

        if (version.ProcessingStatus == KnowledgeDocumentStatus.Queued)
        {
            version.StartProcessing(_clock.GetUtcNow());
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Anything else was finished by an earlier delivery of this job.
        if (version.ProcessingStatus == KnowledgeDocumentStatus.Processing)
        {
            version.MarkFailed(NotYetAvailableIssue, _clock.GetUtcNow());
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Never leaves a version stuck in <c>queued</c>/<c>processing</c>, where it
    /// could not be retried.</summary>
    public async Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken)
    {
        var version = await FindAsync(job, cancellationToken);
        if (version?.ProcessingStatus is KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing)
        {
            version.MarkFailed(FinalFailureIssue, _clock.GetUtcNow());
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private Task<KnowledgeDocumentVersion?> FindAsync(JobContext job, CancellationToken cancellationToken)
    {
        var versionId = job.ReadPayload<ProcessKnowledgeVersionJob>().VersionId;
        return _dbContext.KnowledgeDocumentVersions.SingleOrDefaultAsync(version => version.Id == versionId, cancellationToken);
    }
}
