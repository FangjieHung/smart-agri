using System.Net.Mail;

namespace SmartAgri.Infrastructure.Notifications;

/// <summary>
/// Configuration section <c>Smtp</c> (#355): the mail server notification Emails go through
/// (<c>deploy/.env</c>'s <c>SMTP_*</c>). Optional: without <see cref="Host"/> Email notifications are
/// off and everything else works as before. Every value is nullable so that the blank values compose
/// passes for an unset <c>.env</c> entry bind as unset instead of failing startup.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public const int DefaultPort = 587;

    public const int DefaultTimeoutSeconds = 30;

    /// <summary>The SMTP server, e.g. <c>smtp.gmail.com</c>. Blank: Email notifications are off.</summary>
    public string? Host { get; set; }

    /// <summary>The submission port; <see cref="DefaultPort"/> (STARTTLS) when unset. Port 465 (TLS from
    /// the first byte, "SMTPS") is not supported by .NET's client and refuses to start.</summary>
    public int? Port { get; set; }

    /// <summary>The account to sign in with; together with <see cref="Password"/>, or neither (a relay
    /// that accepts this server without signing in).</summary>
    public string? Username { get; set; }

    /// <summary>For Gmail an app password (Google account → Security → App passwords), never the
    /// account's own password. Never logged.</summary>
    public string? Password { get; set; }

    /// <summary>The sender address, e.g. <c>notify@example.org</c> (for Gmail, the signed-in address).</summary>
    public string? From { get; set; }

    /// <summary>The sender's display name; defaults to 「AI 助理工作台」 (the admin's title).</summary>
    public string? FromName { get; set; }

    /// <summary>Upgrade the connection with STARTTLS before signing in (default on). Off only for a
    /// relay on a trusted network; with a password, only in Development and Testing.</summary>
    public bool? UseStartTls { get; set; }

    /// <summary>How long one send may take; <see cref="DefaultTimeoutSeconds"/> when unset.</summary>
    public int? TimeoutSeconds { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public int EffectivePort => Port ?? DefaultPort;

    public bool EffectiveUseStartTls => UseStartTls ?? true;

    public TimeSpan EffectiveTimeout => TimeSpan.FromSeconds(TimeoutSeconds ?? DefaultTimeoutSeconds);

    public string EffectiveFromName => string.IsNullOrWhiteSpace(FromName) ? "AI 助理工作台" : FromName.Trim();

    public bool HasCredentials => !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password);

    /// <summary>Why this configuration may not start in <paramref name="environmentName"/>, or
    /// <see langword="null"/>. Nothing is checked while <see cref="Host"/> is blank.</summary>
    public string? Validate(string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        if (!IsConfigured)
        {
            return null;
        }

        if (Host!.Trim().Any(character => char.IsWhiteSpace(character) || character is '/' or ':'))
        {
            return $"{SectionName}:{nameof(Host)} must be a host name such as smtp.gmail.com (no scheme, port or path).";
        }

        if (EffectivePort is < 1 or > 65535)
        {
            return $"{SectionName}:{nameof(Port)} must be 1-65535 (usually 587).";
        }

        if (EffectivePort == 465)
        {
            return $"{SectionName}:{nameof(Port)} 465 (SMTPS) is not supported: use 587 with STARTTLS, which Gmail and most providers offer.";
        }

        if (string.IsNullOrWhiteSpace(From) || !MailAddress.TryCreate(From.Trim(), out var from) || from.Address != From.Trim())
        {
            return $"{SectionName}:{nameof(From)} must be a plain sender address such as notify@example.org when {SectionName}:{nameof(Host)} is set.";
        }

        if (string.IsNullOrEmpty(Username) != string.IsNullOrEmpty(Password))
        {
            return $"{SectionName}:{nameof(Username)} and {SectionName}:{nameof(Password)} must be set together (or both left empty for a relay without sign-in).";
        }

        if (TimeoutSeconds is < 1 or > 300)
        {
            return $"{SectionName}:{nameof(TimeoutSeconds)} must be 1-300.";
        }

        var development = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        if (!EffectiveUseStartTls && HasCredentials && !development)
        {
            return $"{SectionName}:{nameof(UseStartTls)} must stay on when a password is set: it would be sent unencrypted.";
        }

        return null;
    }
}
