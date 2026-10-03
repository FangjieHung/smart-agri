using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authorization;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Databases;

/// <summary>What running a fixed query came to. Exactly one of the three.</summary>
public sealed class DatabaseQueryOutcome<T>
{
    private readonly T? _value;

    private DatabaseQueryOutcome(T? value, bool readable, IReadOnlyList<ValidationFailure> failures)
    {
        _value = value;
        Readable = readable;
        Failures = failures;
    }

    /// <summary>The caller may not read this database's records now, or the database is not in their
    /// organization or does not exist (indistinguishable on purpose). Nothing else about the
    /// request has been looked at.</summary>
    public bool Readable { get; }

    /// <summary>The parameters broke the query's definition (or named a field or subject that does
    /// not exist for this caller); empty otherwise.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }

    public bool IsOk => Readable && Failures.Count == 0;

    public T Value => IsOk ? _value! : throw new InvalidOperationException("Only a successful outcome has a value.");

    public static DatabaseQueryOutcome<T> NotReadable() => new(default, false, []);

    public static DatabaseQueryOutcome<T> Invalid(IReadOnlyList<ValidationFailure> failures) => new(default, true, failures);

    public static DatabaseQueryOutcome<T> Ok(T value) => new(value, true, []);

    internal DatabaseQueryOutcome<TOut> Map<TOut>(Func<T, TOut> map) =>
        !Readable ? DatabaseQueryOutcome<TOut>.NotReadable()
        : Failures.Count > 0 ? DatabaseQueryOutcome<TOut>.Invalid(Failures)
        : DatabaseQueryOutcome<TOut>.Ok(map(_value!));
}

/// <summary>
/// Runs the fixed statistics queries of a database (M4 #147) as an application service: the trend
/// tab's endpoints (<see cref="DatabaseQueryEndpoints"/>) call it now; the assistant's conversation
/// tool (#149) and the scheduled report (#150) call it directly with the same parameters, so they
/// get the same rules and numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every call re-applies access</b> and trusts nothing it was handed: the account must be a
/// designated data manager holding <c>read-consented-submissions</c> <i>now</i>
/// (<see cref="DatabaseActiveRecords.ReadableAsync"/>), the database is in the account's
/// organization (query filters), and only active records count (a withdrawn submission has no
/// entries and <c>WithdrawnAt</c> set, so it drops out the moment it is withdrawn). A caller who may
/// not read gets <see cref="DatabaseQueryOutcome{T}.Readable"/> = <see langword="false"/> before
/// the parameters are even looked at, so the validation messages never reveal fields or subjects
/// of a database the caller cannot read.
/// </para>
/// <para>
/// Parameters are validated by <see cref="DatabaseFixedQueries.Validate"/> against the database's
/// form history; a subject must have submitted to <b>this</b> database (active or withdrawn, like the
/// timeline lists them), and a subject that does not exist, is another database's or another
/// organization's is the same <c>subjectId</c> failure. Dates are UTC days
/// (<see cref="DatabaseFixedQueries"/>).
/// </para>
/// </remarks>
public sealed class DatabaseFixedQueryService
{
    private readonly AppDbContext _dbContext;
    private readonly RequestAccountPermissions _permissions;
    private readonly TimeProvider _clock;

    public DatabaseFixedQueryService(AppDbContext dbContext, RequestAccountPermissions permissions, TimeProvider clock)
    {
        _dbContext = dbContext;
        _permissions = permissions;
        _clock = clock;
    }

