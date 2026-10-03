using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authorization;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

/// <summary>
/// The <b>active records</b> (有效紀錄) of a database: its submissions that have not been
/// withdrawn (<c>WithdrawnAt IS NULL</c>), with their entries. The single definition every list,
/// count, comparison and trend uses (M4 #146; #147's fixed queries start here), so a withdrawn
/// submission drops out of all of them at once and a later recomputation never includes it.
/// </summary>
/// <remarks>
/// <para>
/// Withdrawal deletes a submission's entries in the same transaction that sets
/// <c>WithdrawnAt</c>, so a withdrawn submission has no content to leak; filtering on
/// <c>WithdrawnAt</c> as well keeps a count of submissions (which has no entries to join) right.
/// </para>
/// <para>
/// The queries are organization-filtered by the context (query filters) but <b>not</b> narrowed to
/// who may read them: use <see cref="ReadableAsync"/>, which asks
/// <see cref="DatabaseRecordReaders.CanReadAsync"/> for this request first, or check it yourself
/// before calling <see cref="Of"/>.
/// </remarks>
public static class DatabaseActiveRecords
{
    /// <summary>The active submissions of <paramref name="databaseId"/> (no tracking).</summary>
    public static IQueryable<DatabaseSubmission> Of(AppDbContext dbContext, Guid databaseId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.DatabaseSubmissions.AsNoTracking()
            .Where(submission => submission.DatabaseId == databaseId && submission.WithdrawnAt == null);
    }

    /// <summary>The entries (typed values) of <paramref name="submissions"/>, e.g. of
    /// <see cref="Of"/>.</summary>
    public static IQueryable<DatabaseSubmissionEntry> EntriesOf(AppDbContext dbContext, IQueryable<DatabaseSubmission> submissions)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(submissions);
        return dbContext.DatabaseSubmissionEntries.AsNoTracking()
            .Where(entry => submissions.Any(submission => submission.Id == entry.SubmissionId));
    }

    /// <summary>
    /// <see cref="Of"/> when <paramref name="accountId"/> may read the records of
    /// <paramref name="databaseId"/> on this request (designated data manager holding
    /// <c>read-consented-submissions</c> now); <see langword="null"/> otherwise — including a
    /// database of another organization or one that does not exist.
    /// </summary>
    public static async Task<IQueryable<DatabaseSubmission>?> ReadableAsync(
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        Guid accountId,
        Guid databaseId,
        CancellationToken cancellationToken) =>
        await DatabaseRecordReaders.CanReadAsync(dbContext, permissions, accountId, databaseId, cancellationToken)
            ? Of(dbContext, databaseId)
            : null;
}
