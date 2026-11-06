using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>Why an <see cref="CoordinatorAuthorizationLink"/> was made.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorAuthorizationPurpose>))]
public enum CoordinatorAuthorizationPurpose
{
    /// <summary>The platform operator made it (<c>coordinator-shop auth-link</c>).</summary>
    [JsonStringEnumMemberName("initial")]
    Initial,

    /// <summary>The service refused the stored credential: the 統籌助理 pushed it to the shop's owners.</summary>
    [JsonStringEnumMemberName("reauthorize")]
    Reauthorize,
}

/// <summary>
/// 授權連結 (table <c>CoordinatorAuthorizationLinks</c>, store-coordinator design §3 Q12, §5 Slice 6, #377): a single-use
/// link the shop's owner opens to authorize the 統籌助理 in FB or Google — <c>{public base URL}/api/v1/coordinator/connect?token={token}</c>.
/// Only <see cref="TokenHash"/> (<see cref="AuthorizationLinkTokens.Hash"/>) is stored; the link itself is shown once
/// (the operator's terminal, or the owner's LINE chat).
/// </summary>
/// <remarks>
/// Opening the link only redirects to the service's consent screen; the service's callback takes it — the first one
/// marks it used (<see cref="UsedAt"/>) with a compare-and-set, so one link authorizes at most once. A link of
/// <see cref="CoordinatorService.Line"/> does not exist: that token is set by the operator.
/// </remarks>
public sealed class CoordinatorAuthorizationLink : IOrganizationScoped
{
    /// <summary>Length of <see cref="TokenHash"/> (SHA-256 in hexadecimal).</summary>
    public const int TokenHashLength = 64;

    /// <summary>How long a link is valid unless the command says otherwise (3 days); also a re-authorization link's.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromHours(72);

    /// <summary>The longest validity a link may have (7 days).</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromHours(168);

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorAuthorizationLink()
    {
        TokenHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>What the owner authorizes: <see cref="CoordinatorService.Facebook"/> or <see cref="CoordinatorService.Google"/>.</summary>
    public CoordinatorService Service { get; private set; }

    public CoordinatorAuthorizationPurpose Purpose { get; private set; }

    /// <summary><see cref="AuthorizationLinkTokens.Hash"/> of the link's token; unique across all organizations.</summary>
    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the service's callback took it; <see langword="null"/> while unused.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Whether <paramref name="service"/> is authorized through links (FB and Google; not LINE).</summary>
    public static bool IsLinkService(CoordinatorService service) =>
        service is CoordinatorService.Facebook or CoordinatorService.Google;

    /// <summary>A new, unused link of <paramref name="organizationId"/> for <paramref name="service"/>, valid from
    /// <paramref name="now"/> for <paramref name="validity"/> (more than zero, at most <see cref="MaxValidity"/>).</summary>
    public static CoordinatorAuthorizationLink Issue(
        Guid organizationId,
        CoordinatorService service,
        CoordinatorAuthorizationPurpose purpose,
        string tokenHash,
        DateTimeOffset now,
        TimeSpan validity)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An authorization link always belongs to an organization.", nameof(organizationId));
        }

        if (!IsLinkService(service))
        {
            throw new ArgumentOutOfRangeException(nameof(service), service, "Only FB and Google are authorized through links.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Not an authorization link purpose.");
        }

        if (tokenHash is not { Length: TokenHashLength } || !tokenHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A token hash is SHA-256 in hexadecimal.", nameof(tokenHash));
        }

        if (validity <= TimeSpan.Zero || validity > MaxValidity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validity), validity, $"An authorization link is valid for more than zero and at most {MaxValidity.TotalHours} hours.");
        }

        var created = now.ToUniversalTime();
        return new CoordinatorAuthorizationLink
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Service = service,
            Purpose = purpose,
            TokenHash = tokenHash.ToLowerInvariant(),
            CreatedAt = created,
            ExpiresAt = created + validity,
        };
    }

    /// <summary>Unused and not expired at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;

    /// <summary>Shows no part of the token.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorAuthorizationLink)} {{ Id = {Id}, OrganizationId = {OrganizationId}, Service = {Service}, Purpose = {Purpose}, ExpiresAt = {ExpiresAt:O}, Used = {UsedAt is not null} }}";
}

/// <summary>
/// The tokens of <see cref="CoordinatorAuthorizationLink"/>s (#377): 32 bytes from a cryptographic random number
/// generator in base64url without padding (<see cref="Length"/> characters). Only <see cref="Hash"/> is stored.
/// </summary>
public static class AuthorizationLinkTokens
{
    /// <summary>Characters of a token (32 bytes in base64url, no padding).</summary>
    public const int Length = 43;

    /// <summary>A new random token.</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The stored form of <paramref name="token"/>: SHA-256 of its ASCII bytes, as 64 lower-case hexadecimal
    /// digits.</summary>
    public static string Hash(string token)
    {
        if (!IsToken(token))
        {
            throw new ArgumentException("Not an authorization link token.", nameof(token));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    /// <summary>Exactly <see cref="Length"/> characters of base64url.</summary>
    public static bool IsToken(string? token) =>
        token is { Length: Length }
        && token.All(character => character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');
}
