using System.Globalization;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Knowledge;

namespace SmartAgri.Application.Knowledge;

/// <summary>A batch approval that passed <see cref="KnowledgeReviewRules.ValidateApproval"/>.</summary>
/// <param name="VersionIds">As requested, in order (duplicates kept, so refusals can name the
/// request's positions); <see langword="null"/> where an entry is not a GUID at all.</param>
/// <param name="EffectiveFrom">In UTC, never before the <c>now</c> it was validated at.</param>
public sealed record KnowledgeApprovalRequest(IReadOnlyList<Guid?> VersionIds, DateTimeOffset EffectiveFrom);

/// <summary>
/// The rules of approving versions and disabling documents (M2 plan, Slice 8; ticket #42):
/// <list type="bullet">
/// <item><c>POST /api/v1/knowledge-bases/{id}/versions/approve</c> <c>{ versionIds, effectiveFrom? }</c>
/// (<see cref="ValidateApproval"/>, then <see cref="ApprovalRefusals"/>): all or nothing — if any
/// version cannot be approved, none is, and the refusal names each one.</item>
/// <item><c>POST .../documents/{docId}/disable</c> <c>{ reason }</c> (<see cref="ValidateDisableReason"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Clock skew.</b> <c>effectiveFrom</c> defaults to now and may not be earlier; a time up to
/// <see cref="ClockSkewTolerance"/> (one minute) in the past is taken as now, so a browser whose
/// clock is a little behind the server's — or a form that sends "now" a moment before the
/// request arrives — is not refused. Such an approval takes effect at once and is stored with
/// the server's now, never a time before the approval itself.
/// </remarks>
public static class KnowledgeReviewRules
{
    public const string VersionIdsField = "versionIds";

    public const string EffectiveFromField = "effectiveFrom";

    public const string ReasonField = "reason";

    /// <summary>The largest batch one request may approve.</summary>
    public const int MaxVersionsPerApproval = 500;

    /// <summary>The <c>reason</c> of the <c>422</c> that refuses a batch because of its versions.</summary>
    public const string VersionsNotApprovableReason = "versions-not-approvable";

    public const string VersionIdsRequiredMessage = "請選擇要確認生效的版本。";

    public const string EffectiveFromInvalidMessage = "生效時間格式不正確，請使用含時區的 ISO 8601 時間，例如 2026-10-01T00:00:00+08:00。";

    public const string EffectiveFromInPastMessage = "生效時間不能早於現在。";

    public const string VersionNotFoundMessage = "找不到這個版本，或它不屬於這個知識庫。";

    public const string VersionAlreadyApprovedMessage = "這個版本已經確認生效過了。";

    public const string VersionStillProcessingMessage = "這個版本還在等待或處理中，處理完成後才能確認生效。";

    public const string VersionFailedMessage = "這個版本處理失敗，不能確認生效；請重試處理或上傳新版本。";

    public const string ReasonRequiredMessage = "請填寫停用原因。";

    public const string AlreadyDisabledMessage = "這份文件已經停用了。";

    public const string NotDisabledMessage = "這份文件沒有停用。";

    public static readonly string TooManyVersionsMessage = $"一次最多確認 {MaxVersionsPerApproval} 個版本。";

    public static readonly string ReasonTooLongMessage = $"停用原因最多 {KnowledgeDocument.DisabledReasonMaxLength} 個字。";

