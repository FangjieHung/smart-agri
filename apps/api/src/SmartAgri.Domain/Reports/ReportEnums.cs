using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Reports;

/// <summary>How often an assistant's periodic report is produced (M4 #150). Wire names equal the
/// frontend's <c>PeriodicReportSchedule</c> members other than <c>off</c> (no schedule row at all).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportFrequency>))]
public enum ReportFrequency
{
    /// <summary>One report per calendar week of the statistics time zone, Monday to Sunday.</summary>
    [JsonStringEnumMemberName("weekly")]
    Weekly,

    /// <summary>One report per calendar month of the statistics time zone.</summary>
    [JsonStringEnumMemberName("monthly")]
    Monthly,
}

/// <summary>Whether a period produced a report.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportStatus>))]
public enum ReportStatus
{
    /// <summary>The statistics of the period were computed and saved.</summary>
    [JsonStringEnumMemberName("generated")]
    Generated,

    /// <summary>The period came due but no report was produced; <see cref="DatabaseReport.SkipReason"/>
    /// says why. Nothing about the database's records is stored.</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>Why a due period produced no report.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportSkipReason>))]
public enum ReportSkipReason
{
    /// <summary>The assistant is no longer connected to the database.</summary>
    [JsonStringEnumMemberName("not-connected")]
    NotConnected,

    /// <summary>The assistant's owner may no longer read the database's records (designation or
    /// <c>read-consented-submissions</c> removed): the schedule runs as the owner, so it stops.</summary>
    [JsonStringEnumMemberName("owner-cannot-read")]
    OwnerCannotRead,
}

/// <summary>Whether a generated report has enough records to compare with the period before it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportDataState>))]
public enum ReportDataState
{
    /// <summary>Both the period and the period before it have records: the changes are real.</summary>
    [JsonStringEnumMemberName("sufficient")]
    Sufficient,

    /// <summary>The period or the one before it has no records: the counts and sums are saved as
    /// they are, but no change, trend or AI summary is produced (<c>紀錄不足</c>).</summary>
    [JsonStringEnumMemberName("insufficient-records")]
    InsufficientRecords,
}

/// <summary>State of a report's AI summary, kept apart from its statistics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportSummaryStatus>))]
public enum ReportSummaryStatus
{
    /// <summary>No summary is made: the report has too few records, or is a skipped period.</summary>
    [JsonStringEnumMemberName("not-requested")]
    NotRequested,

    /// <summary>A summary job is queued or running.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The model wrote a summary and every number in it is in the statistics.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>The model call failed. The statistics and charts are unaffected; the summary can be retried.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The model's text contained a number that is not in the statistics, so it was thrown away
    /// (never shown). It can be retried.</summary>
    [JsonStringEnumMemberName("discarded")]
    Discarded,
}
