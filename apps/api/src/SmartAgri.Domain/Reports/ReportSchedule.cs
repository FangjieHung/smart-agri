using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Reports;

/// <summary>
/// An assistant's periodic report setting (table <c>AssistantReportSchedules</c>, M4 #150): which
/// connected database it reports on and how often. At most one per assistant (unique index); it is
/// removed with the assistant or the database, and replaced (new <see cref="Id"/>) whenever the owner
/// changes the frequency or the database, so a job queued for the old setting finds nothing.
/// </summary>
/// <remarks>
/// <para>
/// The schedule is a <b>chain of jobs</b>, not a clock: the job for period <see cref="NextPeriodFrom"/>
/// runs once that period is over, saves the report and, in the same transaction, moves
/// <see cref="NextPeriodFrom"/> to the next period with a compare-and-set and queues the next job. A job
/// delivered twice or arriving late (the worker was down) therefore produces each period exactly once,
/// in order.
/// </para>
/// <para>
/// It holds no permission. The job re-checks, for the period it is about, that the assistant is still
/// connected to the database and that the owner may still read its records.
/// </para>
/// <para>
/// <b>Auto-disable (#179).</b> <see cref="ConsecutiveSkips"/> counts the skipped periods in a row; a
/// generated period resets it. The period that makes it <see cref="AutoDisableAfterSkips"/> sets
/// <see cref="AutoDisabledAt"/> and <see cref="AutoDisabledReason"/> (that period's skip reason) in the same
/// compare-and-set that advances <see cref="NextPeriodFrom"/>, and no next job is queued: a disabled schedule
/// writes nothing more. It stays (the owner's settings show why) until the owner turns it off, changes it, or
/// re-enables it, each of which replaces it with a fresh schedule.
/// </para>
/// </remarks>
public sealed class ReportSchedule : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private ReportSchedule()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The assistant whose owner the report is produced for (composite foreign key with the
    /// organization; cascade).</summary>
    public Guid AssistantId { get; private set; }

    /// <summary>The database reported on (composite foreign key with the organization; cascade).</summary>
    public Guid DatabaseId { get; private set; }

    public ReportFrequency Frequency { get; private set; }

    /// <summary>First day (in the statistics time zone) of the next period to report on. Advanced only by
    /// the job of that period.</summary>
    public DateOnly NextPeriodFrom { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>How many periods in a row were skipped, up to and including the last one reported.</summary>
    public int ConsecutiveSkips { get; private set; }

    /// <summary>When the schedule stopped itself after <see cref="AutoDisableAfterSkips"/> skipped periods in a
    /// row; <see langword="null"/> while it runs.</summary>
    public DateTimeOffset? AutoDisabledAt { get; private set; }

    /// <summary>The skip reason of the period that disabled the schedule; set exactly when
    /// <see cref="AutoDisabledAt"/> is.</summary>
    public ReportSkipReason? AutoDisabledReason { get; private set; }

    /// <summary>Skipped periods in a row after which the schedule disables itself (owner decision 2026-10-05).</summary>
    public const int AutoDisableAfterSkips = 3;

    public bool IsAutoDisabled => AutoDisabledAt is not null;

    public static ReportSchedule Create(
        Guid organizationId,
        Guid assistantId,
        Guid databaseId,
        ReportFrequency frequency,
        DateOnly firstPeriodFrom,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || assistantId == Guid.Empty || databaseId == Guid.Empty)
        {
            throw new ArgumentException("A schedule needs an organization, an assistant and a database.");
        }

        if (!Enum.IsDefined(frequency))
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Not a declared frequency.");
        }

        return new ReportSchedule
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            AssistantId = assistantId,
            DatabaseId = databaseId,
            Frequency = frequency,
            NextPeriodFrom = firstPeriodFrom,
            CreatedAt = now,
        };
    }
}
