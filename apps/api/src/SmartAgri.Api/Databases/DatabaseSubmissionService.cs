using Microsoft.EntityFrameworkCore;
using SmartAgri.Application.Databases;
using SmartAgri.Application.Validation;
using SmartAgri.Domain.Databases;
using SmartAgri.Infrastructure;
using SmartAgri.Infrastructure.Databases;
using SmartAgri.Infrastructure.Persistence;

namespace SmartAgri.Api.Databases;

/// <summary>A submission as the entry point received it.</summary>
/// <param name="DatabaseId">The database submitted to.</param>
/// <param name="SubmitterAccountId">The signed-in member (never taken from the body).</param>
/// <param name="SubmissionId">The client's idempotency key: generated once per form fill and
/// reused for every retry of it.</param>
/// <param name="FormVersionNumber">The form version the member filled in and saw.</param>
/// <param name="Consent">Only an explicit <see langword="true"/> is consent.</param>
/// <param name="Answers">By field id (see <see cref="DatabaseEndpoints.ToAnswerInputs"/>).</param>
/// <param name="Source">The entry point: the standalone form (#145) or an assistant's form
/// request (#148).</param>
public sealed record DatabaseSubmissionCommand(
    Guid DatabaseId,
    Guid SubmitterAccountId,
    Guid? SubmissionId,
    int? FormVersionNumber,
    bool? Consent,
    IReadOnlyDictionary<string, DatabaseAnswerInput> Answers,
    DatabaseSubmissionSource Source);

/// <summary>What a submission attempt came to. Exactly one of these; only
/// <see cref="Created"/> wrote anything.</summary>
public abstract record DatabaseSubmissionOutcome
{
    private DatabaseSubmissionOutcome()
    {
    }

    /// <summary>A new record and its receipt.</summary>
    public sealed record Created(DatabaseSubmissionReceiptView Receipt) : DatabaseSubmissionOutcome;

    /// <summary>The key was already used for this very content: the original receipt, nothing written.</summary>
    public sealed record Replayed(DatabaseSubmissionReceiptView Receipt) : DatabaseSubmissionOutcome;

    /// <summary>The database does not exist in the caller's organization.</summary>
    public sealed record Unavailable : DatabaseSubmissionOutcome;

    /// <summary>The request or the answers are invalid (<c>422</c>).</summary>
    public sealed record Invalid(IReadOnlyList<ValidationFailure> Failures) : DatabaseSubmissionOutcome;

    /// <summary>Valid answers, but no explicit consent (<c>422 consent-required</c>).</summary>
    public sealed record ConsentMissing : DatabaseSubmissionOutcome;

    /// <summary>The member filled in a version that is no longer current (<c>409</c>).</summary>
    public sealed record FormChanged(int CurrentVersionNumber) : DatabaseSubmissionOutcome;

    /// <summary>The key was already used for different content or another database (<c>409</c>).</summary>
    public sealed record KeyReused : DatabaseSubmissionOutcome;

    /// <summary>A review (no write): the answers are valid for the current version.</summary>
    public sealed record Reviewed(int FormVersionNumber, IReadOnlyList<DatabaseAnswerEntry> Entries) : DatabaseSubmissionOutcome;
}

/// <summary>
/// Consented submission to a database (M4 #145) as an application service, so every entry point
/// shares one implementation: the standalone form endpoints (<see cref="DatabaseSubmissionEndpoints"/>)
/// now, an assistant's in-conversation form request (#148) later.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entry-point authorization is the caller's job</b>: the standalone form requires
/// <c>submit-authorized-forms</c> (checked by the endpoint filter); #148 must check that the
/// assistant may be used and is connected to the database before calling. This service then
/// re-checks everything else on the server — the database is in the caller's organization (query
/// filter), the submitter is the signed-in account, the form version is current, the answers pass
/// <see cref="DatabaseAnswerRules"/> (the trial's rule), and consent is explicit — in the order of
/// <see cref="DatabaseSubmissionRules"/>.
/// </para>
/// <para>
/// The trail row and every entry row are added to the context and written by <b>one</b>
/// <c>SaveChanges</c>, i.e. one transaction: any failure leaves no row at all. A retry with the same
/// key is compared with what the key created and answered with the same receipt; two concurrent
/// requests with one key race on the unique index of (submitter, key) and the loser is answered
/// the same way after the winner's row is read back.
/// </para>
/// <para>
/// Private conversation content is never an input: a submission carries only the form's answers.
/// </para>
/// </remarks>
public sealed class DatabaseSubmissionService
{
    private const string RemovedAccountName = "已停用的帳號";

