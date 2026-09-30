using SmartAgri.Domain.Organizations;

namespace SmartAgri.Domain.Assistants;

/// <summary>
/// One saved question of an assistant's test set (題組; M3.5 plan §4, issue #123): common,
/// exception or should-refuse, with the reply kind and cited documents it expects. Owned by one
/// <see cref="Assistant"/> (deleted with it); at most
/// <see cref="SmartAgri.Application.Assistants.AssistantTestCaseRules.MaxCount"/> per assistant,
/// enforced by the caller before construction.
/// </summary>
/// <remarks>
/// A plain POCO: EF Core mapping lives in <c>SmartAgri.Infrastructure.Assistants</c>. All
/// user-facing validation (lengths, which documents may be named, which question a follow-up may
/// point at) is <c>SmartAgri.Application.Assistants.AssistantTestCaseRules</c>, which always runs
/// first; the guards here are invariants only.
/// </remarks>
public sealed class AssistantTestCase : IOrganizationScoped
{
    public const int QuestionMaxLength = 2000;

    /// <summary>For EF Core materialization.</summary>
    private AssistantTestCase()
    {
    }

    public AssistantTestCase(
        Guid organizationId,
        Guid assistantId,
        string question,
        AssistantTestCaseCategory category,
        AssistantTestExpectedKind expectedKind,
        IReadOnlyList<Guid> expectedDocumentIds,
        Guid? followUpOfId,
        int ordinal,
        DateTimeOffset now)
    {
        RequireId(organizationId, nameof(organizationId));
        RequireId(assistantId, nameof(assistantId));

        Id = Guid.CreateVersion7();
        OrganizationId = organizationId;
        AssistantId = assistantId;
        Ordinal = ordinal;
        CreatedAt = now;
        Apply(question, category, expectedKind, expectedDocumentIds, followUpOfId, now);
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AssistantId { get; private set; }

    public string Question { get; private set; } = string.Empty;

    public AssistantTestCaseCategory Category { get; private set; }

    /// <summary>The reply kind a passing run must produce.</summary>
    public AssistantTestExpectedKind ExpectedKind { get; private set; }

    /// <summary>Documents a <see cref="AssistantTestExpectedKind.CompanyData"/> answer must
    /// cite; always empty for the other expected kinds.</summary>
    public IReadOnlyList<Guid> ExpectedDocumentIds { get; private set; } = [];

    /// <summary>Another test case of the same assistant this one follows up on, if any.</summary>
    public Guid? FollowUpOfId { get; private set; }

    /// <summary>Display order within the assistant; not required to be contiguous.</summary>
    public int Ordinal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Applies every field at once (all validated and trimmed by the caller — see
    /// <c>AssistantTestCaseRules</c>).</summary>
    public void Apply(
        string question,
        AssistantTestCaseCategory category,
        AssistantTestExpectedKind expectedKind,
        IReadOnlyList<Guid> expectedDocumentIds,
        Guid? followUpOfId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(expectedDocumentIds);

        var trimmed = question.Trim();
        if (trimmed.Length is 0 or > QuestionMaxLength)
        {
            throw new ArgumentException($"A test case question must be 1-{QuestionMaxLength} characters.", nameof(question));
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Not a declared category.");
        }

        if (!Enum.IsDefined(expectedKind))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedKind), expectedKind, "Not a declared expected kind.");
        }

        Question = trimmed;
        Category = category;
        ExpectedKind = expectedKind;
        ExpectedDocumentIds = expectedDocumentIds.Distinct().ToList();
        FollowUpOfId = followUpOfId;
        UpdatedAt = now;
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An id must not be empty.", parameterName);
        }
    }
}
