using SmartAgri.Application.Validation;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>
/// The rules of a consented submission (M4 #145) that need no database: the request's own
/// shape, the explicit consent, the consent terms shown before submitting, and whether a retry
/// carries the same content as the submission its key already created. Answers themselves are
/// checked by <see cref="DatabaseAnswerRules.Validate"/>, the same rule as the trial fill.
/// </summary>
/// <remarks>
/// Order the caller applies (and the frontend mock mirrors): request shape (<c>422</c>) → retry of
/// an existing key (same receipt, or <c>409</c>) → stale form version (<c>409</c>) → answers
/// (<c>422</c>) → consent (<c>422</c>). Consent is checked <b>after</b> the answers, as in the mock's
/// <c>submitChatForm</c>, so a member first sees "the phone number is wrong" rather than "tick the
/// box". Nothing is written unless every step passes.
/// </remarks>
public static class DatabaseSubmissionRules
{
    public const string SubmissionIdKey = "submissionId";

    public const string FormVersionNumberKey = "formVersionNumber";

    public const string ConsentKey = "consent";

    public const string SubmissionIdRequiredMessage = "缺少這次填寫的提交編號，請重新載入表單後再送出。";

    public const string FormVersionRequiredMessage = "缺少你填寫的表單版本，請重新載入表單後再送出。";

    /// <summary>The summary message of a submission refused for lack of consent (the mock's).</summary>
    public const string ConsentMissingMessage = "尚未同意，資料沒有送出。";

    /// <summary>The message on the <c>consent</c> key (the mock's).</summary>
    public const string ConsentRequiredMessage = "請先勾選同意，才能送出資料。";

    /// <summary>Shown with every form before submitting; the mock's <c>CHAT_SENSITIVE_NOTICE</c>.</summary>
    public const string SensitiveNotice =
        "請勿填寫身分證字號、病歷、信用卡號或密碼等敏感資料；只填寫處理這次問題需要的內容。";

    /// <summary>
    /// What the member is told about withdrawal before submitting. Withdrawal itself is #146; until
    /// then the text says so instead of promising a button that does not exist.
    /// </summary>
    public const string WithdrawalNotice =
        "送出後會取得一張回執。撤回功能將於後續版本開放：撤回後接收單位會移除這筆資料的內容，只保留「曾提交、已撤回」的軌跡；在那之前如需撤回，請聯絡接收單位。";

    /// <summary>The request's own members: a client key and the form version the member saw.</summary>
    public static ValidationResult<(Guid Key, int FormVersionNumber)> ValidateRequest(Guid? submissionId, int? formVersionNumber)
    {
        var failures = new List<ValidationFailure>();
        if (submissionId is not { } key || key == Guid.Empty)
        {
            failures.Add(new ValidationFailure(SubmissionIdKey, SubmissionIdRequiredMessage));
        }

        if (formVersionNumber is not { } version || version < 1)
        {
            failures.Add(new ValidationFailure(FormVersionNumberKey, FormVersionRequiredMessage));
        }

        return failures.Count > 0
            ? ValidationResult<(Guid, int)>.Invalid(failures)
            : ValidationResult<(Guid, int)>.Valid((submissionId!.Value, formVersionNumber!.Value));
    }

    /// <summary>Only an explicit <see langword="true"/> is consent; missing or false is not.</summary>
    public static bool HasConsented(bool? consent) => consent == true;

    /// <summary>The receiving unit shown before consenting and on the receipt.</summary>
    public static string RecipientFor(string organizationName, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(organizationName);
        ArgumentNullException.ThrowIfNull(databaseName);
        return $"{organizationName}（{databaseName}）";
    }

    /// <summary>The terms a member consents to, for a database as it is now.</summary>
    /// <param name="viewers">Display names of the accounts that can read the records right now
    /// (designated and permitted), in a stable order.</param>
    public static DatabaseConsentTerms TermsFor(
        string organizationName, string databaseName, string purpose, IReadOnlyList<string> viewers)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentNullException.ThrowIfNull(viewers);
        return new DatabaseConsentTerms(
            databaseName, purpose, RecipientFor(organizationName, databaseName), [.. viewers], SensitiveNotice);
    }

    /// <summary>
    /// Whether a retry's validated answers are the content the earlier submission stored: the same
    /// fields in the same order with the same typed values. (A retry is compared, not trusted: the
    /// same key with other content is a client bug or a replay, and gets a <c>409</c>.)
    /// </summary>
    public static bool IsSameContent(
        IReadOnlyList<DatabaseSubmissionEntry> stored, IReadOnlyList<DatabaseAnswerEntry> requested)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(requested);
        if (stored.Count != requested.Count)
        {
            return false;
        }

        var ordered = stored.OrderBy(entry => entry.Position).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var left = ordered[index];
            var right = requested[index];
            if (left.FieldId != right.Field.Id
                || left.TextValue != right.Text
                || left.NumberValue != right.Number
                || !left.ChoiceValues.SequenceEqual(right.Choices, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
