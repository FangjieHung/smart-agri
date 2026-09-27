using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Domain.Knowledge;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Knowledge;

/// <summary>A chunk in the extraction preview; also the response of <c>PUT .../exclusion</c>.</summary>
/// <param name="LocationLabel">Where it is, for citations: its unit's label, or a worksheet's
/// rows (「工作表『配送時間』第 2–30 列」).</param>
/// <param name="Excluded">Whether the owner excluded it from retrieval.</param>
public sealed record KnowledgeChunkView(Guid Id, string LocationLabel, string Text, bool Excluded);

/// <summary>One page, section or worksheet as processing read it.</summary>
/// <param name="Ordinal">0, 1, 2, … in reading order.</param>
/// <param name="LocationLabel">「第 3 頁」, 「2 退換貨 › 2.1 退貨條件」, 「工作表『配送時間』」.</param>
/// <param name="Readable">Whether its text is used; an unreadable unit has no chunks.</param>
/// <param name="IssueCode">Why it is unreadable (<c>too-little-text</c>, <c>garbled-text</c>), or
/// that a worksheet was cut at the row limit (<c>rows-truncated</c>); otherwise null.</param>
/// <param name="Text">Everything read from it, readable or not.</param>
public sealed record KnowledgeExtractedUnitView(
    int Ordinal,
    KnowledgeUnitLocationKind LocationKind,
    string LocationLabel,
    bool Readable,
    KnowledgeUnitIssue? IssueCode,
    string Text,
    IReadOnlyList<KnowledgeChunkView> Chunks);

/// <summary><c>GET .../documents/{docId}/versions/{versionId}/preview</c> response.</summary>
/// <param name="Status">The version's own processing status (its document may show another
/// version's; see <c>KnowledgeItemStates</c>).</param>
/// <param name="Issue">Why it is failed or partially readable, for the owner.</param>
/// <param name="Units">In reading order, as the last finished processing read them: none
/// before the first one, and a retried version keeps its previous units until it is
/// processed again.</param>
public sealed record KnowledgeVersionPreviewView(
    Guid DocumentId,
    Guid VersionId,
    int VersionNumber,
    string FileName,
    KnowledgeDocumentStatus Status,
    string? Issue,
    IReadOnlyList<KnowledgeExtractedUnitView> Units);

/// <summary><c>PUT .../chunks/{chunkId}/exclusion</c> request. <see cref="Excluded"/> is
/// required; missing, it is this endpoint's own <c>422</c>.</summary>
public sealed record UpdateKnowledgeChunkExclusionRequest(bool? Excluded);

/// <summary>
/// What processing read from a version (M2 plan, Slice 6; ticket #40): the extraction preview,
/// and excluding chunks from retrieval (a cover page, an appendix, outdated terms).
/// </summary>
/// <remarks>
/// Owner only, exactly like <see cref="KnowledgeDocumentEndpoints"/>: a knowledge base that does
/// not exist, belongs to another organization or to someone else — and a document, version or
/// chunk that is not in it — all get the same <c>403 knowledge-base</c>. An exclusion change
/// writes a content-free activity row (the chunk's id only) in the same save; setting what is
/// already set writes nothing.
/// </remarks>
public static class KnowledgeExtractionEndpoints
{
    public const string ExcludedRequiredMessage = "請指定是否排除這個段落。";

    public static IEndpointRouteBuilder MapKnowledgeExtractionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var version = endpoints.MapGroup("/api/v1/knowledge-bases/{id:guid}/documents/{documentId:guid}/versions/{versionId:guid}")
            .RequireAuthorization();

        version.MapGet("/preview", PreviewAsync)
            .Produces<KnowledgeVersionPreviewView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        version.MapPut("/chunks/{chunkId:guid}/exclusion", UpdateExclusionAsync)
            .Produces<KnowledgeChunkView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>The version's status and issue, and its units in order, each with its chunks
    /// in order. File contents are never read.</summary>
    internal static async Task<IResult> PreviewAsync(
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
            .SingleOrDefaultAsync(
                candidate => candidate.Id == versionId && candidate.DocumentId == documentId && candidate.KnowledgeBaseId == knowledgeBase.Id,
                cancellationToken);
        if (version is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        var units = await dbContext.KnowledgeExtractedUnits
            .AsNoTracking()
            .Where(unit => unit.VersionId == versionId)
            .OrderBy(unit => unit.Ordinal)
            .ToListAsync(cancellationToken);
        var chunks = (await dbContext.KnowledgeChunks
                .AsNoTracking()
                .Where(chunk => chunk.VersionId == versionId)
                .OrderBy(chunk => chunk.UnitOrdinal)
                .ThenBy(chunk => chunk.Ordinal)
                .ToListAsync(cancellationToken))
            .ToLookup(chunk => chunk.UnitOrdinal);

        return Results.Ok(new KnowledgeVersionPreviewView(
            version.DocumentId,
            version.Id,
            version.VersionNumber,
            version.FileName,
            version.ProcessingStatus,
            version.Issue,
            [
                .. units.Select(unit => new KnowledgeExtractedUnitView(
                    unit.Ordinal,
                    unit.LocationKind,
                    unit.LocationLabel,
                    unit.Readable,
                    unit.IssueCode,
                    unit.Text,
                    [.. chunks[unit.Ordinal].Select(ToView)])),
            ]));
    }

    /// <summary>Excludes or includes one chunk; <c>200</c> with the chunk as it now is.</summary>
    internal static async Task<IResult> UpdateExclusionAsync(
        Guid id,
        Guid documentId,
        Guid versionId,
        Guid chunkId,
        UpdateKnowledgeChunkExclusionRequest request,
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

        var chunk = await dbContext.KnowledgeChunks.SingleOrDefaultAsync(
            candidate => candidate.Id == chunkId
                && candidate.VersionId == versionId
                && candidate.DocumentId == documentId
                && candidate.KnowledgeBaseId == knowledgeBase.Id,
            cancellationToken);
        if (chunk is null)
        {
            return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
        }

        if (request.Excluded is not { } excluded)
        {
            return ApiErrors.ValidationFailed(
                ExcludedRequiredMessage,
                new Dictionary<string, string[]> { ["excluded"] = [ExcludedRequiredMessage] });
        }

        if (chunk.SetExcluded(excluded))
        {
            dbContext.KnowledgeActivities.Add(KnowledgeActivity.ChunkExclusionChanged(chunk, callerId, clock.GetUtcNow()));
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Deleted in the meantime (its document was deleted, or its failed version
                // reprocessed): gone either way, and nothing was written.
                return ApiErrors.NotFound(ForbiddenReason.KnowledgeBase);
            }
        }

        return Results.Ok(ToView(chunk));
    }

    private static KnowledgeChunkView ToView(KnowledgeChunk chunk) => new(chunk.Id, chunk.LocationLabel, chunk.Text, chunk.Excluded);
}
