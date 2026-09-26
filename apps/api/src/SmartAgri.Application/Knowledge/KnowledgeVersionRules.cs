using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>
/// Rules for acting on an existing version. Retrying (<c>POST
/// .../versions/{versionId}/retry</c>, M2 plan Slice 5) is only for a version whose processing
/// failed: one that is still queued or processing would just be processed twice, and one
/// that was processed has nothing to retry. (The frontend mock also lets partially readable
/// documents be retried; the plan does not, because processing the same file again reads
/// the same pages.)
/// </summary>
public static class KnowledgeVersionRules
{
    public const string StillProcessingMessage = "這個版本還在等待或處理中，處理完成後才能重試。";

    public const string NotFailedMessage = "只有處理失敗的版本可以重試。";

    /// <summary>Why a version in <paramref name="status"/> cannot be retried, or
    /// <see langword="null"/> when it can.</summary>
    public static string? RetryRefusal(KnowledgeDocumentStatus status) => status switch
    {
        KnowledgeDocumentStatus.Failed => null,
        KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing => StillProcessingMessage,
        _ => NotFailedMessage,
    };
}
