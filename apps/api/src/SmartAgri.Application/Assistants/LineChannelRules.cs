using System.Text.RegularExpressions;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>
/// The LINE channel's settings, trimmed and valid. <see cref="ChannelSecret"/> and
/// <see cref="AccessToken"/> are plaintext to be protected right away, or <see langword="null"/> to
/// keep the stored one.
/// </summary>
public sealed record LineChannelSettings(
    string OfficialAccountId,
    string ChannelId,
    string? ChannelSecret,
    string? AccessToken,
    string WelcomeMessage)
{
    /// <summary>Never shows the credentials: the generated <c>ToString</c> would print them into
    /// any log line that formats the object.</summary>
    public override string ToString() =>
        $"{nameof(LineChannelSettings)} {{ OfficialAccountId = {OfficialAccountId}, ChannelId = {ChannelId}, " +
        $"ChannelSecret = {(ChannelSecret is null ? "(keep)" : "(new)")}, AccessToken = {(AccessToken is null ? "(keep)" : "(new)")} }}";
}

/// <summary>
/// The rules of <c>PUT /api/v1/assistants/{id}/publishing/line</c> (M5b plan §3 A).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateField"/> is a port of the frontend mock's per-field check (<c>lineChecks()</c> over
/// <c>trimLineSettings()</c>, <c>apps/admin/src/app/core/repositories/publishing-channels.ts</c>), with
/// the same patterns and messages, so the screen and the API refuse exactly the same input. One shared
/// case list, <c>apps/admin/src/app/core/domain/line-field-cases.json</c>, is run against both.
/// </para>
/// <para>
/// JavaScript semantics are kept exactly: <c>String.prototype.trim()</c> and <c>\S</c> use
/// ECMAScript's white space and line terminators (<see cref="IsJavaScriptWhiteSpace"/>, which differs
/// from .NET's <see cref="char.IsWhiteSpace(char)"/> on U+0085 and U+FEFF), <c>\d</c> is ASCII only
/// and <c>$</c> is <c>\z</c>.
/// </para>
/// <para>
/// Credentials are write-only: an empty <c>channelSecret</c> or <c>accessToken</c> keeps the stored
/// value and is only "required" while nothing is stored yet (the first save).
/// </para>
/// </remarks>
public static partial class LineChannelRules
{
    public const string OfficialAccountIdField = "officialAccountId";

    public const string ChannelIdField = "channelId";

    public const string ChannelSecretField = "channelSecret";

    public const string AccessTokenField = "accessToken";

    public const string WelcomeMessageField = "welcomeMessage";

    /// <summary>The frontend's <c>LINE_FIELDS</c> labels, in its order.</summary>
    public static IReadOnlyList<(string Field, string Label)> Fields { get; } =
    [
        (OfficialAccountIdField, "官方帳號 ID"),
        (ChannelIdField, "Channel ID"),
        (ChannelSecretField, "Channel secret"),
        (AccessTokenField, "Channel access token"),
    ];

    public const string WelcomeMessageRequiredMessage = "請填寫歡迎訊息。";

    public static readonly string WelcomeMessageTooLongMessage =
        $"歡迎訊息請在 {AssistantLineChannel.WelcomeMessageMaxLength} 個字以內。";

    /// <summary>Minimum length of a channel access token (the frontend's <c>/^\S{40,}$/</c>).</summary>
    public const int AccessTokenMinLength = 40;

    /// <summary>
    /// The frontend mock's check of one of the four connection fields: <see langword="null"/> when
    /// <paramref name="raw"/>, trimmed, is valid; otherwise the message it shows
    /// (<c>請填寫 {label}。</c> when empty, <c>{label}{rule}</c> when the pattern does not match).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="field"/> is not one of <see cref="Fields"/>.</exception>
    public static string? ValidateField(string field, string? raw)
    {
        var label = LabelOf(field);
        var value = Normalize(raw);
        if (value.Length == 0)
        {
            return $"請填寫 {label}。";
        }

        var (matches, rule) = field switch
        {
            OfficialAccountIdField => (OfficialAccountIdPattern().IsMatch(value), "需以 @ 開頭，接 3–20 個英數字，例如 @anxin-demo。"),
            ChannelIdField => (ChannelIdPattern().IsMatch(value), "應為 10 位數字。"),
            ChannelSecretField => (ChannelSecretPattern().IsMatch(value), "應為 32 個英數字（0–9、a–f）。"),
            _ => (value.Length >= AccessTokenMinLength && !value.Any(IsJavaScriptWhiteSpace), "至少 40 個字元且不含空白。"),
        };
        return matches ? null : label + rule;
    }

