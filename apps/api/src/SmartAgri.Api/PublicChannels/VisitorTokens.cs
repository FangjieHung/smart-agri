using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace SmartAgri.Api.PublicChannels;

/// <summary>What a valid visitor token says: a random visitor, for one assistant of one
/// organization, until <see cref="ExpiresAt"/>.</summary>
public sealed record VisitorTokenClaims(Guid VisitorId, Guid AssistantId, Guid OrganizationId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues and reads the website channel's visitor tokens (M5a plan §3 D, decision C): the payload
/// <c>{ visitorId, assistantId, organizationId, issuedAt, expiresAt }</c> protected with Data
/// Protection (<see cref="ITimeLimitedDataProtector"/>, a fixed purpose string), valid for
/// <see cref="Lifetime"/>. The visitor id is a fresh random GUID that is never stored anywhere — it
/// only tells one visitor's runs (and, from Slice 5, rate-limit partitions) apart.
/// </summary>
/// <remarks>
/// <para>
/// The key ring is the deployment's (#193, <c>DataProtection:KeysPath</c>), so tokens survive a
/// restart and stop working only when they expire or the key ring is replaced.
/// </para>
/// <para>
/// Expiry is checked twice: by the time-limited protector (against the system clock) and against
/// <see cref="TimeProvider"/> from the payload, so tests can move time forward. A token that fails
/// either check, does not decrypt (tampered, another deployment's, another purpose's) or does not
/// parse is simply invalid — <see cref="TryRead"/> never throws for its caller.
/// </para>
/// </remarks>
public sealed class VisitorTokens
{
    /// <summary>Decision C: a visitor token is valid for 12 hours.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>The Data Protection purpose; changing it invalidates every token issued.</summary>
    public const string Purpose = "SmartAgri.PublicChannels.VisitorToken.v1";

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _clock;

    public VisitorTokens(IDataProtectionProvider provider, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(clock);
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _clock = clock;
    }

    /// <summary>A new visitor's token for <paramref name="assistantId"/> and when it expires.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid assistantId, Guid organizationId)
    {
        var now = _clock.GetUtcNow();
        var issuedAt = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var expiresAt = issuedAt + Lifetime;
        var payload = new Payload(Guid.NewGuid(), assistantId, organizationId, issuedAt.ToUnixTimeSeconds(), expiresAt.ToUnixTimeSeconds());
        var token = _protector.Protect(JsonSerializer.Serialize(payload, PayloadJson), expiresAt);
        return (token, expiresAt);
    }

    /// <summary>The token's claims, or <see langword="null"/> when it is not a valid, unexpired
    /// visitor token of this deployment.</summary>
    public VisitorTokenClaims? TryRead(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token.Trim()), PayloadJson);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || payload.VisitorId == Guid.Empty
            || payload.AssistantId == Guid.Empty
            || payload.OrganizationId == Guid.Empty)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        if (_clock.GetUtcNow() >= expiresAt)
        {
            return null;
        }

        return new VisitorTokenClaims(
            payload.VisitorId, payload.AssistantId, payload.OrganizationId, DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt), expiresAt);
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] Guid VisitorId,
        [property: JsonPropertyName("a")] Guid AssistantId,
        [property: JsonPropertyName("o")] Guid OrganizationId,
        [property: JsonPropertyName("iat")] long IssuedAt,
        [property: JsonPropertyName("exp")] long ExpiresAt);
}
