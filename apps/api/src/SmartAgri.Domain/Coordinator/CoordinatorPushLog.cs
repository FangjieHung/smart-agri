using System.Text.Json.Serialization;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>What a push of the 統籌助理 was for (store-reservations design §7, #390): each one counts against the
/// coordinator account's monthly message quota, which the platform bears.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoordinatorPushPurpose>))]
public enum CoordinatorPushPurpose
{
    /// <summary>A new reservation's card to one member of the shop (Slice R2).</summary>
    [JsonStringEnumMemberName("reservation")]
    Reservation,

    /// <summary>A reminder of a confirmed command's action, pushed when it is due (#375).</summary>
    [JsonStringEnumMemberName("reminder")]
    Reminder,

    /// <summary>The results of a command's automatic actions, when they could not be a reply (#375).</summary>
    [JsonStringEnumMemberName("follow-up")]
    FollowUp,

    /// <summary>A link to authorize FB or Google again, to an owner (#377).</summary>
    [JsonStringEnumMemberName("re-authorization")]
    Reauthorization,
}

/// <summary>
/// 推播紀錄 (table <c>CoordinatorPushLogs</c>, store-reservations design §3.1 point 7 and §7, #390): one row per push of the
/// 統籌助理 that LINE accepted — which shop it was for, when, what for and to how many people — so the operator can see
/// how each shop uses the coordinator account's monthly quota (<c>coordinator-quota</c>). Nothing about the recipients or
/// the message is kept; rows are deleted <see cref="RetentionDays"/> days after they were sent.
/// </summary>
public sealed class CoordinatorPushLog : IOrganizationScoped
{
    /// <summary>A row is kept this many days (a year and a month, to compare a month with the same one last year).</summary>
    public const int RetentionDays = 400;

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorPushLog()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>When LINE accepted the push (UTC).</summary>
    public DateTimeOffset SentAt { get; private set; }

    public CoordinatorPushPurpose Purpose { get; private set; }

    /// <summary>How many people it went to: what it counts against the quota (1 for a push; LINE does not count those
    /// who blocked the account).</summary>
    public int Recipients { get; private set; }

    /// <summary>A push of <paramref name="organizationId"/> for <paramref name="purpose"/> to <paramref name="recipients"/>
    /// people, accepted at <paramref name="sentAt"/>.</summary>
    public static CoordinatorPushLog Record(Guid organizationId, CoordinatorPushPurpose purpose, int recipients, DateTimeOffset sentAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A push is always for an organization.", nameof(organizationId));
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Not a push purpose.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(recipients, 1);
        return new CoordinatorPushLog
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            // Npgsql writes timestamptz only from UTC.
            SentAt = sentAt.ToUniversalTime(),
            Purpose = purpose,
            Recipients = recipients,
        };
    }
}
