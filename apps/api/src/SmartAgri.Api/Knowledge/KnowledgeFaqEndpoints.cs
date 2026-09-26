using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Knowledge;

/// <summary><c>POST /api/v1/knowledge-bases/{id}/faqs</c> and <c>PUT .../faqs/{docId}</c>
/// request. Both fields are required: a question of at most 500 characters and an answer of at
/// most 4000, after normalization (<see cref="KnowledgeFaqRules"/>); missing, blank or too long
/// is this endpoint's own <c>422</c>.</summary>
public sealed record KnowledgeFaqRequest(string? Question, string? Answer);

/// <summary>An FAQ entry's question and answer in one of its versions.</summary>
public sealed record KnowledgeFaqContentView(Guid VersionId, int VersionNumber, string Question, string Answer);

/// <summary>An FAQ entry: the response of <c>GET</c>, <c>POST</c> and <c>PUT .../faqs</c>.</summary>
/// <param name="Document">The entry as the knowledge base lists it (<c>kind</c> <c>faq</c>; its
/// name is its latest question). Its version history, approval and activity log are the
/// document's: <c>GET .../documents/{docId}</c>.</param>
/// <param name="Latest">The latest version's question and answer — what an edit starts from,
/// whether it is approved yet or not (see <c>document.latestVersionState</c>).</param>
/// <param name="Effective">The question and answer assistants use now: the version in effect,
/// which is <paramref name="Latest"/>'s until an edit is approved; <see langword="null"/> while
/// no version is in effect (never approved yet, or only scheduled). Emergency disabling does not
/// clear it (see <c>document.disabled</c>).</param>
public sealed record KnowledgeFaqView(KnowledgeDocumentView Document, KnowledgeFaqContentView Latest, KnowledgeFaqContentView? Effective);

/// <summary>
/// FAQ entries (M2 plan Slice 10; ticket #44): the knowledge-source ADR's hand-written Q&amp;A,
/// under the same approval and retrieval rules as documents.
/// </summary>
/// <remarks>
/// <para>
/// An FAQ entry is a <see cref="KnowledgeDocument"/> of kind <c>faq</c>; each write is a new
/// <see cref="KnowledgeDocumentVersion"/> whose content is the question and answer
/// (<see cref="KnowledgeFaqEntry"/>, stored like a file, so the SHA-256 duplicate rule holds).
/// Creating one writes the document, version 1, its content, a <c>faq-created</c> activity row
/// and a <see cref="ProcessKnowledgeVersionJob"/> in one save — the job turns it into one unit
/// and one chunk located 「FAQ」 and embeds it, exactly as it processes a file. An edit is a new
/// version, pending review like every version, so <b>the answer in effect keeps serving until the
/// owner approves the edit</b> (<c>POST .../versions/approve</c>); retrieval previews show the
/// edit with <c>includePending</c>.
/// </para>
/// <para>
/// Everything else goes through the documents' endpoints, which treat an FAQ entry as the
/// document it is: the version history (<c>GET .../documents/{docId}</c>), approval, disable and
/// enable, the extraction preview and chunk exclusion, retrying a failed version, and the
/// knowledge base's list and counts (<c>faqCount</c>). Only uploading a file as a new version
/// refuses it.
/// </para>
/// <para>
/// Owner only, exactly like the other knowledge endpoints: a knowledge base that does not exist,
/// belongs to another organization or to someone else — and an id that is not an FAQ entry of
/// it, an uploaded document's included — all get the same <c>403 knowledge-base</c>, before the
/// body is looked at. Then <c>422</c> for a missing, blank or too long field, and
/// <c>422 duplicate-content</c> (with <c>existingDocumentName</c>) or <c>duplicate-name</c> by
/// <see cref="KnowledgeFaqRules"/>.
/// </para>
/// </remarks>
public static class KnowledgeFaqEndpoints
{
    public const string ConcurrentEditMessage = "這則 FAQ 剛剛有其他變更，請重新整理後再試一次。";

