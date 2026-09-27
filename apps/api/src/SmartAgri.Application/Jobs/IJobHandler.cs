namespace SmartAgri.Application.Jobs;

/// <summary>
/// Processes the background jobs of one kind (M2 plan, Slice 4). Registered in the Api
/// with <c>services.AddJobHandler&lt;THandler&gt;(kind)</c>; the runner creates a fresh
/// dependency-injection scope for every call, in which the current organization is the
/// job's (<see cref="JobContext.OrganizationId"/>), so everything the handler reads or
/// writes through the application's database context is limited to that organization.
/// </summary>
/// <remarks>
/// Delivery is at least once: a job whose runner stopped before recording the outcome is
/// run again once its lease expires, so handlers must be idempotent. Throw
/// <see cref="PermanentJobFailure"/> for errors that retrying cannot fix; any other
/// exception is retried with exponential backoff until the job's maximum attempts.
/// </remarks>
public interface IJobHandler
{
    Task HandleAsync(JobContext job, CancellationToken cancellationToken);

    /// <summary>
    /// Called once when the job has failed for good (a <see cref="PermanentJobFailure"/>,
    /// or the last attempt failed), e.g. to mark the document it was processing as failed.
    /// Runs in its own scope, in one transaction with recording the job as failed, and
    /// only while this runner still owns the job.
    /// </summary>
    /// <param name="error">The message stored as the job's last error.</param>
    Task OnFinalFailureAsync(JobContext job, string error, CancellationToken cancellationToken) => Task.CompletedTask;
}
