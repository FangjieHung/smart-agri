using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// 成員 (table <c>CoordinatorMembers</c>, store-coordinator design §4, #372): a LINE user bound to one
/// shop (organization) through the platform's shared 統籌助理 LINE account, as 老闆 or 店長. One LINE
/// user may be bound to several organizations (one row each); within an organization a LINE user is
/// bound at most once.
/// </summary>
/// <remarks>
/// The LINE user id is the coordinator account's id of the person (LINE gives each official account
/// its own ids for the same person), kept so the coordinator knows whose message it is; nothing the
/// person writes is stored. Rows are created by binding with a <see cref="CoordinatorBindingCode"/>
/// only; removal and role changes are a later slice.
/// </remarks>
public sealed class CoordinatorMember : IOrganizationScoped
{
    /// <summary>Longest LINE user id stored (LINE's are <c>U</c> + 32 hexadecimal digits).</summary>
    public const int LineUserIdMaxLength = 64;

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorMember()
    {
        LineUserId = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The person's user id as the 統籌助理 LINE account sees it.</summary>
    public string LineUserId { get; private set; }

    public CoordinatorRole Role { get; private set; }

    public DateTimeOffset BoundAt { get; private set; }

    /// <summary>A new member of <paramref name="organizationId"/> with <paramref name="role"/>, bound
    /// <paramref name="now"/>.</summary>
    public static CoordinatorMember Bind(Guid organizationId, string lineUserId, CoordinatorRole role, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A member always belongs to an organization.", nameof(organizationId));
        }

        if (!IsValidLineUserId(lineUserId))
        {
            throw new ArgumentException($"A LINE user id is 1-{LineUserIdMaxLength} characters without white space.", nameof(lineUserId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Not a coordinator role.");
        }

        return new CoordinatorMember
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            LineUserId = lineUserId,
            Role = role,
            BoundAt = now,
        };
    }

    /// <summary>Whether <paramref name="lineUserId"/> can be stored as a LINE user id.</summary>
    public static bool IsValidLineUserId(string? lineUserId) =>
        !string.IsNullOrEmpty(lineUserId)
        && lineUserId.Length <= LineUserIdMaxLength
        && !lineUserId.Any(char.IsWhiteSpace);
}