    public static IEndpointRouteBuilder MapKnowledgeFaqEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var faqs = endpoints.MapGroup("/api/v1/knowledge-bases/{id:guid}/faqs")
            .RequireAuthorization();

        faqs.MapPost("", CreateAsync)
            .Produces<KnowledgeFaqView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        faqs.MapGet("/{documentId:guid}", GetAsync)
            .Produces<KnowledgeFaqView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        faqs.MapPut("/{documentId:guid}", UpdateAsync)
            .Produces<KnowledgeFaqView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        faqs.MapDelete("/{documentId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>
    /// A new FAQ entry, version 1 pending review, in one save with its content, a
    /// <c>faq-created</c> activity row and its processing job; <c>201</c> with the entry.
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        Guid id,
        KnowledgeFaqRequest? request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await KnowledgeBaseEndpoints.FindManageableAsync(
            dbContext.KnowledgeBases.AsNoTracking(), id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var validation = KnowledgeFaqRules.Validate(request?.Question, request?.Answer);
        if (!validation.IsValid)
        {
            return ApiErrors.ValidationFailed(validation.Failures);
        }

        var draft = validation.Value;
        if (await FindNewEntryDuplicateAsync(dbContext, knowledgeBase.Id, draft, cancellationToken) is { } duplicate)
        {
            return Refuse(duplicate);
        }

        var now = clock.GetUtcNow();
        var document = KnowledgeDocument.CreateFaq(knowledgeBase, draft.Name, now);
        var version = KnowledgeDocumentVersion.CreateFaq(document, versionNumber: 1, draft.Name, draft.Content, draft.Sha256, callerId, now);
        dbContext.KnowledgeDocuments.Add(document);
        dbContext.KnowledgeDocumentVersions.Add(version);
        dbContext.KnowledgeFileContents.Add(new KnowledgeFileContent(version, draft.Content));
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.FaqCreated(version, callerId, now));
        dbContext.BackgroundJobs.Add(KnowledgeDocumentEndpoints.EnqueueProcessing(version, now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            // A concurrent request committed the same content or name between the check above
            // and this save; the unique indexes refused this one. Nothing was written.
            dbContext.ChangeTracker.Clear();
            if (await FindNewEntryDuplicateAsync(dbContext, knowledgeBase.Id, draft, cancellationToken) is { } raced)
            {
                return Refuse(raced);
            }

            throw;
        }

        return Results.Created(
            $"/api/v1/knowledge-bases/{knowledgeBase.Id}/faqs/{document.Id}",
            await FaqViewAsync(dbContext, document.Id, now, cancellationToken));
    }

    /// <summary>The entry with its latest and its effective question and answer.</summary>
    internal static async Task<IResult> GetAsync(
        Guid id,
        Guid documentId,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await KnowledgeBaseEndpoints.FindManageableAsync(
            dbContext.KnowledgeBases.AsNoTracking(), id, callerId, cancellationToken);
        if (knowledgeBase is null
            || !await FaqsOf(dbContext.KnowledgeDocuments.AsNoTracking(), knowledgeBase.Id)
                .AnyAsync(document => document.Id == documentId, cancellationToken))
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        return Results.Ok(await FaqViewAsync(dbContext, documentId, clock.GetUtcNow(), cancellationToken));
    }

    /// <summary>
    /// An edit: a new version (the entry's highest number plus one) pending review, in one save
    /// with its content, a <c>faq-updated</c> activity row and its processing job — and, when the
    /// question changed, the entry's new name. The version in effect is untouched. <c>409</c>
    /// when a concurrent change (another edit taking the version number, a disable or enable)
    /// won the race.
    /// </summary>
    internal static async Task<IResult> UpdateAsync(
        Guid id,
        Guid documentId,
        KnowledgeFaqRequest? request,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } callerId)
        {
            return ApiErrors.Unauthorized();
        }

