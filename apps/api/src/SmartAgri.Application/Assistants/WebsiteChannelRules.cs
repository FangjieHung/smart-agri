using System.Text.RegularExpressions;
using SmartAgri.Application.Validation;
using SmartAgri.Domain;
using SmartAgri.Domain.Assistants;

namespace SmartAgri.Application.Assistants;

/// <summary>The website channel's settings, trimmed, normalized and within limits.</summary>
/// <param name="AllowedDomains">Lower-case host names, in the order given, without duplicates.</param>
public sealed record WebsiteChannelSettings(
    string DisplayName,
    string WelcomeMessage,
    WebsiteBrandColor BrandColor,
    WebsiteLauncherPosition Position,
    IReadOnlyList<string> AllowedDomains);

/// <summary>A connected knowledge base, by id and name (for <see cref="WebsiteChannelRules.PublishFailures"/>).</summary>
public sealed record WebsiteKnowledgeBaseRef(Guid Id, string Name);

/// <summary>
/// The rules of <c>PUT /api/v1/assistants/{id}/publishing/website</c> and of the publishing gate
/// (<c>POST …/website:publish</c>; M5a plan §3 C, decision B).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateDomain"/> and <see cref="ForUpdate"/> are ports of the frontend's
/// <c>validateAllowedDomain()</c> and <c>validateWebsiteSettings()</c>
/// (<c>apps/admin/src/app/core/domain/publishing.model.ts</c>,
/// <c>apps/admin/src/app/core/repositories/publishing-channels.ts</c>), with the same messages, so
/// the screen and the API refuse exactly the same input. One shared case list,
/// <c>apps/admin/src/app/core/domain/allowed-domain-cases.json</c>, is run against both.
/// </para>
/// <para>
/// A host name only: lower case after trimming, no scheme, path, port or wildcard, at most
/// <see cref="AssistantWebsiteChannel.MaxAllowedDomains"/>. <c>frame-ancestors</c> always uses
/// <c>https://&lt;domain&gt;</c>, so <c>www.example.com</c> and <c>example.com</c> are listed
/// separately (M5a plan §3 B).
/// </para>
/// </remarks>
public static partial class WebsiteChannelRules
{
    public const string DisplayNameField = "displayName";

    public const string WelcomeMessageField = "welcomeMessage";

    public const string BrandColorField = "brandColor";

    public const string PositionField = "position";

    public const string AllowedDomainsField = "allowedDomains";

    /// <summary>Publishing-gate failure keys (<c>errors</c> of the <c>422</c>).</summary>
    public const string AcceptanceField = "acceptance";

    public const string AllowedDomainsGateField = "allowed-domains";

    public const string AssistantPausedField = "assistant-paused";

    public const string KnowledgeOwnershipField = "knowledge-ownership";

    public const string PublicBaseUrlField = "public-base-url";

    public const string DisplayNameRequiredMessage = "請填寫顯示名稱。";

    public static readonly string DisplayNameTooLongMessage =
        $"顯示名稱請在 {AssistantWebsiteChannel.DisplayNameMaxLength} 個字以內。";

    public const string WelcomeMessageRequiredMessage = "請填寫歡迎語。";

    public static readonly string WelcomeMessageTooLongMessage =
        $"歡迎語請在 {AssistantWebsiteChannel.WelcomeMessageMaxLength} 個字以內。";

    public const string BrandColorInvalidMessage = "請選擇品牌色。";

    public const string PositionInvalidMessage = "請選擇顯示位置。";

    public const string DomainRequiredMessage = "請輸入網域，例如 shop.example.com。";

    public const string DomainHasSchemeOrPathMessage = "只需要填網域，不要包含 https:// 或路徑，例如 shop.example.com。";

    public static readonly string TooManyDomainsMessage = $"最多可設定 {AssistantWebsiteChannel.MaxAllowedDomains} 個網域。";

    public const string AcceptanceNotPassedMessage = "驗收狀態必須是「通過」才能對外發布；請先到題組頁執行驗收並讓所有題目通過。";