    private const string NoRecordsMessage = "目前只有 0 筆紀錄，累積 2 筆以上才會顯示比較與趨勢。";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _clock;

    public DatabaseSubmissionService(AppDbContext dbContext, TimeProvider clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    /// <summary>
    /// What a member sees before submitting (提交資訊): purpose, recipient, who can actually read
    /// the record right now (<see cref="DatabaseRecordReaders.EffectiveReaderIdsAsync"/>), the
    /// sensitive-data notice and the current form. <see langword="null"/> when the database does
    /// not exist in the caller's organization.
    /// </summary>
    public async Task<DatabaseSubmissionFormView?> GetFormAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var row = await DatabaseEndpoints.WithOwnerAndCurrentForm(
                _dbContext, _dbContext.Databases.Where(database => database.Id == databaseId))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var terms = await TermsAsync(row.Database, cancellationToken);
        return new DatabaseSubmissionFormView(
            row.Database.Id,
            terms.DatabaseName,
            terms.Purpose,
            terms.Recipient,
            terms.Viewers,
            terms.SensitiveNotice,
            DatabaseSubmissionRules.WithdrawalNotice,
            new DatabaseFormView(
                row.Form.Id, row.Form.VersionNumber, row.Form.CreatedAt, [.. row.Form.Fields.Select(DatabaseFieldView.From)]));
    }

    /// <summary>
    /// The "check before consenting" step (確認同意前的預覽): validates <paramref name="answers"/>
    /// against the current form with the submission's own rule and returns what would be recorded.
    /// Writes nothing. <see langword="null"/> when the database is not in the caller's organization.
    /// </summary>
    public async Task<DatabaseSubmissionOutcome?> ReviewAsync(
        Guid databaseId,
        int? formVersionNumber,
        IReadOnlyDictionary<string, DatabaseAnswerInput> answers,
        CancellationToken cancellationToken)
    {
        var current = await _dbContext.DatabaseFormVersions.AsNoTracking()
            .Where(version => version.DatabaseId == databaseId)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return new DatabaseSubmissionOutcome.Unavailable();
        }

        if (formVersionNumber is not { } version || version < 1)
        {
            return new DatabaseSubmissionOutcome.Invalid(
                [new ValidationFailure(DatabaseSubmissionRules.FormVersionNumberKey, DatabaseSubmissionRules.FormVersionRequiredMessage)]);
        }

        if (version != current.VersionNumber)
        {
            return new DatabaseSubmissionOutcome.FormChanged(current.VersionNumber);
        }