        var knowledgeBase = await KnowledgeBaseEndpoints.FindManageableAsync(
            dbContext.KnowledgeBases.AsNoTracking(), id, callerId, cancellationToken);
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        // Tracked: the new version is linked to it, and a new question renames it.
        var document = await FaqsOf(dbContext.KnowledgeDocuments, knowledgeBase.Id)
            .SingleOrDefaultAsync(candidate => candidate.Id == documentId, cancellationToken);
        if (document is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var validation = KnowledgeFaqRules.Validate(request?.Question, request?.Answer);
        if (!validation.IsValid)
        {
            return ApiErrors.ValidationFailed(validation.Failures);
        }

        var draft = validation.Value;
        if (await FindEditDuplicateAsync(dbContext, knowledgeBase.Id, document.Id, document.Name, draft, cancellationToken) is { } duplicate)
        {
            return Refuse(duplicate);
        }

        var latest = await dbContext.KnowledgeDocumentVersions
            .Where(version => version.DocumentId == document.Id)
            .MaxAsync(version => (int?)version.VersionNumber, cancellationToken);
        var currentName = document.Name;
        var now = clock.GetUtcNow();
        if (draft.Name != currentName)
        {
            document.RenameFaq(draft.Name);
        }

        var version = KnowledgeDocumentVersion.CreateFaq(document, (latest ?? 0) + 1, draft.Name, draft.Content, draft.Sha256, callerId, now);
        dbContext.KnowledgeDocumentVersions.Add(version);
        dbContext.KnowledgeFileContents.Add(new KnowledgeFileContent(version, draft.Content));
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.FaqUpdated(version, callerId, now));
        dbContext.BackgroundJobs.Add(KnowledgeDocumentEndpoints.EnqueueProcessing(version, now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Renaming updates the entry's row, which is saved only if it was not disabled,
            // enabled or deleted since it was read (DisabledAt is a concurrency token). Nothing
            // was written.
            dbContext.ChangeTracker.Clear();
            return await FaqsOf(dbContext.KnowledgeDocuments.AsNoTracking(), knowledgeBase.Id)
                    .AnyAsync(candidate => candidate.Id == documentId, cancellationToken)
                ? ConcurrentEdit()
                : ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            // A concurrent request committed the same content or name, or took this version
            // number, in the meantime (the unique indexes refused this one). Nothing was written.
            dbContext.ChangeTracker.Clear();
            return await FindEditDuplicateAsync(dbContext, knowledgeBase.Id, documentId, currentName, draft, cancellationToken) is { } raced
                ? Refuse(raced)
                : ConcurrentEdit();
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsForeignKeyViolation(exception))
        {
            // The entry was deleted in the meantime: gone, and nothing was written.
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        return Results.Ok(await FaqViewAsync(dbContext, document.Id, now, cancellationToken));
    }

    /// <summary>Deletes the entry exactly as a document is deleted (its versions, content, units
    /// and chunks go with it by cascade), recording <c>faq-deleted</c>; <c>204</c>.</summary>
    internal static Task<IResult> DeleteAsync(
        Guid id,
        Guid documentId,
        HttpContext httpContext,
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        KnowledgeDocumentEndpoints.DeleteItemAsync(id, documentId, KnowledgeItemKind.Faq, httpContext, dbContext, clock, cancellationToken);

    /// <summary>The entry as listed, with its latest and its effective version's content (the
    /// only file contents this reads: two small JSON rows at most).</summary>
    private static async Task<KnowledgeFaqView> FaqViewAsync(
        AppDbContext dbContext,
        Guid documentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var item = await KnowledgeDocumentEndpoints.DocumentViewAsync(dbContext, documentId, now, cancellationToken);
        int[] versionNumbers = item.EffectiveVersionNumber is { } effective && effective != item.LatestVersionNumber
            ? [item.LatestVersionNumber, effective]
            : [item.LatestVersionNumber];
        var contents = await dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(version => version.DocumentId == documentId && versionNumbers.Contains(version.VersionNumber))
            .Join(
                dbContext.KnowledgeFileContents.AsNoTracking(),
                version => version.Id,
                content => content.VersionId,
                (version, content) => new { version.Id, version.VersionNumber, content.Bytes })
            .ToListAsync(cancellationToken);

        var views = contents.ToDictionary(
            content => content.VersionNumber,
            content =>
            {
                var entry = KnowledgeFaqEntry.FromContent(content.Bytes);
                return new KnowledgeFaqContentView(content.Id, content.VersionNumber, entry.Question, entry.Answer);
            });
        return new KnowledgeFaqView(
            item,
            views[item.LatestVersionNumber],
            item.EffectiveVersionNumber is { } number ? views[number] : null);
    }

    private static IQueryable<KnowledgeDocument> FaqsOf(IQueryable<KnowledgeDocument> documents, Guid knowledgeBaseId) =>
        documents.Where(document => document.KnowledgeBaseId == knowledgeBaseId && document.Kind == KnowledgeItemKind.Faq);

    /// <summary><see cref="KnowledgeFaqRules.CheckNewEntryDuplicates"/> against the knowledge
    /// base's current items and versions.</summary>
    private static async Task<KnowledgeUploadRejection?> FindNewEntryDuplicateAsync(
        AppDbContext dbContext,
        Guid knowledgeBaseId,
        KnowledgeFaqDraft draft,
        CancellationToken cancellationToken)
    {
        var sameContent = await KnowledgeDocumentEndpoints.FindExistingContentAsync(dbContext, knowledgeBaseId, draft.Sha256, cancellationToken);
        var nameTaken = await dbContext.KnowledgeDocuments
            .AnyAsync(document => document.KnowledgeBaseId == knowledgeBaseId && document.Name == draft.Name, cancellationToken);
        return KnowledgeFaqRules.CheckNewEntryDuplicates(draft.Name, sameContent, nameTaken);
    }

    /// <summary><see cref="KnowledgeFaqRules.CheckEditDuplicates"/> for an edit of
    /// <paramref name="documentId"/>, now named <paramref name="currentName"/>.</summary>
    private static async Task<KnowledgeUploadRejection?> FindEditDuplicateAsync(
        AppDbContext dbContext,
        Guid knowledgeBaseId,
        Guid documentId,
        string currentName,
        KnowledgeFaqDraft draft,
        CancellationToken cancellationToken)
    {
        var sameContent = await KnowledgeDocumentEndpoints.FindExistingContentAsync(dbContext, knowledgeBaseId, draft.Sha256, cancellationToken);
        var nameTakenByAnother = draft.Name != currentName && await dbContext.KnowledgeDocuments
            .AnyAsync(
                document => document.KnowledgeBaseId == knowledgeBaseId && document.Name == draft.Name && document.Id != documentId,
                cancellationToken);
        return KnowledgeFaqRules.CheckEditDuplicates(documentId, draft.Name, sameContent, nameTakenByAnother);
    }

    /// <summary>A duplicate refusal: <c>422</c> with the upload rules' reason, about the
    /// question.</summary>
    private static IResult Refuse(KnowledgeUploadRejection rejection) =>
        ApiErrors.WithReason(
            StatusCodes.Status422UnprocessableEntity,
            WireNames<KnowledgeUploadRejectionReason>.ToWire(rejection.Reason),
            rejection.Message,
            KnowledgeFaqRules.QuestionField,
            rejection.ExistingDocumentName is { } existing ? [new("existingDocumentName", existing)] : null);

    private static IResult ConcurrentEdit() =>
        ApiErrors.WithReason(
            StatusCodes.Status409Conflict,
            KnowledgeDocumentEndpoints.ConcurrentVersionUploadReason,
            ConcurrentEditMessage);
}
