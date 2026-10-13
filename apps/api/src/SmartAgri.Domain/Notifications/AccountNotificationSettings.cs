using System.Net.Mail;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Notifications;

/// <summary>
/// One account's own notification settings (table <c>AccountNotificationSettings</c>, #355): the
/// address its notification Emails go to, and whether it wants the 轉真人 Email at all. Accounts sign
/// in with a login name and have no address otherwise, so without a row (or without an address) no
/// Email is sent; the in-app notification is unaffected either way.
/// </summary>
public sealed class AccountNotificationSettings : IOrganizationScoped
{
    /// <summary>RFC 5321's limit on a forward path.</summary>
    public const int EmailMaxLength = 254;

    public const string EmailInvalidMessage = "請輸入有效的 Email 地址，例如 name@example.com。";

    /// <summary>For EF Core materialization.</summary>
    private AccountNotificationSettings()
    {
    }

    public Guid AccountId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Where notification Emails go; <see langword="null"/> for none.</summary>
    public string? Email { get; private set; }

    /// <summary>Whether a 轉真人 Email is sent to <see cref="Email"/>. On unless the account opts out.</summary>
    public bool HandoffEmails { get; private set; } = true;

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>An account with no row yet: no address, 轉真人 Emails on.</summary>
    public static AccountNotificationSettings CreateDefault(Guid organizationId, Guid accountId, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Settings always belong to an organization.", nameof(organizationId));
        }

        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Settings always belong to an account.", nameof(accountId));
        }

        return new AccountNotificationSettings
        {
            AccountId = accountId,
            OrganizationId = organizationId,
            HandoffEmails = true,
            UpdatedAt = now,
        };
    }

    /// <summary>Whether a 轉真人 Email goes to this account, and where.</summary>
    public string? HandoffEmailAddress => HandoffEmails ? Email : null;

    /// <summary>Applies the account's own choice; <paramref name="email"/> is normalized by
    /// <see cref="TryNormalizeEmail"/> (blank clears it).</summary>
    /// <exception cref="ArgumentException">The address is not valid.</exception>
    public void Update(string? email, bool handoffEmails, DateTimeOffset now)
    {
        if (!TryNormalizeEmail(email, out var normalized))
        {
            throw new ArgumentException(EmailInvalidMessage, nameof(email));
        }

        Email = normalized;
        HandoffEmails = handoffEmails;
        UpdatedAt = now;
    }

    /// <summary>
    /// Blank → <see langword="null"/> (no address). Otherwise the trimmed text must be one plain
    /// <c>local@domain</c> address of at most <see cref="EmailMaxLength"/> characters — no display name,
    /// no list, no line breaks — whose domain has a dot.
    /// </summary>
    public static bool TryNormalizeEmail(string? email, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(email))
        {
            return true;
        }

        var trimmed = email.Trim();
        if (trimmed.Length > EmailMaxLength
            || trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || trimmed.IndexOfAny([',', ';', '<', '>', '"']) >= 0
            || !MailAddress.TryCreate(trimmed, out var address)
            || !string.Equals(address.Address, trimmed, StringComparison.Ordinal)
            || address.DisplayName.Length > 0
            || !address.Host.Contains('.', StringComparison.Ordinal)
            || address.Host.StartsWith('.') || address.Host.EndsWith('.'))
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }
}
