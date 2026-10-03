namespace SmartAgri.Application.Reports;

/// <summary>
/// The payload of a <see cref="Kind"/> background job (M4 #150): produce the report of one schedule's
/// period. The job's <c>RunAfter</c> is the instant the period is over. Only ids and a date — the
/// assistant, the database and its records are read when the job runs, so nothing stale is acted on.
/// </summary>
/// <remarks>
/// Delivery is at least once. The handler is idempotent: a schedule that is gone (turned off, replaced,
/// its assistant or database deleted) or already past <see cref="PeriodFrom"/> means nothing to do, and the
/// unique index on the report makes a second delivery save nothing. The schedule's own compare-and-set
/// on <c>NextPeriodFrom</c> queues the following job exactly once.
/// </remarks>
public sealed record GenerateDatabaseReportJob(Guid ScheduleId, DateOnly PeriodFrom)
{
    public const string Kind = "reports.generate-period";
}

/// <summary>
/// The payload of a <see cref="Kind"/> background job (M4 #150): write the AI summary of one report.
/// Queued in the same transaction that saves a report with enough records, and again when someone
/// retries a failed summary.
/// </summary>
/// <remarks>
/// A failed model call does <b>not</b> fail or retry the job: it is recorded on the report (summary
/// <c>failed</c>), the statistics stay as they are, and a person can retry. Only an infrastructure error
/// (the database) is retried by the queue, up to <see cref="MaxAttempts"/>.
/// </remarks>
public sealed record SummarizeDatabaseReportJob(Guid ReportId)
{
    public const string Kind = "reports.summarize";

    public const int MaxAttempts = 3;
}
