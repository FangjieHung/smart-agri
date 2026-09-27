using System.Text.Json;

namespace SmartAgri.Application.Jobs;

/// <summary>What an <see cref="IJobHandler"/> is asked to process.</summary>
/// <param name="Payload">The job's JSON payload; see <see cref="ReadPayload{TPayload}"/>.</param>
/// <param name="Attempt">1 on the first run; counts every time the job was claimed.</param>
public sealed record JobContext(
    Guid JobId,
    Guid OrganizationId,
    string Kind,
    string Payload,
    int Attempt,
    int MaxAttempts)
{
    /// <summary>Whether a retryable failure now would fail the job for good.</summary>
    public bool IsLastAttempt => Attempt >= MaxAttempts;

    /// <summary>
    /// The payload deserialized as it was enqueued (<see cref="JsonSerializerOptions.Web"/>).
    /// A payload that cannot be read will not become readable on a retry, so this throws
    /// <see cref="PermanentJobFailure"/>.
    /// </summary>
    public TPayload ReadPayload<TPayload>()
    {
        TPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TPayload>(Payload, JsonSerializerOptions.Web);
        }
        catch (JsonException exception)
        {
            throw new PermanentJobFailure($"The {Kind} job's payload is not a valid {typeof(TPayload).Name}.", exception);
        }

        return payload ?? throw new PermanentJobFailure($"The {Kind} job's payload is empty.");
    }
}
