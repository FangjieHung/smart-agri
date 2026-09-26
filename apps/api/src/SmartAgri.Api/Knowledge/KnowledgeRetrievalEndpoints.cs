using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authentication;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Knowledge;
using SmartAgri.Application.Knowledge.Embeddings;
using SmartAgri.Application.Knowledge.Retrieval;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Knowledge;

/// <summary><c>POST /api/v1/knowledge-bases/{id}/retrieval-preview</c> request.</summary>
/// <param name="Question">Required, at most 500 characters after trimming.</param>
/// <param name="IncludePending">Also search each document's newest version still pending review
/// (its passages come back with <c>versionState</c> <c>pending-review</c>); default false.</param>
/// <param name="Top">How many passages, 1–20; absent for the deployment's <c>Retrieval:Top</c>.</param>
public sealed record PreviewKnowledgeRetrievalRequest(string? Question, bool? IncludePending = null, int? Top = null);

/// <summary>One passage the question would retrieve.</summary>
/// <param name="VersionState"><c>effective</c>, or <c>pending-review</c> for a pending version's
/// passage (only with <c>includePending</c>).</param>
/// <param name="LocationLabel">Where it is: 「第 2 頁」, a heading path, or worksheet rows.</param>
/// <param name="Excerpt">The passage's text, cut after 300 characters with 「…」.</param>
/// <param name="Score">Cosine similarity to the question, higher is closer (at most 1).</param>
/// <param name="VersionId">The version, e.g. to open its extraction preview.</param>
/// <param name="ChunkId">The passage itself, as the extraction preview lists it.</param>
public sealed record KnowledgeRetrievalPassageView(
    Guid DocumentId,
    string DocumentName,
    int VersionNumber,
    KnowledgeVersionState VersionState,
    string LocationLabel,
    string Excerpt,
    double Score,
    Guid VersionId,
    Guid ChunkId);

/// <summary><c>POST /api/v1/knowledge-bases/{id}/retrieval-preview</c> response.</summary>
/// <param name="Passages">The top passages, closest first, whatever their score.</param>
/// <param name="Threshold">The relevance threshold (<c>Retrieval:MinScore</c>).</param>
/// <param name="BelowThreshold">No passage reaches the threshold (or none was found): an assistant
/// that may only use the organization's data would answer 「查無結果」 to this question.</param>
public sealed record KnowledgeRetrievalPreviewView(
    IReadOnlyList<KnowledgeRetrievalPassageView> Passages,
    double Threshold,
    bool BelowThreshold);

/// <summary>
/// The retrieval preview (M2 plan Slice 9; ticket #43): which version and which page a question
/// would cite, without generating anything — the same <see cref="KnowledgeRetriever"/> M3's
/// conversations will use, over this one knowledge base.
/// </summary>
/// <remarks>
/// <para>
/// Owner only, exactly like the other knowledge endpoints: a knowledge base that does not exist,
/// belongs to another organization or to someone else gets the same <c>403 knowledge-base</c>,
/// before the body is even looked at. Then <c>422</c> for a blank question, one over 500
/// characters, or <c>top</c> outside 1–20. Nothing is written except the question's
/// <c>ModelInvocation</c> row (purpose <c>embed-query</c>, the caller's account).
/// </para>
/// <para>
/// When the question cannot be embedded: <c>503</c> with <c>reason</c>
/// <c>embedding-unavailable</c> (the provider failed or answered unusably; 「嵌入模型暫時無法使用，
/// 請稍後重試」) or <c>embedding-not-configured</c> (the deployment has no provider; ask an
/// administrator). <c>503</c> rather than <c>502</c>: the frontend shows the message and offers
/// to try again later, whatever went wrong upstream.
/// </para>
/// </remarks>
public static class KnowledgeRetrievalEndpoints
{
    /// <summary>The <c>503</c> reason when the embedding provider failed.</summary>
    public const string EmbeddingUnavailableReason = "embedding-unavailable";

    /// <summary>The <c>503</c> reason when the deployment has no embedding provider.</summary>
    public const string EmbeddingNotConfiguredReason = "embedding-not-configured";

    public static IEndpointRouteBuilder MapKnowledgeRetrievalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/knowledge-bases/{id:guid}/retrieval-preview", PreviewAsync)
            .RequireAuthorization()
            .Produces<KnowledgeRetrievalPreviewView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    /// <summary>
    /// Embeds the question and returns the knowledge base's top passages (current effective
    /// versions; with <c>includePending</c> also each document's newest pending version), with
    /// document, version, state, location, an excerpt and the score, and whether all of them
    /// fall below the threshold.
    /// </summary>
    internal static async Task<IResult> PreviewAsync(
        Guid id,
        PreviewKnowledgeRetrievalRequest? request,
        HttpContext httpContext,
        AppDbContext dbContext,
        KnowledgeRetriever retriever,
        ILoggerFactory loggerFactory,
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

        var validation = KnowledgeRetrievalRules.ValidatePreview(request?.Question, request?.Top);
        if (!validation.IsValid)
        {
            return ApiErrors.ValidationFailed(validation.Failures);
        }

        KnowledgeRetrievalResult result;
        try
        {
            result = await retriever.RetrieveAsync(
                new KnowledgeRetrievalQuery(
                    validation.Value.Question,
                    [knowledgeBase.Id],
                    callerId,
                    AssistantId: null,
                    IncludePending: request?.IncludePending ?? false,
                    Top: validation.Value.Top),
                cancellationToken);
        }
        catch (KnowledgeEmbeddingException exception)
        {
            // The ModelInvocation row and span record the failed call; this says why, for operators.
            loggerFactory.CreateLogger(typeof(KnowledgeRetrievalEndpoints).FullName!)
                .LogWarning(exception.InnerException, "The retrieval preview could not embed the question: {Issue}", exception.Message);
            return ApiErrors.WithReason(
                StatusCodes.Status503ServiceUnavailable,
                exception.ProviderNotConfigured ? EmbeddingNotConfiguredReason : EmbeddingUnavailableReason,
                exception.Message);
        }

        return Results.Ok(new KnowledgeRetrievalPreviewView(
            [
                .. result.Passages.Select(passage => new KnowledgeRetrievalPassageView(
                    passage.DocumentId,
                    passage.DocumentName,
                    passage.VersionNumber,
                    passage.VersionState,
                    passage.LocationLabel,
                    KnowledgeRetrievalRules.Excerpt(passage.Text),
                    passage.Score,
                    passage.VersionId,
                    passage.ChunkId)),
            ],
            result.Threshold,
            result.BelowThreshold));
    }
}