    /// <summary>Runs <paramref name="kind"/>; the result is the kind's result record
    /// (<see cref="DatabaseRecordCountResult"/>, <see cref="DatabaseFieldSumResult"/>,
    /// <see cref="DatabasePeriodSummaryResult"/> or <see cref="DatabaseSubjectComparisonResult"/>).</summary>
    public async Task<DatabaseQueryOutcome<object>> RunAsync(
        DatabaseQueryKind kind,
        Guid accountId,
        Guid databaseId,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken) => kind switch
        {
            DatabaseQueryKind.RecordCount => (await RecordCountAsync(accountId, databaseId, parameters, cancellationToken)).Map(result => (object)result),
            DatabaseQueryKind.FieldSum => (await FieldSumAsync(accountId, databaseId, parameters, cancellationToken)).Map(result => (object)result),
            DatabaseQueryKind.PeriodSummary => (await PeriodSummaryAsync(accountId, databaseId, parameters, cancellationToken)).Map(result => (object)result),
            DatabaseQueryKind.SubjectComparison => (await SubjectComparisonAsync(accountId, databaseId, parameters, cancellationToken)).Map(result => (object)result),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a declared query."),
        };

    public async Task<DatabaseQueryOutcome<DatabaseRecordCountResult>> RecordCountAsync(
        Guid accountId, Guid databaseId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(DatabaseQueryKind.RecordCount, accountId, databaseId, parameters, cancellationToken);
        if (opened.Outcome is { } refused)
        {
            return refused.Map<DatabaseRecordCountResult>(_ => throw new InvalidOperationException());
        }

        var (readable, spec) = (opened.Readable!, opened.Spec!);
        var period = spec.Period!;
        var previous = DatabaseFixedQueries.Previous(period);
        var count = await InPeriod(readable, period, spec.SubjectId).CountAsync(cancellationToken);
        var before = await InPeriod(readable, previous, spec.SubjectId).CountAsync(cancellationToken);
        return DatabaseQueryOutcome<DatabaseRecordCountResult>.Ok(new DatabaseRecordCountResult(
            DatabaseQueryPeriodView.Of(period),
            DatabaseQueryPeriodView.Of(previous),
            spec.SubjectId,
            count,
            before,
            count - before,
            DatabaseQueryResults.CountChangeLabel(count - before)));
    }

    public async Task<DatabaseQueryOutcome<DatabaseFieldSumResult>> FieldSumAsync(
        Guid accountId, Guid databaseId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(DatabaseQueryKind.FieldSum, accountId, databaseId, parameters, cancellationToken);
        if (opened.Outcome is { } refused)
        {
            return refused.Map<DatabaseFieldSumResult>(_ => throw new InvalidOperationException());
        }

        var (readable, spec) = (opened.Readable!, opened.Spec!);
        var period = spec.Period!;
        var previous = DatabaseFixedQueries.Previous(period);
        var current = await SumRowsAsync(InPeriod(readable, period, spec.SubjectId), spec.FieldId, cancellationToken);
        var before = await SumRowsAsync(InPeriod(readable, previous, spec.SubjectId), spec.FieldId, cancellationToken);
        var sums = DatabaseQueryResults.Sums(current, before, opened.Fields!, opened.CurrentVersion, spec.FieldId);
        return DatabaseQueryOutcome<DatabaseFieldSumResult>.Ok(new DatabaseFieldSumResult(
            DatabaseQueryPeriodView.Of(period), DatabaseQueryPeriodView.Of(previous), spec.SubjectId, sums.Single()));
    }

    public async Task<DatabaseQueryOutcome<DatabasePeriodSummaryResult>> PeriodSummaryAsync(
        Guid accountId, Guid databaseId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(DatabaseQueryKind.PeriodSummary, accountId, databaseId, parameters, cancellationToken);
        if (opened.Outcome is { } refused)
        {
            return refused.Map<DatabasePeriodSummaryResult>(_ => throw new InvalidOperationException());
        }

        var (readable, spec) = (opened.Readable!, opened.Spec!);
        var period = spec.Period!;
        var previous = DatabaseFixedQueries.Previous(period);
        var inPeriod = InPeriod(readable, period, spec.SubjectId);
        var inPrevious = InPeriod(readable, previous, spec.SubjectId);
        var count = await inPeriod.CountAsync(cancellationToken);
        var before = await inPrevious.CountAsync(cancellationToken);
        var current = await SumRowsAsync(inPeriod, null, cancellationToken);
        var previousRows = await SumRowsAsync(inPrevious, null, cancellationToken);
        return DatabaseQueryOutcome<DatabasePeriodSummaryResult>.Ok(new DatabasePeriodSummaryResult(
            DatabaseQueryPeriodView.Of(period),
            DatabaseQueryPeriodView.Of(previous),
            spec.SubjectId,
            count,
            before,
            count - before,
            DatabaseQueryResults.CountChangeLabel(count - before),
            DatabaseQueryResults.Sums(current, previousRows, opened.Fields!, opened.CurrentVersion)));
    }

