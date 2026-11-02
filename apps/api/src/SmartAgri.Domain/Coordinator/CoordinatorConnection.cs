using System.Text.Json.Serialization;
using SmartAgri.Domain.Organizations;
using SmartAgri.Domain.Secrets;

namespace SmartAgri.Domain.Coordinator;

/// <summary>A service a shop connects so the 統籌助理 can act in it (store-coordinator design §5 Slices 5, 6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorService>))]
public enum CoordinatorService
{
    /// <summary>The shop's own LINE official account, for LINE 群發 (Slice 5, #376). Slice 6 adds <c>facebook</c> and
    /// <c>google</c>.</summary>
    [JsonStringEnumMemberName("line")]
    Line,
}

/// <summary>Whether a <see cref="CoordinatorConnection"/> can be used.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorConnectionStatus>))]
public enum CoordinatorConnectionStatus
{
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>The service refused the credential (wrong, reissued, revoked) or it could no longer be decrypted: the
    /// action becomes a reminder until the credential is set again.</summary>
    [JsonStringEnumMemberName("revoked")]
    Revoked,
}

/// <summary>
/// A shop's credential for one external service (table <c>CoordinatorConnections</c>, store-coordinator design §3 Q10,
/// Q18, §5 Slices 5 and 6, #376): at most one per organization and service. Slice 5 stores the shop's LINE official
/// account's long-lived channel access token, set by the platform operator with <c>coordinator-shop line-token</c>;
/// Slice 6 reuses the table for the FB and Google authorizations. Shop settings imports never touch it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credential is write-only.</b> <see cref="Secret"/> is a <see cref="ProtectedSecret"/> under
/// <see cref="PurposeOf"/>'s purpose: only the executor that calls the service decrypts it; nothing prints, logs or
/// returns it (<see cref="ToString"/> shows none of it).
/// </para>
/// <para>
/// <see cref="ExternalId"/> is whose credential it is in the service (for LINE the account's bot user id) and
/// <see cref="DisplayName"/> a label for people (for LINE 「{顯示名稱}（{basic id}）」). A service refusing the credential
/// marks the row <see cref="CoordinatorConnectionStatus.Revoked"/> (compare-and-set on <see cref="UpdatedAt"/>, so a
/// credential set again meanwhile stays active); setting it again makes it active.
/// </para>
/// </remarks>
public sealed class CoordinatorConnection : IOrganizationScoped
{
    /// <summary>Longest <see cref="ExternalId"/> (a LINE user id is <c>U</c> + 32 hexadecimal digits).</summary>
    public const int ExternalIdMaxLength = 64;

    /// <summary>Longest <see cref="DisplayName"/>; a longer label is shortened.</summary>
    public const int DisplayNameMaxLength = 100;

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorConnection()
    {
        Secret = null!;
        ExternalId = string.Empty;
        DisplayName = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public CoordinatorService Service { get; private set; }

    /// <summary>The encrypted credential (purpose <see cref="PurposeOf"/>).</summary>
    public ProtectedSecret Secret { get; private set; }

    /// <summary>Whose credential it is in the service: the LINE account's bot user id.</summary>
    public string ExternalId { get; private set; }

    /// <summary>A label for people: the LINE account's display name and basic id.</summary>
    public string DisplayName { get; private set; }

    public CoordinatorConnectionStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the credential was last set or the connection revoked.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The <c>ISecretProtector</c> purpose of <paramref name="service"/>'s credential.</summary>
    public static string PurposeOf(CoordinatorService service) => service switch
    {
        CoordinatorService.Line => "coordinator.line-access-token",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Not a coordinator service."),
    };

    /// <summary>A new, active connection of <paramref name="organizationId"/> to <paramref name="service"/>.</summary>
    public static CoordinatorConnection Create(
        Guid organizationId, CoordinatorService service, ProtectedSecret secret, string externalId, string displayName, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A connection belongs to an organization.", nameof(organizationId));
        }

        if (!Enum.IsDefined(service))
        {
            throw new ArgumentOutOfRangeException(nameof(service), service, "Not a coordinator service.");
        }

        var connection = new CoordinatorConnection
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Service = service,
            CreatedAt = now.ToUniversalTime(),
        };
        connection.Set(secret, externalId, displayName, now);
        return connection;
    }

    /// <summary>Replaces the credential (a new token, or the same one again after a revocation): active again.</summary>
    public void Set(ProtectedSecret secret, string externalId, string displayName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > ExternalIdMaxLength || externalId.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"An external id is 1-{ExternalIdMaxLength} characters without white space.", nameof(externalId));
        }

        ArgumentNullException.ThrowIfNull(displayName);
        var label = displayName.Trim();
        Secret = secret;
        ExternalId = externalId;
        DisplayName = label.Length <= DisplayNameMaxLength ? label : label[..DisplayNameMaxLength];
        Status = CoordinatorConnectionStatus.Active;
        UpdatedAt = now.ToUniversalTime();
    }

    /// <summary>Shows no part of the credential.</summary>
    public override string ToString() =>
        $"{nameof(CoordinatorConnection)} {{ OrganizationId = {OrganizationId}, Service = {Service}, Status = {Status}, UpdatedAt = {UpdatedAt:O} }}";
}
