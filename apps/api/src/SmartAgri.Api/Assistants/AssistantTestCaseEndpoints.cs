using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>One row of <c>GET/POST/PATCH /api/v1/assistants/{id}/test-cases</c>.</summary>
public sealed record AssistantTestCaseView(
    Guid Id,
    Guid AssistantId,
    string Question,
    AssistantTestCaseCategory Category,
    AssistantTestExpectedKind ExpectedKind,
    IReadOnlyList<Guid> ExpectedDocumentIds,
    Guid? FollowUpOfId,
    int Ordinal,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary><c>POST /api/v1/assistants/{id}/test-cases</c> request. <see cref="Category"/> and
/// <see cref="ExpectedKind"/> are plain strings (as in <c>UpdateAssistantSettingsRequest</c>): an
/// unknown value is this endpoint's own <c>422</c>, not a model-binding failure.</summary>
public sealed record CreateAssistantTestCaseRequest(
    string? Question,
    string? Category,
    string? ExpectedKind,
    IReadOnlyList<Guid>? ExpectedDocumentIds,
    Guid? FollowUpOfId);

/// <summary><c>PATCH .../test-cases/{caseId}</c> request: a <see langword="null"/> field is left
/// unchanged. <see cref="FollowUpOfId"/> cannot be cleared back to <see langword="null"/> once
/// set through this endpoint (out of this slice's scope) — only replaced with another id.</summary>
public sealed record UpdateAssistantTestCaseRequest(
    string? Question = null,
    string? Category = null,
    string? ExpectedKind = null,
    IReadOnlyList<Guid>? ExpectedDocumentIds = null,
    Guid? FollowUpOfId = null);

/// <summary>One question of an import/export batch, shaped like <c>eval-answers</c>'
/// <c>questions.json</c> (<c>AnswerEvalSet</c>) — <see cref="ExpectedCitedDocuments"/> names
/// documents by their file name, not by id, so a bank can move between assistants and
/// organizations. <see cref="Category"/> defaults to <c>common</c> when absent, since the
/// eval-answers format does not have it.</summary>
public sealed record AssistantTestCaseImportEntry(
    string? Id,
    string? Question,
    string? Category,
    string? ExpectedKind,
    IReadOnlyList<string>? ExpectedCitedDocuments,
    string? FollowUpOf);

/// <summary><c>POST /api/v1/assistants/{id}/test-cases/import</c> request.</summary>
public sealed record ImportAssistantTestCasesRequest(IReadOnlyList<AssistantTestCaseImportEntry>? Questions);

/// <summary>One exported question; the same shape as <see cref="AssistantTestCaseImportEntry"/>
/// but every field filled in, so exporting and re-importing round-trips.</summary>
public sealed record AssistantTestCaseExportEntry(
    string Id,
    string Question,
    string Category,
    string ExpectedKind,
    IReadOnlyList<string> ExpectedCitedDocuments,
    string? FollowUpOf);

/// <summary><c>GET /api/v1/assistants/{id}/test-cases/export</c> response.</summary>
public sealed record ExportAssistantTestCasesResponse(IReadOnlyList<AssistantTestCaseExportEntry> Questions);

/// <summary>
/// An assistant's saved test set (題組; M3.5 plan §4/§5 Slice 1, issue #123): CRUD, plus bulk
/// import/export in the <c>eval-answers</c> question-bank shape. Same access rule as the
/// assistant's own settings (<c>S+MA+OWN</c>, <see cref="ForbiddenReason.AssistantConfiguration"/>):
/// an assistant id that does not exist, belongs to another organization or is not the caller's own
/// gets the exact same <c>403</c> as <c>AssistantEndpoints</c>.
/// </summary>
public static class AssistantTestCaseEndpoints
{
    public static IEndpointRouteBuilder MapAssistantTestCaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var testCases = endpoints.MapGroup("/api/v1/assistants/{id:guid}/test-cases")
            .RequireAuthorization()
            .RequirePermission(AccountPermission.ManageAssistants, ForbiddenReason.AssistantConfiguration);

        testCases.MapGet("", ListAsync)
            .Produces<IReadOnlyList<AssistantTestCaseView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        testCases.MapPost("", CreateAsync)
            .Produces<AssistantTestCaseView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        testCases.MapPatch("/{caseId:guid}", UpdateAsync)
            .Produces<AssistantTestCaseView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        testCases.MapDelete("/{caseId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        // Plain path segments (not "test-cases:import"/"test-cases:export"), matching this
        // codebase's existing custom-action convention (e.g. KnowledgeReviewEndpoints'
        // "/documents/{id}/disable"), rather than the plan doc's Google-style shorthand.
        testCases.MapPost("/import", ImportAsync)
            .Produces<IReadOnlyList<AssistantTestCaseView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        testCases.MapGet("/export", ExportAsync)
            .Produces<ExportAssistantTestCasesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    internal static async Task<IResult> ListAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var cases = await OrderedAsync(dbContext, assistant.Id, cancellationToken);
        return Results.Ok(cases.ConvertAll(ToView));
    }

    internal static async Task<IResult> CreateAsync(
        Guid id,
        CreateAssistantTestCaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var existing = await dbContext.AssistantTestCases.AsNoTracking()
            .Where(testCase => testCase.AssistantId == assistant.Id)
            .Select(testCase => new { testCase.Id, testCase.Ordinal })
            .ToListAsync(cancellationToken);
        var connectedDocumentIds = await ConnectedDocumentIdsAsync(dbContext, assistant.Id, cancellationToken);

        var validated = AssistantTestCaseRules.ForCreate(
            existing.Count,
            request.Question,
            request.Category,
            request.ExpectedKind,
            request.ExpectedDocumentIds,
            request.FollowUpOfId,
            connectedDocumentIds,
            existing.Select(testCase => testCase.Id).ToHashSet());
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        var value = validated.Value;
        var now = clock.GetUtcNow();
        var nextOrdinal = existing.Count == 0 ? 1 : existing.Max(testCase => testCase.Ordinal) + 1;
        var created = new AssistantTestCase(
            assistant.OrganizationId, assistant.Id, value.Question, value.Category, value.ExpectedKind,
            value.ExpectedDocumentIds, value.FollowUpOfId, nextOrdinal, now);
        dbContext.AssistantTestCases.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/v1/assistants/{assistant.Id}/test-cases/{created.Id}", ToView(created));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id,
        Guid caseId,
        UpdateAssistantTestCaseRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var testCase = await dbContext.AssistantTestCases
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id && candidate.Id == caseId, cancellationToken);
        if (testCase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var connectedDocumentIds = await ConnectedDocumentIdsAsync(dbContext, assistant.Id, cancellationToken);
        var otherCaseIds = await dbContext.AssistantTestCases.AsNoTracking()
            .Where(candidate => candidate.AssistantId == assistant.Id && candidate.Id != caseId)
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken);

        var current = new AssistantTestCaseDetails(
            testCase.Question, testCase.Category, testCase.ExpectedKind, testCase.ExpectedDocumentIds, testCase.FollowUpOfId);
        var validated = AssistantTestCaseRules.ForUpdate(
            current,
            request.Question,
            request.Category,
            request.ExpectedKind,
            request.ExpectedDocumentIds,
            request.FollowUpOfId,
            connectedDocumentIds,
            otherCaseIds.ToHashSet());
        if (!validated.IsValid)
        {
            return ApiErrors.ValidationFailed(validated.Failures);
        }

        var value = validated.Value;
        testCase.Apply(value.Question, value.Category, value.ExpectedKind, value.ExpectedDocumentIds, value.FollowUpOfId, clock.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(ToView(testCase));
    }

    internal static async Task<IResult> DeleteAsync(
        Guid id,
        Guid caseId,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var testCase = await dbContext.AssistantTestCases
            .SingleOrDefaultAsync(candidate => candidate.AssistantId == assistant.Id && candidate.Id == caseId, cancellationToken);
        if (testCase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        dbContext.AssistantTestCases.Remove(testCase);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Imports a batch in the <c>eval-answers</c> question-bank shape. Atomic: any row's failure
    /// (unknown document name, invalid category/expectedKind, a <c>followUpOf</c> that is not an
    /// earlier row of the same batch or an existing test case, going over
    /// <see cref="AssistantTestCaseRules.MaxCount"/>) writes nothing at all. A row's
    /// <c>followUpOf</c> may only reference an earlier row's own <c>id</c> in the same batch (or
    /// an id already saved for this assistant) — never a later one — mirroring
    /// <c>AnswerEvalSet</c>'s own rule for its question bank.
    /// </summary>
    internal static async Task<IResult> ImportAsync(
        Guid id,
        ImportAssistantTestCasesRequest request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var entries = request.Questions ?? [];
        if (entries.Count == 0)
        {
            return ApiErrors.ValidationFailed(
                "請至少匯入一個題目。", new Dictionary<string, string[]> { ["questions"] = ["請至少匯入一個題目。"] });
        }

        var existingIds = await dbContext.AssistantTestCases.AsNoTracking()
            .Where(testCase => testCase.AssistantId == assistant.Id)
            .Select(testCase => testCase.Id)
            .ToListAsync(cancellationToken);
        if (existingIds.Count + entries.Count > AssistantTestCaseRules.MaxCount)
        {
            return ApiErrors.ValidationFailed(
                AssistantTestCaseRules.TooManyTestCasesMessage,
                new Dictionary<string, string[]> { ["questions"] = [AssistantTestCaseRules.TooManyTestCasesMessage] });
        }

        var startOrdinal = existingIds.Count == 0
            ? 1
            : (await dbContext.AssistantTestCases.AsNoTracking()
                .Where(testCase => testCase.AssistantId == assistant.Id)
                .MaxAsync(testCase => (int?)testCase.Ordinal, cancellationToken) ?? 0) + 1;

        var connectedKnowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistant.Id, cancellationToken);
        var documents = await dbContext.KnowledgeDocuments.AsNoTracking()
            .Where(document => connectedKnowledgeBaseIds.Contains(document.KnowledgeBaseId))
            .Select(document => new { document.Id, document.Name })
            .ToListAsync(cancellationToken);
        var documentIdByName = documents
            .GroupBy(document => document.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        var connectedDocumentIds = documents.Select(document => document.Id).ToHashSet();

        var existingIdSet = existingIds.ToHashSet();
        var idsByLabel = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var created = new List<AssistantTestCase>();
        var now = clock.GetUtcNow();
        var ordinal = startOrdinal;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var where = $"questions[{i}]";
            var rowErrors = new List<string>();

            var documentIds = new List<Guid>();
            foreach (var name in entry.ExpectedCitedDocuments ?? [])
            {
                if (documentIdByName.TryGetValue(name, out var documentId))
                {
                    documentIds.Add(documentId);
                }
                else
                {
                    rowErrors.Add($"找不到文件「{name}」，或它的名稱在已連接的知識庫中重複，無法判斷是哪一份。");
                }
            }

            Guid? followUpOfId = null;
            if (!string.IsNullOrWhiteSpace(entry.FollowUpOf))
            {
                var label = entry.FollowUpOf.Trim();
                if (idsByLabel.TryGetValue(label, out var resolved))
                {
                    followUpOfId = resolved;
                }
                else if (Guid.TryParse(label, out var asGuid) && existingIdSet.Contains(asGuid))
                {
                    followUpOfId = asGuid;
                }
                else
                {
                    rowErrors.Add("followUpOf 必須是題庫裡在它之前的題目 id，或這個助理已存在的題目 id。");
                }
            }

            var effectiveExistingIds = followUpOfId is { } resolvedFollowUp
                ? new HashSet<Guid>(existingIdSet) { resolvedFollowUp }
                : existingIdSet;
            var validated = AssistantTestCaseRules.Validate(
                entry.Question,
                string.IsNullOrWhiteSpace(entry.Category) ? "common" : entry.Category,
                entry.ExpectedKind,
                documentIds,
                followUpOfId,
                connectedDocumentIds,
                effectiveExistingIds);
            if (!validated.IsValid)
            {
                rowErrors.AddRange(validated.Failures.Select(failure => failure.Message));
            }

            if (rowErrors.Count > 0)
            {
                errors[where] = [.. rowErrors];
                continue;
            }

            var value = validated.Value;
            var testCase = new AssistantTestCase(
                assistant.OrganizationId, assistant.Id, value.Question, value.Category, value.ExpectedKind,
                value.ExpectedDocumentIds, value.FollowUpOfId, ordinal, now);
            ordinal++;
            created.Add(testCase);

            var ownLabel = string.IsNullOrWhiteSpace(entry.Id) ? null : entry.Id!.Trim();
            if (ownLabel is not null)
            {
                idsByLabel.TryAdd(ownLabel, testCase.Id);
            }
        }

        if (errors.Count > 0)
        {
            return ApiErrors.ValidationFailed(errors.Values.First()[0], errors);
        }

        dbContext.AssistantTestCases.AddRange(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(created.OrderBy(testCase => testCase.Ordinal).Select(ToView).ToList());
    }

    internal static async Task<IResult> ExportAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var assistant = await FindManageableAsync(dbContext, id, callerId, cancellationToken);
        if (assistant is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.AssistantConfiguration);
        }

        var cases = await OrderedAsync(dbContext, assistant.Id, cancellationToken);
        var documentIds = cases.SelectMany(testCase => testCase.ExpectedDocumentIds).Distinct().ToList();
        var documentNames = await dbContext.KnowledgeDocuments.AsNoTracking()
            .Where(document => documentIds.Contains(document.Id))
            .ToDictionaryAsync(document => document.Id, document => document.Name, cancellationToken);

        var entries = cases.ConvertAll(testCase => new AssistantTestCaseExportEntry(
            testCase.Id.ToString(),
            testCase.Question,
            WireNames<AssistantTestCaseCategory>.ToWire(testCase.Category),
            WireNames<AssistantTestExpectedKind>.ToWire(testCase.ExpectedKind),
            testCase.ExpectedDocumentIds.Select(documentId => documentNames.GetValueOrDefault(documentId)).OfType<string>().ToList(),
            testCase.FollowUpOfId?.ToString()));

        return Results.Ok(new ExportAssistantTestCasesResponse(entries));
    }

    private static Task<Assistant?> FindManageableAsync(
        AppDbContext dbContext, Guid id, Guid callerId, CancellationToken cancellationToken) =>
        dbContext.Assistants
            .Where(AssistantAccess.ManageableBy(callerId))
            .SingleOrDefaultAsync(assistant => assistant.Id == id, cancellationToken);

    private static Task<List<AssistantTestCase>> OrderedAsync(
        AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken) =>
        dbContext.AssistantTestCases
            .AsNoTracking()
            .Where(testCase => testCase.AssistantId == assistantId)
            .OrderBy(testCase => testCase.Ordinal)
            .ThenBy(testCase => testCase.Id)
            .ToListAsync(cancellationToken);

    private static Task<List<Guid>> ConnectedKnowledgeBaseIdsAsync(
        AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken) =>
        dbContext.AssistantKnowledgeBases
            .AsNoTracking()
            .Where(link => link.AssistantId == assistantId)
            .Select(link => link.KnowledgeBaseId)
            .ToListAsync(cancellationToken);

    private static async Task<HashSet<Guid>> ConnectedDocumentIdsAsync(
        AppDbContext dbContext, Guid assistantId, CancellationToken cancellationToken)
    {
        var knowledgeBaseIds = await ConnectedKnowledgeBaseIdsAsync(dbContext, assistantId, cancellationToken);
        return (await dbContext.KnowledgeDocuments.AsNoTracking()
                .Where(document => knowledgeBaseIds.Contains(document.KnowledgeBaseId))
                .Select(document => document.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    private static AssistantTestCaseView ToView(AssistantTestCase testCase) =>
        new(
            testCase.Id,
            testCase.AssistantId,
            testCase.Question,
            testCase.Category,
            testCase.ExpectedKind,
            testCase.ExpectedDocumentIds,
            testCase.FollowUpOfId,
            testCase.Ordinal,
            testCase.CreatedAt,
            testCase.UpdatedAt);
}