    public async Task<DatabaseQueryOutcome<DatabaseSubjectComparisonResult>> SubjectComparisonAsync(
        Guid accountId, Guid databaseId, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(DatabaseQueryKind.SubjectComparison, accountId, databaseId, parameters, cancellationToken);
        if (opened.Outcome is { } refused)
        {
            return refused.Map<DatabaseSubjectComparisonResult>(_ => throw new InvalidOperationException());
        }

        var (readable, spec) = (opened.Readable!, opened.Spec!);
        var subjectId = spec.SubjectId!.Value;
        var records = await LoadRecordsAsync(readable.Where(submission => submission.SubmittedByAccountId == subjectId), cancellationToken);
        var comparison = DatabaseQueryResults.Compare(
            records.GetValueOrDefault(subjectId) ?? [], opened.Fields!, spec.FieldId);
        return DatabaseQueryOutcome<DatabaseSubjectComparisonResult>.Ok(new DatabaseSubjectComparisonResult(subjectId, comparison));
    }

    /// <summary>
    /// The comparison of <b>every</b> subject of the database (those with at least one active
    /// record), for the timeline (<c>GET .../tracking</c>): the same numbers as
    /// <see cref="SubjectComparisonAsync"/>. <see langword="null"/> when the caller may not read the
    /// database's records now.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, DatabaseSubjectComparison>?> CompareAllSubjectsAsync(
        Guid accountId, Guid databaseId, CancellationToken cancellationToken)
    {
        var readable = await DatabaseActiveRecords.ReadableAsync(_dbContext, _permissions, accountId, databaseId, cancellationToken);
        if (readable is null)
        {
            return null;
        }

        var fields = await FieldsAsync(databaseId, cancellationToken);
        var records = await LoadRecordsAsync(readable, cancellationToken);
        return records.ToDictionary(
            pair => pair.Key,
            pair => DatabaseQueryResults.Compare(pair.Value, fields.References));
    }

    private sealed record Opened(
        IQueryable<DatabaseSubmission>? Readable,
        DatabaseQuerySpec? Spec,
        IReadOnlyDictionary<string, DatabaseFieldReference>? Fields,
        int CurrentVersion,
        DatabaseQueryOutcome<object>? Outcome);

    /// <summary>Access first, then the parameters, then the subject.</summary>
    private async Task<Opened> OpenAsync(
        DatabaseQueryKind kind,
        Guid accountId,
        Guid databaseId,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var readable = await DatabaseActiveRecords.ReadableAsync(_dbContext, _permissions, accountId, databaseId, cancellationToken);
        if (readable is null)
        {
            return new Opened(null, null, null, 0, DatabaseQueryOutcome<object>.NotReadable());
        }

        var (references, currentVersion) = await FieldsAsync(databaseId, cancellationToken);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var validated = DatabaseFixedQueries.Validate(kind, parameters, today, references);
        if (!validated.IsValid)
        {
            return new Opened(null, null, null, 0, DatabaseQueryOutcome<object>.Invalid(validated.Failures));
        }

        var spec = validated.Value;
        if (spec.SubjectId is { } subjectId
            && !await _dbContext.DatabaseSubmissions.AsNoTracking().AnyAsync(
                submission => submission.DatabaseId == databaseId && submission.SubmittedByAccountId == subjectId, cancellationToken))
        {
            return new Opened(
                null, null, null, 0,
                DatabaseQueryOutcome<object>.Invalid([new ValidationFailure(DatabaseFixedQueries.SubjectIdKey, DatabaseFixedQueries.SubjectNotFoundMessage)]));
        }

        return new Opened(readable, spec, references, currentVersion, null);
    }

    private async Task<(IReadOnlyDictionary<string, DatabaseFieldReference> References, int CurrentVersion)> FieldsAsync(
        Guid databaseId, CancellationToken cancellationToken)
    {
        var versions = await _dbContext.DatabaseFormVersions.AsNoTracking()
            .Where(version => version.DatabaseId == databaseId)
            .Select(version => new { version.VersionNumber, version.Fields })
            .ToListAsync(cancellationToken);
        return (
            DatabaseFixedQueries.FieldReferences(versions.Select(version => (version.VersionNumber, version.Fields))),
            versions.Count == 0 ? 0 : versions.Max(version => version.VersionNumber));
    }

    private static IQueryable<DatabaseSubmission> InPeriod(
        IQueryable<DatabaseSubmission> readable, DatabaseQueryPeriod period, Guid? subjectId)
    {
        var start = period.Start;
        var end = period.EndExclusive;
        var inPeriod = readable.Where(submission => submission.SubmittedAt >= start && submission.SubmittedAt < end);
        return subjectId is { } subject
            ? inPeriod.Where(submission => submission.SubmittedByAccountId == subject)
            : inPeriod;
    }

    /// <summary>The sum and count of the number entries of <paramref name="submissions"/>, one row per
    /// (field id, unit): the grouping happens in the database, so the cost does not grow with the
    /// number of records.</summary>
    private async Task<IReadOnlyList<DatabaseSumRow>> SumRowsAsync(
        IQueryable<DatabaseSubmission> submissions, string? fieldId, CancellationToken cancellationToken)
    {
        var entries = DatabaseActiveRecords.EntriesOf(_dbContext, submissions)
            .Where(entry => entry.FieldType == DatabaseFieldType.Number && entry.NumberValue != null);
        if (fieldId is not null)
        {
            entries = entries.Where(entry => entry.FieldId == fieldId);
        }

        var rows = await entries
            .GroupBy(entry => new { entry.FieldId, entry.Unit })
            .Select(group => new { group.Key.FieldId, group.Key.Unit, Sum = group.Sum(entry => entry.NumberValue!.Value), Count = group.Count() })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new DatabaseSumRow(row.FieldId, row.Unit, row.Sum, row.Count))];
    }

    /// <summary>The active records among <paramref name="submissions"/> with their number and scale
    /// values, by subject, oldest first.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<DatabaseQueryRecord>>> LoadRecordsAsync(
        IQueryable<DatabaseSubmission> submissions, CancellationToken cancellationToken)
    {
        var rows = await submissions
            .OrderBy(submission => submission.SubmittedAt).ThenBy(submission => submission.Id)
            .Select(submission => new { submission.Id, submission.SubmittedByAccountId, submission.SubmittedAt })
            .ToListAsync(cancellationToken);
        var entries = (await DatabaseActiveRecords.EntriesOf(_dbContext, submissions)
                .Where(entry => (entry.FieldType == DatabaseFieldType.Number || entry.FieldType == DatabaseFieldType.Scale) && entry.NumberValue != null)
                .OrderBy(entry => entry.SubmissionId).ThenBy(entry => entry.Position)
                .Select(entry => new { entry.SubmissionId, entry.FieldId, entry.Label, entry.FieldType, entry.Unit, entry.Display, Value = entry.NumberValue!.Value })
                .ToListAsync(cancellationToken))
            .ToLookup(entry => entry.SubmissionId);

        return rows
            .GroupBy(row => row.SubmittedByAccountId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DatabaseQueryRecord>)[.. group.Select(row => new DatabaseQueryRecord(
                    row.Id,
                    row.SubmittedAt,
                    [.. entries[row.Id].Select(entry => new DatabaseQueryEntry(
                        entry.FieldId, entry.Label, entry.FieldType, entry.Unit, entry.Display, entry.Value))]))]);
    }
}
