using Microsoft.EntityFrameworkCore;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Application.Assistants;
using SmartAgri.Domain.Accounts;
using SmartAgri.Domain.Assistants;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Assistants;

/// <summary>What a member is told before consenting, shaped like the frontend's
/// <c>ChatConsentView</c>.</summary>
/// <param name="Purpose">The assistant's collection purpose (<c>rules.dataWritePurpose</c>).</param>
/// <param name="Viewers">Who can actually read the record right now (designated data managers
/// holding <c>read-consented-submissions</c>), by name.</param>
public sealed record ChatFormConsentView(
    string Recipient,
    string Purpose,
    IReadOnlyList<string> Viewers,
    string SensitiveNotice,
    string WithdrawalNotice);

/// <summary>
/// A form request's form (M4 #148), shaped like the frontend's <c>ChatFormView</c> plus the
/// version: <see cref="Id"/> is the database, <see cref="FormVersion"/> goes back with the review
/// and the submission so a form changed in between is a <c>409</c>, not a silent mismatch.
/// Everything here comes from the server — the current form version and the consent terms.
/// </summary>
public sealed record ChatFormRequestView(
    Guid Id,
    string Title,
    int FormVersion,
    IReadOnlyList<DatabaseFieldView> Fields,
    ChatFormConsentView Consent);

/// <summary>
/// The one place that decides whether an assistant may use a connected database for a form right
/// now (M4 #148), and builds what the member sees. Every decision is made for <b>this</b> request:
/// the connection row, the database (organization query filter) and whether the assistant's owner
/// may still use it (<see cref="AssistantDatabaseAccess.ConnectableBy"/>, with the owner's
/// permission read through <see cref="RequestAccountPermissions"/>). Nothing is cached, so a
/// revoked designation or permission, a disconnected or deleted database, or a changed form target
/// stops new form requests and submissions on the next request.
/// </summary>
/// <remarks>
/// <b>Authorization order</b> for every form entry point: signed in → the caller may use the
/// assistant (<c>ChatEndpoints.FindUsableAsync</c>, <c>403 assistant-use</c>) → the conversation, when
/// one is named, is the caller's own (<c>403 chat-thread</c>) → <see cref="FormTargetAsync"/>: the
/// database is the assistant's connected form target and its owner may still use it
/// (<c>403 assistant-form</c>) → <see cref="DatabaseSubmissionService"/> (form version, answers,
/// consent, idempotency).
/// </remarks>
public sealed class AssistantFormRequests
{
    private readonly AppDbContext _dbContext;
    private readonly RequestAccountPermissions _permissions;
    private readonly DatabaseSubmissionService _submissions;

    public AssistantFormRequests(AppDbContext dbContext, RequestAccountPermissions permissions, DatabaseSubmissionService submissions)
    {
        _dbContext = dbContext;
        _permissions = permissions;
        _submissions = submissions;
    }

    /// <summary>The ids of the databases connected to <paramref name="assistant"/> that its owner
    /// may use right now.</summary>
    public async Task<IReadOnlyList<Guid>> UsableDatabaseIdsAsync(Assistant assistant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        var connectable = await ConnectableDatabasesAsync(assistant.OwnerAccountId, cancellationToken);
        return await _dbContext.AssistantDatabases.AsNoTracking()
            .Where(link => link.AssistantId == assistant.Id)
            .Where(link => connectable.Any(database => database.Id == link.DatabaseId))
            .Select(link => link.DatabaseId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The assistant's form target if it is <paramref name="databaseId"/> (or any target, when
    /// <paramref name="databaseId"/> is <see langword="null"/>) and its owner may still use the
    /// database; <see langword="null"/> otherwise — not connected, not the target, revoked, deleted
    /// or another organization's, all alike.
    /// </summary>
    public async Task<AssistantDatabase?> FormTargetAsync(Assistant assistant, Guid? databaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        var connectable = await ConnectableDatabasesAsync(assistant.OwnerAccountId, cancellationToken);
        return await _dbContext.AssistantDatabases.AsNoTracking()
            .Where(link => link.AssistantId == assistant.Id && link.CollectsForms)
            .Where(link => databaseId == null || link.DatabaseId == databaseId)
            .Where(link => connectable.Any(database => database.Id == link.DatabaseId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>The form request <paramref name="assistant"/> may show right now for
    /// <paramref name="databaseId"/> (any target when <see langword="null"/>), or
    /// <see langword="null"/> when it may not (see <see cref="FormTargetAsync"/>).</summary>
    public async Task<ChatFormRequestView?> FormRequestAsync(Assistant assistant, Guid? databaseId, CancellationToken cancellationToken)
    {
        var target = await FormTargetAsync(assistant, databaseId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        var form = await _submissions.GetFormAsync(target.DatabaseId, target.CollectionPurpose, cancellationToken);
        return form is null
            ? null
            : new ChatFormRequestView(
                form.DatabaseId,
                form.DatabaseName,
                form.Form.VersionNumber,
                form.Form.Fields,
                new ChatFormConsentView(form.Recipient, form.Purpose, form.Viewers, form.SensitiveNotice, form.WithdrawalNotice));
    }

    private async Task<IQueryable<Domain.Databases.Database>> ConnectableDatabasesAsync(
        Guid ownerAccountId, CancellationToken cancellationToken)
    {
        var ownerPermissions = await _permissions.GetAsync(ownerAccountId, cancellationToken);
        var ownerMayRead = ownerPermissions.Contains(AccountPermission.ReadConsentedSubmissions);
        return _dbContext.Databases.Where(
            AssistantDatabaseAccess.ConnectableBy(ownerAccountId, ownerMayRead, _dbContext.DatabaseDataManagers));
    }
}
