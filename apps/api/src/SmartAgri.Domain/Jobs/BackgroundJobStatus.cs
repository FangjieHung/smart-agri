using System.Text.Json.Serialization;

namespace SmartAgri.Domain.Jobs;

/// <summary>
/// Where a <see cref="BackgroundJob"/> is in its life. Stored as the wire name, which the
/// claim SQL (<c>SmartAgri.Infrastructure.Jobs.JobClaimer</c>) also compares against.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BackgroundJobStatus>))]
public enum BackgroundJobStatus
{
    /// <summary>Waiting for its <see cref="BackgroundJob.RunAfter"/>; also where a job goes
    /// back to after a retryable failure.</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    /// <summary>Claimed by a runner until <see cref="BackgroundJob.LockedUntil"/>. A job still
    /// running after that (its process stopped) may be claimed again.</summary>
    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>Gave up: a permanent failure, or the last allowed attempt failed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
