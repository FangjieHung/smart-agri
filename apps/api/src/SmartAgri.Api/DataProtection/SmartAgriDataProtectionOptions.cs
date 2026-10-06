namespace SmartAgri.Api.DataProtection;

/// <summary>
/// Configuration section <c>DataProtection</c>: where the ASP.NET Core Data Protection key ring
/// lives and which certificate encrypts it. See <c>apps/api/README.md</c> ("Data Protection key
/// ring") for what the key ring protects and how to back it up.
/// </summary>
public sealed class SmartAgriDataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Directory the key ring files are written to; outside the database and kept across container
    /// rebuilds (the customer compose file mounts a named volume at <c>/app/keys</c>). Required
    /// outside <c>Development</c> and <c>Testing</c>; the Api refuses to start without it.
    /// </summary>
    public string? KeysPath { get; set; }

    /// <summary>PKCS#12 (<c>.pfx</c>) certificate whose key encrypts the key ring files. Required
    /// outside <c>Development</c> and <c>Testing</c>.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }
}
