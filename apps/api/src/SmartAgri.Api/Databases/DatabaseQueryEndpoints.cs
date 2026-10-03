using SmartAgri.Api.Authentication;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Errors;
using SmartAgri.Application.Databases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

/// <summary>
/// The fixed statistics queries as HTTP (M4 #147): <c>GET /api/v1/databases/{id}/queries/{name}</c>
/// for each <see cref="DatabaseFixedQueries.Definitions"/> (<c>record-count</c>, <c>field-sum</c>,
/// <c>period-summary</c>, <c>subject-comparison</c>). The endpoints only translate: the query string
/// is the parameter set, and <see cref="DatabaseFixedQueryService"/> decides everything else, the
/// same way for the assistant's tool (#149) and the scheduled report (#150).
/// </summary>
/// <remarks>
/// Status codes: <c>200</c> the result; <c>401</c> not signed in; <c>403 database</c> the database is
/// not visible to the caller or does not exist (also another organization's); <c>403
/// database-records</c> visible but its records may not be read by this caller (e.g. its owner who is
/// not a designated data manager); <c>422</c> a parameter the query does not define, a required one
/// missing, an undefined value, or a field or subject that does not exist for this caller
/// (<c>errors.&lt;parameter&gt;</c>). Access is checked first, so a <c>422</c> never reveals anything
/// about a database the caller cannot read. Not enough data is not an error: counts and sums are 0,
/// and a comparison answers <c>status: insufficient-records</c>.
/// </remarks>
public static class DatabaseQueryEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseQueryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var queries = endpoints.MapGroup(DatabaseEndpoints.DatabasesPath).RequireAuthorization();

        Map<DatabaseRecordCountResult>(queries, DatabaseQueryKind.RecordCount);
        Map<DatabaseFieldSumResult>(queries, DatabaseQueryKind.FieldSum);
        Map<DatabasePeriodSummaryResult>(queries, DatabaseQueryKind.PeriodSummary);
        Map<DatabaseSubjectComparisonResult>(queries, DatabaseQueryKind.SubjectComparison);
        return endpoints;
    }

    private static void Map<TResult>(RouteGroupBuilder queries, DatabaseQueryKind kind)
    {
        var name = SmartAgri.Domain.WireNames<DatabaseQueryKind>.ToWire(kind);
        queries.MapGet(
                "/{id:guid}/queries/" + name,
                (Guid id, HttpContext httpContext, AppDbContext dbContext, RequestAccountPermissions permissions,
                    DatabaseFixedQueryService service, CancellationToken cancellationToken) =>
                    RunAsync(kind, id, httpContext, dbContext, permissions, service, cancellationToken))
            .WithName("DatabaseQuery_" + name)
            .Produces<TResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity);
    }

    internal static async Task<IResult> RunAsync(
        DatabaseQueryKind kind,
        Guid id,
        HttpContext httpContext,
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        DatabaseFixedQueryService service,
        CancellationToken cancellationToken)
    {
        if (AccountClaims.GetAccountId(httpContext.User) is not { } viewerId)
        {
            return ApiErrors.Unauthorized();
        }

        // A repeated key (?period=a&period=b) is one value that is none of the defined ones.
        var parameters = httpContext.Request.Query.ToDictionary(
            pair => pair.Key, pair => (string?)string.Join(',', pair.Value.ToArray()), StringComparer.Ordinal);
        var outcome = await service.RunAsync(kind, viewerId, id, parameters, cancellationToken);
        if (!outcome.Readable)
        {
            return await DatabaseSubmissionEndpoints.RecordsRefusedAsync(dbContext, permissions, viewerId, id, cancellationToken);
        }

        return outcome.IsOk ? Results.Ok(outcome.Value) : ApiErrors.ValidationFailed(outcome.Failures);
    }
}
