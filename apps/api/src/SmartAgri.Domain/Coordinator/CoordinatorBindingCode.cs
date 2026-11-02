using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// 綁定碼 (table <c>CoordinatorBindingCodes</c>, store-coordinator design §4, #372): a single-use code
/// that binds whoever sends it to the 統籌助理 LINE account as a <see cref="CoordinatorMember"/> of one
/// organization, with the role it was issued for. A 老闆 code is issued by the platform operator
/// (<c>coordinator-binding-code</c>, <see cref="IssuedByLineUserId"/> empty); a 店長 code by a 老闆 in
/// LINE. Only <see cref="CodeHash"/> (<see cref="BindingCodes.Hash"/>) is stored.
/// </summary>
/// <remarks>
/// A code is usable until <see cref="ExpiresAt"/> and only once: the first bind marks it used
/// (<see cref="UsedAt"/> together with <see cref="UsedByLineUserId"/>, enforced by a check constraint)
/// with a compare-and-set, so of two people sending the same code exactly one is bound.
/// </remarks>
public sealed class CoordinatorBindingCode : IOrganizationScoped
{
    /// <summary>Length of <see cref="CodeHash"/> (SHA-256 in hexadecimal).</summary>
    public const int CodeHashLength = 64;

    /// <summary>How long an operator's 老闆 code is valid unless the command says otherwise (7 days).</summary>
    public static readonly TimeSpan OwnerDefaultValidity = TimeSpan.FromHours(168);

    /// <summary>The longest validity any code may have (30 days).</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromHours(720);

    /// <summary>How long a 店長 code a 老闆 asks for in LINE is valid (1 day).</summary>
    public static readonly TimeSpan ManagerValidity = TimeSpan.FromHours(24);

    /// <summary>For EF Core materialization.</summary>
    private CoordinatorBindingCode()
    {
        CodeHash = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary><see cref="BindingCodes.Hash"/> of the code; unique across all organizations.</summary>
    public string CodeHash { get; private set; }

    /// <summary>The role whoever binds with the code gets.</summary>
    public CoordinatorRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When someone was bound with it; <see langword="null"/> while unused.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Who was bound with it; set exactly when <see cref="UsedAt"/> is.</summary>
    public string? UsedByLineUserId { get; private set; }

    /// <summary>The 老闆 who asked for it in LINE, or <see langword="null"/> when the platform operator
    /// issued it with the command-line tool.</summary>
    public string? IssuedByLineUserId { get; private set; }

    /// <summary>A new, unused code for <paramref name="organizationId"/>, valid from
    /// <paramref name="now"/> for <paramref name="validity"/> (more than zero, at most
    /// <see cref="MaxValidity"/>).</summary>
    public static CoordinatorBindingCode Issue(
        Guid organizationId,
        string codeHash,
        CoordinatorRole role,
        DateTimeOffset now,
        TimeSpan validity,
        string? issuedByLineUserId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A binding code always belongs to an organization.", nameof(organizationId));
        }

        if (codeHash is not { Length: CodeHashLength } || !codeHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A code hash is SHA-256 in hexadecimal.", nameof(codeHash));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Not a coordinator role.");
        }

        if (validity <= TimeSpan.Zero || validity > MaxValidity)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), validity, $"A binding code is valid for more than zero and at most {MaxValidity.TotalHours} hours.");
        }

        if (issuedByLineUserId is not null && !CoordinatorMember.IsValidLineUserId(issuedByLineUserId))
        {
            throw new ArgumentException("Not a LINE user id.", nameof(issuedByLineUserId));
        }

        return new CoordinatorBindingCode
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            CodeHash = codeHash.ToLowerInvariant(),
            Role = role,
            CreatedAt = now,
            ExpiresAt = now + validity,
            IssuedByLineUserId = issuedByLineUserId,
        };
    }

    /// <summary>Unused and not expired at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;
}
