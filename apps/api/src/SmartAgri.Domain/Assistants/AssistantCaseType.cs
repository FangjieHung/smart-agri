using SmartAgri.Domain.Cases;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One case type an assistant may propose in a conversation (table <c>AssistantCaseTypes</c>, M7 plan
/// §4, decision U; issue #254) — the case-type counterpart of <see cref="AssistantDatabase"/>. The
/// assistant's owner chooses among the organization's active types; none by default. The database
/// enforces that both sides belong to <see cref="OrganizationId"/> (two composite foreign keys); the
/// row goes when the assistant is deleted.
/// </summary>
/// <remarks>
/// A row is necessary but not sufficient: every proposal re-checks, for <b>that</b> request, that the
/// type is still active (a deactivated type keeps its row, so reactivating it brings it back) and
/// that the asker is an internal account.
/// </remarks>
public sealed class AssistantCaseType : IOrganizationScoped
{
    /// <summary>For EF Core materialization.</summary>
    private AssistantCaseType()
    {
    }

    public AssistantCaseType(Assistant assistant, CaseType caseType, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(caseType);
        if (assistant.OrganizationId != caseType.OrganizationId)
        {
            throw new ArgumentException("An assistant may only propose its own organization's case types.", nameof(caseType));
        }

        AssistantId = assistant.Id;
        CaseTypeId = caseType.Id;
        OrganizationId = assistant.OrganizationId;
        CreatedAt = createdAt;
    }

    public Guid AssistantId { get; private set; }

    public Guid CaseTypeId { get; private set; }

    /// <summary>Always the assistant's (and the case type's) organization.</summary>
    public Guid OrganizationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
