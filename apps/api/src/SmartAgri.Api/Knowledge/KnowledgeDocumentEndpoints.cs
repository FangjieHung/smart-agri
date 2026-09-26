using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Knowledge;
using SmartAgri.Domain;
using SmartAgri.Domain.Jobs;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Knowledge;

/// <summary>
/// The multipart form of <c>POST /api/v1/knowledge-bases/{id}/documents</c>, for the OpenAPI
/// document only: the endpoint reads the form itself (see <see cref="KnowledgeDocumentEndpoints"/>).
/// </summary>
/// <param name="File">Exactly one file.</param>
/// <param name="BatchId">Optional GUID naming the multi-file upload this file belongs to.</param>
public sealed record KnowledgeDocumentUploadForm(IFormFile File, string? BatchId = null);

/// <summary>
/// A knowledge base's documents (M2 plan, Slice 5; ticket #39): upload a file as a new
/// document, retry a failed version, download a version's original file, delete a document.
/// </summary>
/// <remarks>
/// <para>
/// Owner only, exactly like <see cref="KnowledgeBaseEndpoints"/>: a knowledge base that does
/// not exist, belongs to another organization or to someone else — and a document or
/// version that is not in it — all get the same <c>403 knowledge-base</c>. The owner check
/// runs before the request body is read, so nobody else can make the Api buffer a file.
/// </para>
/// <para>
/// Upload rules live in <see cref="KnowledgeUploadRules"/>; every refusal is ProblemDetails
/// with a <c>reason</c> (<see cref="KnowledgeUploadRejectionReason"/>): <c>413</c> too large,
/// <c>415</c> unsupported or mismatched format, <c>422</c> everything else (including
/// <c>duplicate-content</c> with <c>existingDocumentName</c>, and <c>duplicate-name</c>).
/// An accepted upload writes the document, its version 1, the original bytes, an activity
/// row and a <see cref="ProcessKnowledgeVersionJob"/> in one <c>SaveChanges</c> — one
/// transaction — so they exist together or not at all. (A request that is not
/// <c>multipart/form-data</c> at all never reaches the endpoint: routing answers it with a
/// bare <c>415</c>, because the endpoint declares that content type.)
/// </para>
/// </remarks>
public static class KnowledgeDocumentEndpoints
{
    /// <summary>The <c>409</c> reason of a retry that is not allowed.</summary>
    public const string NotRetryableReason = "version-not-retryable";

    public static IEndpointRouteBuilder MapKnowledgeDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<KnowledgeOptions>>().Value;

        var documents = endpoints.MapGroup("/api/v1/knowledge-bases/{id:guid}/documents")
            .RequireAuthorization();

