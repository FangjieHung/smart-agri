using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Cases;

/// <summary>
/// One entry of a <see cref="Case"/>'s history — its 交接軌跡 (M7 plan §4). Append-only: created only
/// by <see cref="Case"/>'s own methods, which number the entries (<see cref="Ordinal"/>, unique per
/// case) so the history has one stable order. The fields after <see cref="Note"/> are the values an
/// action set; each action fills the ones it changes.
/// </summary>
public sealed class CaseEvent : IOrganizationScoped
{
    public const int NoteMaxLength = 2000;

    /// <summary>For EF Core materialization.</summary>
    private CaseEvent()
    {
    }

    internal CaseEvent(Case owner, int ordinal, CaseEventAction action, Guid? actorAccountId, DateTimeOffset at, string? note)
    {
        if (actorAccountId == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", nameof(actorAccountId));
        }

        Id = Guid.CreateVersion7();
        OrganizationId = owner.OrganizationId;
        CaseId = owner.Id;
        Ordinal = ordinal;
        Action = action;
        ActorAccountId = actorAccountId;
        At = at;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid CaseId { get; private set; }

    /// <summary>1-based position in the case's history.</summary>
    public int Ordinal { get; private set; }

    public CaseEventAction Action { get; private set; }

    /// <summary>Who did it; <see langword="null"/> for what no person did (the <c>created</c> event of a
    /// case opened from a database submission, decision M; M7-11's <c>case-set-due</c>).</summary>
    public Guid? ActorAccountId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public string? Note { get; private set; }

    /// <summary>The status afterwards (<c>created</c> and every status change).</summary>
    public CaseStatus? Status { get; internal set; }

    /// <summary>For <see cref="CaseEventAction.Accepted"/>: the new case owner.</summary>
    public Guid? OwnerAccountId { get; internal set; }

    /// <summary>For <see cref="CaseEventAction.Transferred"/>: the group it left.</summary>
    public Guid? FromGroupId { get; internal set; }

    /// <summary>For <see cref="CaseEventAction.Created"/> and <see cref="CaseEventAction.Transferred"/>: the group afterwards.</summary>
    public Guid? ToGroupId { get; internal set; }

    /// <summary>For <see cref="CaseEventAction.Created"/> and <see cref="CaseEventAction.DueChanged"/>: the due time afterwards.</summary>
    public DateTimeOffset? DueAt { get; internal set; }
}
