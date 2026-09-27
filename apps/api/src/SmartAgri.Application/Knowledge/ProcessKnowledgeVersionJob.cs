namespace SmartAgri.Application.Knowledge;

/// <summary>
/// The payload of a <see cref="Kind"/> background job: process one uploaded version (Slice 5
/// enqueues it; Slice 6, #40, does the real processing). Ids only, never file content.
/// </summary>
/// <remarks>
/// Enqueued in the same save as the version it names (on upload and on retry), so there is
/// no version without its job. Delivery is at least once, and a version may be deleted
/// while its job waits: a handler must treat a version that no longer exists as nothing to
/// do, and must be idempotent for one that does.
/// </remarks>
public sealed record ProcessKnowledgeVersionJob(Guid VersionId)
{
    public const string Kind = "knowledge.process-version";
}