        var validation = DatabaseAnswerRules.Validate(current.Fields, answers);
        return validation.IsValid
            ? new DatabaseSubmissionOutcome.Reviewed(current.VersionNumber, validation.Value)
            : new DatabaseSubmissionOutcome.Invalid(validation.Failures);
    }

    /// <summary>Validates and, only if everything passes, records the submission.</summary>
    public async Task<DatabaseSubmissionOutcome> SubmitAsync(DatabaseSubmissionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var database = await _dbContext.Databases.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.DatabaseId, cancellationToken);
        if (database is null)
        {
            return new DatabaseSubmissionOutcome.Unavailable();
        }

        var request = DatabaseSubmissionRules.ValidateRequest(command.SubmissionId, command.FormVersionNumber);
        if (!request.IsValid)
        {
            return new DatabaseSubmissionOutcome.Invalid(request.Failures);
        }

        var (key, versionNumber) = request.Value;

        // A retry first: it gets its original receipt even if the form has changed since.
        var replay = await ReplayAsync(command, key, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        var current = await _dbContext.DatabaseFormVersions.AsNoTracking()
            .Where(version => version.DatabaseId == database.Id)
            .OrderByDescending(version => version.VersionNumber)
            .FirstAsync(cancellationToken);
        if (versionNumber != current.VersionNumber)
        {
            return new DatabaseSubmissionOutcome.FormChanged(current.VersionNumber);
        }

        var answers = DatabaseAnswerRules.Validate(current.Fields, command.Answers);
        if (!answers.IsValid)
        {
            return new DatabaseSubmissionOutcome.Invalid(answers.Failures);
        }

        if (!DatabaseSubmissionRules.HasConsented(command.Consent))
        {
            return new DatabaseSubmissionOutcome.ConsentMissing();
        }

        var now = _clock.GetUtcNow();
        var terms = await TermsAsync(database, cancellationToken);
        var submission = DatabaseSubmission.Create(
            database, current, command.SubmitterAccountId, key, command.Source, terms, now);
        var entries = answers.Value
            .Select((entry, position) => DatabaseSubmissionEntry.Create(
                submission, position, entry.Field, entry.Display, entry.Text, entry.Number, entry.Choices))
            .ToList();

        _dbContext.DatabaseSubmissions.Add(submission);
        _dbContext.DatabaseSubmissionEntries.AddRange(entries);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsKeyConflict(exception))
        {
            // Another request with the same key won the race: forget this attempt and answer the
            // way a retry would be answered.
            _dbContext.ChangeTracker.Clear();
            return await ReplayAsync(command, key, cancellationToken)
                ?? throw new InvalidOperationException("The submission that won the key is not readable.", exception);
        }

        return new DatabaseSubmissionOutcome.Created(ToReceipt(submission, entries));
    }

    /// <summary>The receipt of <paramref name="submissionId"/> if <paramref name="submitterAccountId"/>
    /// submitted it; <see langword="null"/> otherwise (missing, another organization's or someone else's).</summary>
    public async Task<DatabaseSubmissionReceiptView?> GetReceiptAsync(
        Guid submissionId, Guid submitterAccountId, CancellationToken cancellationToken)
    {
        var submission = await _dbContext.DatabaseSubmissions.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == submissionId && candidate.SubmittedByAccountId == submitterAccountId,
                cancellationToken);
        return submission is null ? null : ToReceipt(submission, await EntriesAsync(submission.Id, cancellationToken));
    }

    /// <summary>
    /// The consented records of a database, newest first, with their content. <b>The caller must
    /// have checked <see cref="DatabaseRecordReaders.CanReadAsync"/></b> for the same request.
    /// Withdrawn submissions (no content, #146) are not records.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseSubmittedRecordView>> ListRecordsAsync(Guid databaseId, CancellationToken cancellationToken)
    {
        var active = DatabaseActiveRecords.Of(_dbContext, databaseId);
        var submissions = await active
            .OrderByDescending(submission => submission.SubmittedAt)
            .ThenByDescending(submission => submission.Id)
            .ToListAsync(cancellationToken);
        var entries = (await DatabaseActiveRecords.EntriesOf(_dbContext, active).ToListAsync(cancellationToken))
            .ToLookup(entry => entry.SubmissionId);
        var names = await NamesAsync(submissions.Select(submission => submission.SubmittedByAccountId), cancellationToken);

        return [.. submissions.Select(submission => ToRecordView(submission, names, entries[submission.Id]))];
    }

    /// <summary>
    /// The timeline of a database per tracked subject (追蹤對象 = the submitting account, M4 #146):
    /// each subject's active records with content and the content-free trails of its withdrawn
    /// submissions. Only subjects who submitted to <b>this</b> database appear, so the timeline
    /// never reaches another database's or organization's subjects. <b>The caller must have
    /// checked <see cref="DatabaseRecordReaders.CanReadAsync"/></b> for the same request;
    /// <paramref name="comparisons"/> are <see cref="DatabaseFixedQueryService.CompareAllSubjectsAsync"/>'s
    /// (a subject with only withdrawn submissions has none: nothing to compare).
    /// </summary>
    public async Task<DatabaseTrackingView> GetTrackingAsync(
        Guid databaseId,
        IReadOnlyDictionary<Guid, DatabaseSubjectComparison> comparisons,
        CancellationToken cancellationToken)
    {
        var records = await ListRecordsAsync(databaseId, cancellationToken);
        var withdrawn = await _dbContext.DatabaseSubmissions.AsNoTracking()
            .Where(submission => submission.DatabaseId == databaseId && submission.WithdrawnAt != null)
            .OrderByDescending(submission => submission.SubmittedAt)
            .ThenByDescending(submission => submission.Id)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(withdrawn.Select(submission => submission.SubmittedByAccountId), cancellationToken);

        var subjectIds = records.Select(record => record.Submitter.Id)
            .Concat(withdrawn.Select(submission => submission.SubmittedByAccountId))
            .Distinct();
        var subjects = subjectIds
            .Select(subjectId =>
            {
                var own = records.Where(record => record.Submitter.Id == subjectId).ToList();
                var trails = withdrawn.Where(submission => submission.SubmittedByAccountId == subjectId)
                    .Select(submission => new DatabaseWithdrawnRecordView(
                        submission.Id,
                        submission.SubmittedAt,
                        submission.WithdrawnAt!.Value,
                        submission.Source,
                        submission.FormVersionNumber))
                    .ToList();
                var name = own.Count > 0
                    ? own[0].Submitter.DisplayName
                    : names.GetValueOrDefault(subjectId, RemovedAccountName);
                var latest = own.Select(record => record.SubmittedAt).Concat(trails.Select(trail => trail.SubmittedAt)).Max();
                return (Latest: latest, View: new DatabaseTrackedSubjectView(
                    new DatabaseAccountView(subjectId, name),
                    own,
                    trails,
                    comparisons.GetValueOrDefault(subjectId) ?? DatabaseQueryResults.Insufficient(0, NoRecordsMessage)));
            })
            .OrderByDescending(subject => subject.Latest)
            .ThenBy(subject => subject.View.Subject.Id)
            .Select(subject => subject.View)
            .ToList();

        return new DatabaseTrackingView(databaseId, subjects);
    }

    /// <summary>The submitter's own submissions, newest first, active and withdrawn, without
    /// content (M4 #146). Only <paramref name="submitterAccountId"/>'s, in the caller's organization.</summary>
    public async Task<IReadOnlyList<DatabaseOwnSubmissionView>> ListOwnAsync(Guid submitterAccountId, CancellationToken cancellationToken)
    {
        var submissions = await _dbContext.DatabaseSubmissions.AsNoTracking()
            .Where(submission => submission.SubmittedByAccountId == submitterAccountId)
            .OrderByDescending(submission => submission.SubmittedAt)
            .ThenByDescending(submission => submission.Id)
            .ToListAsync(cancellationToken);
        return [.. submissions.Select(submission => new DatabaseOwnSubmissionView(
            submission.Id,
            submission.ReceiptNumber,
            submission.SubmittedAt,
            submission.DatabaseId,
            submission.ConsentTerms.DatabaseName,
            submission.FormVersionNumber,
            submission.Source,
            submission.WithdrawnAt))];
    }

    /// <summary>
    /// Withdraws <paramref name="submissionId"/> for its submitter (M4 #146, withdrawal ADR): in one
    /// transaction sets <c>WithdrawnAt</c> and <b>deletes every entry</b> — the answers, the typed
    /// values and the field snapshot — leaving the content-free trail (submitted, withdrawn, source,
    /// form version, receipt number, consent terms). Returns the receipt as it now reads (no
    /// entries); <see langword="null"/> when there is no such submission of this submitter in the
    /// organization (missing, someone else's, another organization's: the caller answers all alike).
    /// </summary>
    /// <remarks>
    /// Idempotent: withdrawing again changes nothing and returns the same receipt with the
    /// original <c>WithdrawnAt</c>. Concurrent withdrawals serialize on the submission row: the
    /// conditional update (<c>WHERE "WithdrawnAt" IS NULL</c>) matches for exactly one of them, the
    /// others match nothing once it commits, and all read back the same trail.
    /// </remarks>
    public async Task<DatabaseSubmissionReceiptView?> WithdrawAsync(
        Guid submissionId, Guid submitterAccountId, CancellationToken cancellationToken)
    {
        // ExecuteUpdate bypasses the SaveChanges interceptor that trims timestamps to PostgreSQL's
        // microseconds, so trim here: the value returned now equals the value read back later.
        var now = _clock.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var withdrawn = await _dbContext.DatabaseSubmissions
                .Where(submission => submission.Id == submissionId
                    && submission.SubmittedByAccountId == submitterAccountId
                    && submission.WithdrawnAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(submission => submission.WithdrawnAt, now), cancellationToken);
            if (withdrawn == 1)
            {
                await _dbContext.DatabaseSubmissionEntries
                    .Where(entry => entry.SubmissionId == submissionId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await GetReceiptAsync(submissionId, submitterAccountId, cancellationToken);
    }

    /// <summary>
    /// The answer to a request whose key this submitter already used: the original receipt when it
    /// is the same content for the same database and the member consented again, a conflict
    /// otherwise; <see langword="null"/> when the key is new.
    /// </summary>
    private async Task<DatabaseSubmissionOutcome?> ReplayAsync(DatabaseSubmissionCommand command, Guid key, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.DatabaseSubmissions.AsNoTracking()
            .SingleOrDefaultAsync(
                submission => submission.SubmittedByAccountId == command.SubmitterAccountId && submission.IdempotencyKey == key,
                cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.WithdrawnAt is not null)
        {
            // The fill this key made was withdrawn (#146). A retry of it learns that final state —
            // the withdrawn receipt, no content — and nothing is recorded again; the content cannot
            // be compared any more because it was deleted. Another database or channel is a conflict.
            return existing.DatabaseId == command.DatabaseId
                && existing.FormVersionNumber == command.FormVersionNumber
                && existing.Source == command.Source
                    ? new DatabaseSubmissionOutcome.Replayed(ToReceipt(existing, []))
                    : new DatabaseSubmissionOutcome.KeyReused();
        }

        var entries = await EntriesAsync(existing.Id, cancellationToken);
        if (existing.DatabaseId != command.DatabaseId
            || existing.FormVersionNumber != command.FormVersionNumber
            || existing.Source != command.Source
            || !DatabaseSubmissionRules.HasConsented(command.Consent)
            || entries.Count == 0)
        {
            return new DatabaseSubmissionOutcome.KeyReused();
        }

        var version = await _dbContext.DatabaseFormVersions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == existing.FormVersionId, cancellationToken);
        var answers = DatabaseAnswerRules.Validate(version.Fields, command.Answers);
        return answers.IsValid && DatabaseSubmissionRules.IsSameContent(entries, answers.Value)
            ? new DatabaseSubmissionOutcome.Replayed(ToReceipt(existing, entries))
            : new DatabaseSubmissionOutcome.KeyReused();
    }

    private async Task<List<DatabaseSubmissionEntry>> EntriesAsync(Guid submissionId, CancellationToken cancellationToken) =>
        await _dbContext.DatabaseSubmissionEntries.AsNoTracking()
            .Where(entry => entry.SubmissionId == submissionId)
            .OrderBy(entry => entry.Position)
            .ToListAsync(cancellationToken);

    /// <summary>The consent terms for <paramref name="database"/> as it is right now.</summary>
    private async Task<DatabaseConsentTerms> TermsAsync(Database database, CancellationToken cancellationToken)
    {
        var organizationName = await _dbContext.Organizations.AsNoTracking()
            .Where(organization => organization.Id == database.OrganizationId)
            .Select(organization => organization.Name)
            .SingleAsync(cancellationToken);
        var readerIds = await DatabaseRecordReaders.EffectiveReaderIdsAsync(_dbContext, database.Id, cancellationToken);
        var designations = await _dbContext.DatabaseDataManagers.AsNoTracking()
            .Where(designation => designation.DatabaseId == database.Id)
            .Select(designation => new { designation.AccountId, designation.AssignedAt })
            .ToListAsync(cancellationToken);
        var readers = designations.Where(designation => readerIds.Contains(designation.AccountId))
            .OrderBy(designation => designation.AssignedAt)
            .ThenBy(designation => designation.AccountId)
            .Select(designation => designation.AccountId)
            .ToList();
        var names = await _dbContext.Accounts.AsNoTracking()
            .Where(account => readers.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken);

        return DatabaseSubmissionRules.TermsFor(
            organizationName,
            database.Name,
            database.Purpose,
            [.. readers.Select(id => names.GetValueOrDefault(id, RemovedAccountName))]);
    }

    private static bool IsKeyConflict(DbUpdateException exception) =>
        DatabaseErrors.IsUniqueViolation(exception, DatabaseSubmissionIndexes.IdempotencyKey);

    private static DatabaseSubmissionReceiptView ToReceipt(DatabaseSubmission submission, IReadOnlyList<DatabaseSubmissionEntry> entries) =>
        new(
            submission.Id,
            submission.ReceiptNumber,
            submission.SubmittedAt,
            submission.DatabaseId,
            submission.ConsentTerms.DatabaseName,
            submission.ConsentTerms.Purpose,
            submission.ConsentTerms.Recipient,
            [.. submission.ConsentTerms.Viewers],
            submission.FormVersionId,
            submission.FormVersionNumber,
            submission.Source,
            ToEntryViews(entries),
            submission.WithdrawnAt);

    private static DatabaseSubmittedRecordView ToRecordView(
        DatabaseSubmission submission, IReadOnlyDictionary<Guid, string> names, IEnumerable<DatabaseSubmissionEntry> entries) =>
        new(
            submission.Id,
            submission.ReceiptNumber,
            submission.SubmittedAt,
            submission.Source,
            new DatabaseAccountView(
                submission.SubmittedByAccountId, names.GetValueOrDefault(submission.SubmittedByAccountId, RemovedAccountName)),
            submission.FormVersionNumber,
            ToEntryViews(entries));

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToList();
        return await _dbContext.Accounts.AsNoTracking()
            .Where(account => ids.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, cancellationToken);
    }

    private static List<DatabaseSubmissionEntryView> ToEntryViews(IEnumerable<DatabaseSubmissionEntry> entries) =>
        [.. entries.OrderBy(entry => entry.Position)
            .Select(entry => new DatabaseSubmissionEntryView(entry.FieldId, entry.Label, entry.FieldType, entry.Display))];
}