    public const string NoAllowedDomainMessage = "請先設定至少一個允許嵌入的網域。";

    public const string AssistantPausedMessage = "助理目前已暫停，請先恢復助理再發布。";

    public const string PublicBaseUrlMissingMessage =
        "伺服器尚未設定對外網址（PublicChannels:PublicBaseUrl），無法產生嵌入程式碼；請洽系統管理者。";

    /// <summary>The summary message of a refused publish.</summary>
    public const string PublishRefusedMessage = "目前還不能對外發布，請先處理下列項目。";

    public static string KnowledgeNotOwnedMessage(string knowledgeBaseName) =>
        $"「{knowledgeBaseName}」不是助理擁有者自己的知識庫，對外發布時不能使用；請解除連接後再發布。";

    /// <summary>The frontend's <c>normalizeDomain()</c>: trimmed and lower case.</summary>
    public static string NormalizeDomain(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The frontend's <c>validateAllowedDomain(raw, existing)</c>: <see langword="null"/> when
    /// <paramref name="raw"/> may be added after <paramref name="existing"/> (already normalized
    /// domains), otherwise the message to show.
    /// </summary>
    public static string? ValidateDomain(string? raw, IReadOnlyCollection<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var domain = NormalizeDomain(raw ?? string.Empty);
        if (domain.Length == 0)
        {
            return DomainRequiredMessage;
        }

        if (SchemePrefix().IsMatch(domain) || domain.Contains('/', StringComparison.Ordinal))
        {
            return DomainHasSchemeOrPathMessage;
        }

        if (!DomainPattern().IsMatch(domain))
        {
            return $"「{domain}」不是有效的網域，例如 shop.example.com。";
        }

        if (existing.Contains(domain))
        {
            return $"「{domain}」已在允許清單中。";
        }

        return existing.Count >= AssistantWebsiteChannel.MaxAllowedDomains ? TooManyDomainsMessage : null;
    }

    /// <summary>
    /// The frontend's <c>validateWebsiteSettings()</c> for a full replace: every field is required
    /// (a <see langword="null"/> one counts as empty), every broken field is reported at once, and
    /// only the first broken domain is (the domains are checked in order, each against those
    /// before it).
    /// </summary>
    public static ValidationResult<WebsiteChannelSettings> ForUpdate(
        string? displayName,
        string? welcomeMessage,
        string? brandColor,
        string? position,
        IReadOnlyList<string?>? allowedDomains)
    {
        var failures = new List<ValidationFailure>();

        var trimmedName = (displayName ?? string.Empty).Trim();
        if (trimmedName.Length == 0)
        {
            failures.Add(new ValidationFailure(DisplayNameField, DisplayNameRequiredMessage));
        }
        else if (trimmedName.Length > AssistantWebsiteChannel.DisplayNameMaxLength)
        {
            failures.Add(new ValidationFailure(DisplayNameField, DisplayNameTooLongMessage));
        }

        var trimmedWelcome = (welcomeMessage ?? string.Empty).Trim();
        if (trimmedWelcome.Length == 0)
        {
            failures.Add(new ValidationFailure(WelcomeMessageField, WelcomeMessageRequiredMessage));
        }
        else if (trimmedWelcome.Length > AssistantWebsiteChannel.WelcomeMessageMaxLength)
        {
            failures.Add(new ValidationFailure(WelcomeMessageField, WelcomeMessageTooLongMessage));
        }

        if (!TryParse<WebsiteBrandColor>(brandColor, out var resolvedColor))
        {
            failures.Add(new ValidationFailure(BrandColorField, BrandColorInvalidMessage));
        }

        if (!TryParse<WebsiteLauncherPosition>(position, out var resolvedPosition))
        {
            failures.Add(new ValidationFailure(PositionField, PositionInvalidMessage));
        }

        var domains = new List<string>();
        foreach (var raw in allowedDomains ?? [])
        {
            if (ValidateDomain(raw, domains) is { } message)
            {
                failures.Add(new ValidationFailure(AllowedDomainsField, message));
                break;
            }

            domains.Add(NormalizeDomain(raw!));
        }

        return failures.Count > 0
            ? ValidationResult<WebsiteChannelSettings>.Invalid(failures)
            : ValidationResult<WebsiteChannelSettings>.Valid(
                new WebsiteChannelSettings(trimmedName, trimmedWelcome, resolvedColor, resolvedPosition, domains));
    }

    /// <summary>
    /// What <c>GET …/publishing/website</c> shows before anything is saved: the frontend mock's
    /// defaults (<c>publishing-channels.ts</c>), no domain.
    /// </summary>
    public static WebsiteChannelSettings Defaults(string assistantName)
    {
        ArgumentNullException.ThrowIfNull(assistantName);
        return new WebsiteChannelSettings(
            Truncate(assistantName, AssistantWebsiteChannel.DisplayNameMaxLength),
            Truncate($"您好，我是{assistantName}，有什麼可以協助？", AssistantWebsiteChannel.WelcomeMessageMaxLength),
            WebsiteBrandColor.Forest,
            WebsiteLauncherPosition.BottomRight,
            []);
    }

    /// <summary>
    /// The publishing gate (M5a plan §3 C, decision B): every reason publishing is refused right
    /// now, each under its own key — empty means it may be published. The acceptance status must be
    /// <see cref="AssistantAcceptanceStatus.Passed"/> <em>now</em> (an outdated one waits for the
    /// rerun), at least one domain is allowed, the assistant is not paused, every connected knowledge
    /// base is the owner's own (one message per other knowledge base, naming it), and the server
    /// knows its public URL (otherwise there is no embed code to paste).
    /// </summary>
    public static IReadOnlyList<ValidationFailure> PublishFailures(
        AssistantAcceptanceStatus acceptance,
        int allowedDomainCount,
        AssistantStatus assistantStatus,
        IReadOnlyList<WebsiteKnowledgeBaseRef> nonOwnedKnowledgeBases,
        bool publicBaseUrlConfigured)
    {
        ArgumentNullException.ThrowIfNull(nonOwnedKnowledgeBases);
        var failures = new List<ValidationFailure>();
        if (acceptance != AssistantAcceptanceStatus.Passed)
        {
            failures.Add(new ValidationFailure(AcceptanceField, AcceptanceNotPassedMessage));
        }

        if (allowedDomainCount == 0)
        {
            failures.Add(new ValidationFailure(AllowedDomainsGateField, NoAllowedDomainMessage));
        }

        if (assistantStatus == AssistantStatus.Paused)
        {
            failures.Add(new ValidationFailure(AssistantPausedField, AssistantPausedMessage));
        }

        failures.AddRange(nonOwnedKnowledgeBases.Select(knowledgeBase =>
            new ValidationFailure(KnowledgeOwnershipField, KnowledgeNotOwnedMessage(knowledgeBase.Name))));

        if (!publicBaseUrlConfigured)
        {
            failures.Add(new ValidationFailure(PublicBaseUrlField, PublicBaseUrlMissingMessage));
        }

        return failures;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static bool TryParse<TEnum>(string? wire, out TEnum value)
        where TEnum : struct, Enum
    {
        if (wire is not null && WireNames<TEnum>.All.Contains(wire))
        {
            value = WireNames<TEnum>.Parse(wire);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>The frontend's <c>/^[a-z]+:\/\//</c>.</summary>
    [GeneratedRegex("^[a-z]+://", RegexOptions.CultureInvariant)]
    private static partial Regex SchemePrefix();

    /// <summary>
    /// The frontend's <c>DOMAIN_PATTERN</c>, with <c>\z</c> for JavaScript's <c>$</c> (.NET's
    /// <c>$</c> also matches before a final newline) and an explicit ASCII class for each letter
    /// range (<see cref="RegexOptions.CultureInvariant"/>, no <c>IgnoreCase</c>).
    /// </summary>
    [GeneratedRegex(@"^(?=.{1,253}\z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