    /// <summary>The frontend's <c>trimLineSettings()</c> for one value: JavaScript's <c>trim()</c>.</summary>
    public static string Normalize(string? raw)
    {
        var value = raw ?? string.Empty;
        var start = 0;
        var end = value.Length;
        while (start < end && IsJavaScriptWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsJavaScriptWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary>
    /// The settings form, every broken field reported at once (each under its own key). The official
    /// account id, the channel id and the welcome message are always required (a full replace); an
    /// empty <paramref name="channelSecret"/> or <paramref name="accessToken"/> means "keep the stored
    /// one" when <paramref name="credentialsStored"/>, and is required otherwise.
    /// </summary>
    public static ValidationResult<LineChannelSettings> ForUpdate(
        string? officialAccountId,
        string? channelId,
        string? channelSecret,
        string? accessToken,
        string? welcomeMessage,
        bool credentialsStored)
    {
        var failures = new List<ValidationFailure>();

        Check(OfficialAccountIdField, officialAccountId);
        Check(ChannelIdField, channelId);

        var newSecret = Normalize(channelSecret);
        if (newSecret.Length > 0 || !credentialsStored)
        {
            Check(ChannelSecretField, channelSecret);
        }

        var newToken = Normalize(accessToken);
        if (newToken.Length > 0 || !credentialsStored)
        {
            Check(AccessTokenField, accessToken);
        }

        var welcome = Normalize(welcomeMessage);
        if (welcome.Length == 0)
        {
            failures.Add(new ValidationFailure(WelcomeMessageField, WelcomeMessageRequiredMessage));
        }
        else if (welcome.Length > AssistantLineChannel.WelcomeMessageMaxLength)
        {
            failures.Add(new ValidationFailure(WelcomeMessageField, WelcomeMessageTooLongMessage));
        }

        return failures.Count > 0
            ? ValidationResult<LineChannelSettings>.Invalid(failures)
            : ValidationResult<LineChannelSettings>.Valid(new LineChannelSettings(
                Normalize(officialAccountId),
                Normalize(channelId),
                newSecret.Length > 0 ? newSecret : null,
                newToken.Length > 0 ? newToken : null,
                welcome));

        void Check(string field, string? raw)
        {
            if (ValidateField(field, raw) is { } message)
            {
                failures.Add(new ValidationFailure(field, message));
            }
        }
    }

    /// <summary>The publishing gate's key for "the connection test has not passed".</summary>
    public const string ConnectionGateField = "connection";

    public const string ConnectionNotPassedMessage =
        "請先測試連線，三項檢查（Token 與官方帳號、Webhook 網址、Webhook 連線測試）都通過後才能啟用。";

    public const string PublicBaseUrlMissingMessage =
        "伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl），LINE 無法把訊息送到這個伺服器；請洽系統管理者。";

    /// <summary>The summary message of a refused enable.</summary>
    public const string PublishRefusedMessage = "目前還不能啟用 LINE 頻道，請先處理下列項目。";

    /// <summary>
    /// The LINE channel's publishing gate (<c>POST …/line:publish</c>; M5b plan §3 B): the website
    /// channel's (<see cref="WebsiteChannelRules.PublishFailures"/> — acceptance <c>passed</c> now, the
    /// assistant not paused, every connected knowledge base the owner's own, the server's public URL
    /// known), with "every connection check passed" (<c>connection</c>) in place of "has an allowed
    /// domain". Every reason under its own key, in that order; empty means it may be enabled.
    /// </summary>
    public static IReadOnlyList<ValidationFailure> PublishFailures(
        bool connectionChecksPassed,
        AssistantAcceptanceStatus acceptance,
        AssistantStatus assistantStatus,
        IReadOnlyList<WebsiteKnowledgeBaseRef> nonOwnedKnowledgeBases,
        bool publicBaseUrlConfigured)
    {
        ArgumentNullException.ThrowIfNull(nonOwnedKnowledgeBases);
        var failures = new List<ValidationFailure>();
        if (!connectionChecksPassed)
        {
            failures.Add(new ValidationFailure(ConnectionGateField, ConnectionNotPassedMessage));
        }

        if (acceptance != AssistantAcceptanceStatus.Passed)
        {
            failures.Add(new ValidationFailure(WebsiteChannelRules.AcceptanceField, WebsiteChannelRules.AcceptanceNotPassedMessage));
        }

        if (assistantStatus == AssistantStatus.Paused)
        {
            failures.Add(new ValidationFailure(WebsiteChannelRules.AssistantPausedField, WebsiteChannelRules.AssistantPausedMessage));
        }

        failures.AddRange(nonOwnedKnowledgeBases.Select(knowledgeBase =>
            new ValidationFailure(WebsiteChannelRules.KnowledgeOwnershipField, WebsiteChannelRules.KnowledgeNotOwnedMessage(knowledgeBase.Name))));

        if (!publicBaseUrlConfigured)
        {
            failures.Add(new ValidationFailure(WebsiteChannelRules.PublicBaseUrlField, PublicBaseUrlMissingMessage));
        }

        return failures;
    }

    /// <summary>ECMAScript's <c>WhiteSpace</c> and <c>LineTerminator</c> code points: what
    /// <c>trim()</c> removes and <c>\s</c> matches.</summary>
    public static bool IsJavaScriptWhiteSpace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' '
            || c == (char)0x00A0
            || c == (char)0x1680
            || (c >= (char)0x2000 && c <= (char)0x200A)
            || c == (char)0x2028
            || c == (char)0x2029
            || c == (char)0x202F
            || c == (char)0x205F
            || c == (char)0x3000
            || c == (char)0xFEFF;

    private static string LabelOf(string field)
    {
        foreach (var (candidate, label) in Fields)
        {
            if (candidate == field)
            {
                return label;
            }
        }

        throw new ArgumentException($"'{field}' is not a LINE connection field.", nameof(field));
    }

    /// <summary>The frontend's <c>/^@[a-z0-9._-]{3,20}$/i</c>: JavaScript's non-Unicode
    /// case-insensitive match only folds ASCII letters here, so both cases are listed explicitly.</summary>
    [GeneratedRegex(@"^@[a-zA-Z0-9._-]{3,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OfficialAccountIdPattern();

    /// <summary>The frontend's <c>/^\d{10}$/</c> (JavaScript's <c>\d</c> is ASCII only).</summary>
    [GeneratedRegex(@"^[0-9]{10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelIdPattern();

    /// <summary>The frontend's <c>/^[a-f0-9]{32}$/i</c>.</summary>
    [GeneratedRegex(@"^[a-fA-F0-9]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelSecretPattern();
}