        documents.MapPost("", UploadAsync)
            // Antiforgery protects cookie-authenticated forms; this API only accepts bearer
            // tokens, which a cross-site form cannot send. Explicit, so switching to IFormFile
            // binding later cannot silently start requiring an antiforgery token.
            .DisableAntiforgery()
            // The whole request, not just the file: Kestrel refuses a larger body with 413
            // before it is buffered (reverse proxies must allow as much, see the README).
            .WithMetadata(new RequestBodySizeLimit(options.MaxRequestBodyBytes))
            .WithFormOptions(multipartBodyLengthLimit: options.MaxRequestBodyBytes)
            .Accepts<KnowledgeDocumentUploadForm>("multipart/form-data")
            .Produces<KnowledgeDocumentView>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        documents.MapDelete("/{documentId:guid}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        documents.MapPost("/{documentId:guid}/versions/{versionId:guid}/retry", RetryAsync)
            .Produces<KnowledgeDocumentView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        documents.MapGet("/{documentId:guid}/versions/{versionId:guid}/file", DownloadAsync)
            // The stored content type in fact (application/pdf, text/markdown, ...).
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>
    /// Checks in this order, writing nothing unless all pass: owner (<c>403</c>); the
    /// request's size (<c>413</c>, from <c>Content-Length</c> before reading anything); one
    /// file and a valid <c>batchId</c> (<c>422</c>); the file's size (<c>413</c>); name,
    /// extension and content (<see cref="KnowledgeUploadRules.Inspect"/>); duplicates in the
    /// knowledge base (<c>422</c>).
    /// </summary>
    internal static async Task<IResult> UploadAsync(
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        IOptions<KnowledgeOptions> options,
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

        var limits = options.Value;
        if (httpContext.Request.ContentLength > limits.MaxRequestBodyBytes)
        {
            return Refuse(KnowledgeUploadRules.FileTooLarge(limits.MaxFileBytes));
        }

        var received = await ReceiveAsync(httpContext.Request, limits.MaxFileBytes, cancellationToken);
        if (!received.IsAccepted)
        {
            return Refuse(received.Rejection);
        }

        var (content, rawFileName, batchId) = received.Value;
        var inspection = KnowledgeUploadRules.Inspect(rawFileName, content, limits.MaxFileBytes);
        if (!inspection.IsAccepted)
        {
            return Refuse(inspection.Rejection);
        }

        var file = inspection.Value;
        if (await FindDuplicateAsync(dbContext, knowledgeBase.Id, file, cancellationToken) is { } duplicate)
        {
            return Refuse(duplicate);
        }

        var now = clock.GetUtcNow();
        var document = KnowledgeDocument.CreateUploaded(knowledgeBase, file.FileName, now);
        var version = KnowledgeDocumentVersion.Create(
            document, versionNumber: 1, file.FileName, file.ContentType, file.SizeBytes, file.Sha256, callerId, batchId, now);
        dbContext.KnowledgeDocuments.Add(document);
        dbContext.KnowledgeDocumentVersions.Add(version);
        dbContext.KnowledgeFileContents.Add(new KnowledgeFileContent(version, content));
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.DocumentUploaded(version, callerId, now));
        dbContext.BackgroundJobs.Add(EnqueueProcessing(version, now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            // A concurrent upload of the same content or name committed between the check
            // above and this save; the unique indexes refused this one. Nothing was written.
            dbContext.ChangeTracker.Clear();
            if (await FindDuplicateAsync(dbContext, knowledgeBase.Id, file, cancellationToken) is { } raced)
            {
                return Refuse(raced);
            }

            throw;
        }

        return Results.Created(
            $"/api/v1/knowledge-bases/{knowledgeBase.Id}/documents/{document.Id}",
            new KnowledgeDocumentView(document.Id, document.Kind, document.Name, version.ProcessingStatus, version.Issue, version.UpdatedAt));
    }

    /// <summary>
    /// A <c>failed</c> version back to <c>queued</c>, with a new processing job and an
    /// activity row in the same save; anything else is <c>409</c>. Returns the document as
    /// listed (<see cref="KnowledgeItemStates"/>).
    /// </summary>
    internal static async Task<IResult> RetryAsync(
        Guid id,
        Guid documentId,
        Guid versionId,
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

        var version = await dbContext.KnowledgeDocumentVersions.SingleOrDefaultAsync(
            candidate => candidate.Id == versionId && candidate.DocumentId == documentId && candidate.KnowledgeBaseId == knowledgeBase.Id,
            cancellationToken);
        if (version is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        if (KnowledgeVersionRules.RetryRefusal(version.ProcessingStatus) is { } refusal)
        {
            return ApiErrors.WithReason(StatusCodes.Status409Conflict, NotRetryableReason, refusal);
        }

        var now = clock.GetUtcNow();
        version.Requeue(now);
        dbContext.BackgroundJobs.Add(EnqueueProcessing(version, now));
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.VersionRetried(version, callerId, now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The version's status changed (a concurrent retry) or it was deleted since it
            // was read: ProcessingStatus is a concurrency token. Nothing was written.
            dbContext.ChangeTracker.Clear();
            var current = await dbContext.KnowledgeDocumentVersions
                .AsNoTracking()
                .Where(candidate => candidate.Id == versionId)
                .Select(candidate => (KnowledgeDocumentStatus?)candidate.ProcessingStatus)
                .SingleOrDefaultAsync(cancellationToken);
            return current is { } status
                ? ApiErrors.WithReason(
                    StatusCodes.Status409Conflict,
                    NotRetryableReason,
                    KnowledgeVersionRules.RetryRefusal(status) ?? KnowledgeVersionRules.StillProcessingMessage)
                : ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var state = (await KnowledgeBaseEndpoints.ItemStatesAsync(
            dbContext, dbContext.KnowledgeDocuments.Where(document => document.Id == documentId), cancellationToken)).Single();
        return Results.Ok(KnowledgeBaseEndpoints.ToView(state));
    }

    /// <summary>
    /// The version's original bytes with its stored content type, as an attachment named
    /// after its file. <c>Content-Disposition</c> carries an ASCII-only <c>filename</c>
    /// fallback and the exact (e.g. Chinese) name as RFC 5987 <c>filename*</c>.
    /// </summary>
    internal static async Task<IResult> DownloadAsync(
        Guid id,
        Guid documentId,
        Guid versionId,
        HttpContext httpContext,
        AppDbContext dbContext,
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

        var version = await dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(candidate => candidate.Id == versionId && candidate.DocumentId == documentId && candidate.KnowledgeBaseId == knowledgeBase.Id)
            .Select(candidate => new { candidate.FileName, candidate.ContentType })
            .SingleOrDefaultAsync(cancellationToken);
        var bytes = version is null
            ? null
            : await dbContext.KnowledgeFileContents
                .AsNoTracking()
                .Where(content => content.VersionId == versionId)
                .Select(content => content.Bytes)
                .SingleOrDefaultAsync(cancellationToken);
        if (version is null || bytes is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        // Organization files: never cached, never sniffed into something the browser renders.
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(bytes, version.ContentType, version.FileName);
    }

    /// <summary>
    /// Deletes the document in one save: the document row, and by database cascade all of
    /// its versions and their original files; plus a content-free
    /// <see cref="KnowledgeActivityAction.DocumentDeleted"/> row. Processing jobs already
    /// queued for its versions stay and find nothing to do.
    /// </summary>
    internal static async Task<IResult> DeleteAsync(
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
        if (knowledgeBase is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var document = await dbContext.KnowledgeDocuments.SingleOrDefaultAsync(
            candidate => candidate.Id == documentId && candidate.KnowledgeBaseId == knowledgeBase.Id,
            cancellationToken);
        if (document is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        dbContext.KnowledgeDocuments.Remove(document);
        dbContext.KnowledgeActivities.Add(KnowledgeActivity.DocumentDeleted(document, callerId, clock.GetUtcNow()));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by a concurrent request in the meantime: gone either way.
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        return Results.NoContent();
    }

    private static BackgroundJob EnqueueProcessing(KnowledgeDocumentVersion version, DateTimeOffset now) =>
        BackgroundJob.Create(version.OrganizationId, ProcessKnowledgeVersionJob.Kind, new ProcessKnowledgeVersionJob(version.Id), now);

    /// <summary>The request's single file (read whole; it is at most the size limit) and
    /// batch id, or why the request is refused.</summary>
    private static async Task<KnowledgeUploadCheck<(byte[] Content, string FileName, Guid? BatchId)>> ReceiveAsync(
        HttpRequest request,
        long maxFileBytes,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            // Not reached through routing, which refuses any content type but the declared
            // multipart/form-data (Accepts) with a bare 415; kept so a form is all this reads.
            return Refused(KnowledgeUploadRules.FileMissing);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The body outgrew the endpoint's request size limit (a chunked body, or a
            // Content-Length that was not sent).
            return Refused(KnowledgeUploadRules.FileTooLarge(maxFileBytes));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            // A malformed multipart body (IOException includes BadHttpRequestException).
            return Refused(KnowledgeUploadRules.FileUnreadable);
        }

        if (form.Files.Count > 1)
        {
            return Refused(KnowledgeUploadRules.TooManyFiles);
        }

        if (form.Files.GetFile(KnowledgeUploadRules.FileField) is not { } file)
        {
            return Refused(KnowledgeUploadRules.FileMissing);
        }

        var batchId = KnowledgeUploadRules.ParseBatchId(form[KnowledgeUploadRules.BatchIdField]);
        if (!batchId.IsAccepted)
        {
            return Refused(batchId.Rejection);
        }

        if (KnowledgeUploadRules.CheckSize(file.Length, maxFileBytes) is { } tooLarge)
        {
            return Refused(tooLarge);
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, cancellationToken);
        }

        return KnowledgeUploadCheck<(byte[], string, Guid?)>.Accept((content, file.FileName, batchId.Value));

        static KnowledgeUploadCheck<(byte[], string, Guid?)> Refused(KnowledgeUploadRejection rejection) =>
            KnowledgeUploadCheck<(byte[], string, Guid?)>.Reject(rejection);
    }

    /// <summary><see cref="KnowledgeUploadRules.CheckDuplicates"/> against the knowledge
    /// base's current documents and versions.</summary>
    private static async Task<KnowledgeUploadRejection?> FindDuplicateAsync(
        AppDbContext dbContext,
        Guid knowledgeBaseId,
        InspectedKnowledgeFile file,
        CancellationToken cancellationToken)
    {
        var documentWithSameContent = await dbContext.KnowledgeDocumentVersions
            .AsNoTracking()
            .Where(version => version.KnowledgeBaseId == knowledgeBaseId && version.Sha256 == file.Sha256)
            .Join(dbContext.KnowledgeDocuments, version => version.DocumentId, document => document.Id, (_, document) => document.Name)
            .FirstOrDefaultAsync(cancellationToken);
        var nameTaken = await dbContext.KnowledgeDocuments
            .AnyAsync(document => document.KnowledgeBaseId == knowledgeBaseId && document.Name == file.FileName, cancellationToken);
        return KnowledgeUploadRules.CheckDuplicates(file.FileName, documentWithSameContent, nameTaken);
    }

    /// <summary>The HTTP form of an upload refusal: its status follows from the reason.</summary>
    private static IResult Refuse(KnowledgeUploadRejection rejection)
    {
        var reason = WireNames<KnowledgeUploadRejectionReason>.ToWire(rejection.Reason);
        return rejection.Reason switch
        {
            KnowledgeUploadRejectionReason.FileTooLarge =>
                ApiErrors.WithReason(StatusCodes.Status413PayloadTooLarge, reason, rejection.Message),
            KnowledgeUploadRejectionReason.UnsupportedFileType or KnowledgeUploadRejectionReason.FileContentMismatch =>
                ApiErrors.WithReason(StatusCodes.Status415UnsupportedMediaType, reason, rejection.Message),
            _ => ApiErrors.WithReason(
                StatusCodes.Status422UnprocessableEntity,
                reason,
                rejection.Message,
                rejection.Field,
                rejection.ExistingDocumentName is { } existing ? [new("existingDocumentName", existing)] : null),
        };
    }

    /// <summary>Endpoint metadata that sets the request's body size limit (honored by the
    /// routing middleware on Kestrel and IIS).</summary>
    private sealed class RequestBodySizeLimit : IRequestSizeLimitMetadata
    {
        public RequestBodySizeLimit(long maxRequestBodySize)
        {
            MaxRequestBodySize = maxRequestBodySize;
        }

        public long? MaxRequestBodySize { get; }
    }
}
