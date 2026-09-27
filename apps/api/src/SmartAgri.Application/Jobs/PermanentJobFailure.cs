namespace SmartAgri.Application.Jobs;

/// <summary>
/// Thrown by an <see cref="IJobHandler"/> for a failure that retrying cannot fix (e.g. an
/// encrypted PDF, or a document that no longer exists): the job fails after this attempt,
/// whatever attempts it has left. The message becomes the job's last error.
/// </summary>
public sealed class PermanentJobFailure : Exception
{
    public PermanentJobFailure(string message)
        : base(message)
    {
    }

    public PermanentJobFailure(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