    /// <summary>A time this far before now still counts as now (see the remarks).</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The request's shape: at least one and at most <see cref="MaxVersionsPerApproval"/>
    /// entries, and an <c>effectiveFrom</c> that is absent (now), or an ISO 8601 time with a
    /// time zone that is not before now (within <see cref="ClockSkewTolerance"/>). Whether the
    /// versions can be approved is <see cref="ApprovalRefusals"/>'s question.
    /// </summary>
    public static ValidationResult<KnowledgeApprovalRequest> ValidateApproval(
        IReadOnlyList<string?>? versionIds,
        string? effectiveFrom,
        DateTimeOffset now)
    {
        var failures = new List<ValidationFailure>();
        if (versionIds is null || versionIds.Count == 0)
        {
            failures.Add(new ValidationFailure(VersionIdsField, VersionIdsRequiredMessage));
        }
        else if (versionIds.Count > MaxVersionsPerApproval)
        {
            failures.Add(new ValidationFailure(VersionIdsField, TooManyVersionsMessage));
        }

        var effective = now;
        if (!string.IsNullOrWhiteSpace(effectiveFrom))
        {
            if (!TryParseWithOffset(effectiveFrom.Trim(), out var requested))
            {
                failures.Add(new ValidationFailure(EffectiveFromField, EffectiveFromInvalidMessage));
            }
            else if (requested < now - ClockSkewTolerance)
            {
                failures.Add(new ValidationFailure(EffectiveFromField, EffectiveFromInPastMessage));
            }
            else if (requested > now)
            {
                effective = requested;
            }
        }

        return failures.Count > 0
            ? ValidationResult<KnowledgeApprovalRequest>.Invalid(failures)
            : ValidationResult<KnowledgeApprovalRequest>.Valid(new KnowledgeApprovalRequest(
                [.. versionIds!.Select(ParseVersionId)],
                effective.ToUniversalTime()));
    }

    /// <summary>Why <paramref name="version"/> (<see langword="null"/>: not a version of this
    /// knowledge base) cannot be approved, or <see langword="null"/> when it can.</summary>
    public static string? ApprovalRefusal(KnowledgeDocumentVersion? version) => version switch
    {
        null => VersionNotFoundMessage,
        { ReviewState: KnowledgeReviewState.Approved } => VersionAlreadyApprovedMessage,
        { ProcessingStatus: KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing } => VersionStillProcessingMessage,
        { ProcessingStatus: KnowledgeDocumentStatus.Failed } => VersionFailedMessage,
        _ => null,
    };

    /// <summary>
    /// One failure per requested entry that cannot be approved, keyed <c>versionIds[i]</c> by
    /// its position in the request; empty when the whole batch can be approved.
    /// <paramref name="versions"/> holds the knowledge base's versions among the requested ids
    /// (anything else — another knowledge base's, another organization's, none at all — is
    /// simply absent, and refused alike).
    /// </summary>
    public static IReadOnlyList<ValidationFailure> ApprovalRefusals(
        IReadOnlyList<Guid?> requested,
        IReadOnlyDictionary<Guid, KnowledgeDocumentVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(versions);

        var refusals = new List<ValidationFailure>();
        for (var index = 0; index < requested.Count; index++)
        {
            var version = requested[index] is { } id && versions.TryGetValue(id, out var found) ? found : null;
            if (ApprovalRefusal(version) is { } refusal)
            {
                refusals.Add(new ValidationFailure($"{VersionIdsField}[{index}]", refusal));
            }
        }

        return refusals;
    }

    /// <summary>The summary <c>message</c> of a refused batch.</summary>
    public static string BatchRefusedMessage(int refusedCount) =>
        $"有 {refusedCount} 個版本不能確認生效，所以這一批都沒有確認。請移除標示的版本後再試一次。";

    /// <summary>The reason for disabling a document: required, trimmed, at most
    /// <see cref="KnowledgeDocument.DisabledReasonMaxLength"/> characters.</summary>
    public static ValidationResult<string> ValidateDisableReason(string? reason)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ValidationResult<string>.Invalid(ReasonField, ReasonRequiredMessage);
        }

        return trimmed.Length > KnowledgeDocument.DisabledReasonMaxLength
            ? ValidationResult<string>.Invalid(ReasonField, ReasonTooLongMessage)
            : ValidationResult<string>.Valid(trimmed);
    }

    private static Guid? ParseVersionId(string? raw) =>
        Guid.TryParse(raw?.Trim(), out var id) && id != Guid.Empty ? id : null;

    /// <summary>A time with an explicit offset or <c>Z</c>: without one, it would silently be
    /// read in the server's time zone.</summary>
    private static bool TryParseWithOffset(string raw, out DateTimeOffset value)
    {
        value = default;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && parsed.Kind != DateTimeKind.Unspecified
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
