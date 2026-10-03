using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SmartAgri.Api.Assistants;
using SmartAgri.Api.Authorization;
using SmartAgri.Api.Databases;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Databases;
using SmartAgri.Domain;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Assistants;
using SmartAgri.Domain.Observability;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Chat;

/// <summary>Runs one fixed query for the conversation tool. <see cref="DatabaseFixedQueryService"/>
/// behind a seam, so a test can make the tool itself fail.</summary>
public interface IChatDatabaseQueryRunner
{
    Task<DatabaseQueryOutcome<object>> RunAsync(
        DatabaseQueryKind kind,
        Guid accountId,
        Guid databaseId,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken);
}

internal sealed class FixedQueryChatRunner(DatabaseFixedQueryService queries) : IChatDatabaseQueryRunner
{
    public Task<DatabaseQueryOutcome<object>> RunAsync(
        DatabaseQueryKind kind,
        Guid accountId,
        Guid databaseId,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken) =>
        queries.RunAsync(kind, accountId, databaseId, parameters, cancellationToken);
}

/// <summary>The databases one question may query: connected to the assistant, usable by its owner,
/// and readable by the asker — all as of this request. Empty when the assistant has usable databases
/// but none the asker may read.</summary>
public sealed record ChatDatabaseQueryScope(IReadOnlyList<DatabaseQueryToolSource> Sources);

/// <summary>
/// The conversation's database query tool (M4 #149), in the orchestration layer: the model chooses
/// a fixed query and its parameters; the server checks them and runs the query <b>as the asking
/// member</b>, and composes the answer from the result itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order of checks</b> (every request, nothing cached): (1) the assistant's usable databases
/// (<see cref="AssistantFormRequests.UsableDatabaseIdsAsync"/>: connected and still usable by the
/// assistant's owner) — none means this assistant has no database to query and the question is
/// answered as usual; (2) of those, the ones the asker may read now
/// (<see cref="DatabaseRecordReaders.ReadableDatabaseIdsAsync"/>: designated data manager holding
/// <c>read-consented-submissions</c>) — none is <c>not-available</c> without a model call; (3) the
/// model is offered only those (<see cref="DatabaseQueryTools.Declarations"/>) and calls at most one
/// tool; (4) the tool name and database must be offered ones (an unknown tool is <c>rejected</c>, an
/// unoffered database <c>not-available</c>); (5) <see cref="DatabaseFixedQueryService.RunAsync"/>
/// with the asker's account re-checks readability and validates the parameters
/// (<see cref="DatabaseFixedQueries.Validate"/>: anything outside the definition is <c>rejected</c>);
/// (6) <see cref="DatabaseQueryTools.Compose"/> writes the reply from the result.
/// </para>
/// <para>
/// <b>Usage.</b> The selection call goes through the recording chat client like every model call
/// (<see cref="ModelInvocationPurpose.DatabaseQuery"/>, attributed to the asker and assistant; no
/// content). The tool call itself is one <see cref="ActivityName"/> span (query, status) — never its
/// parameters or result. A model failure is a <see cref="ChatGenerationException"/>, like the answer
/// pipeline's; a tool failure is a <c>failed</c> reply.
/// </para>
/// </remarks>
public sealed class ChatDatabaseQueries
{
    public const string ActivityName = "smartagri.chat.database_query";

    private readonly AppDbContext _dbContext;
    private readonly RequestAccountPermissions _permissions;
    private readonly AssistantFormRequests _assistantDatabases;
    private readonly IChatDatabaseQueryRunner _runner;
    private readonly IChatClient _chat;
    private readonly TimeProvider _clock;
    private readonly StatisticsOptions _statistics;
    private readonly ILogger<ChatDatabaseQueries> _logger;

    public ChatDatabaseQueries(
        AppDbContext dbContext,
        RequestAccountPermissions permissions,
        AssistantFormRequests assistantDatabases,
        IChatDatabaseQueryRunner runner,
        IChatClient chat,
        TimeProvider clock,
        IOptions<StatisticsOptions> statistics,
        ILogger<ChatDatabaseQueries> logger)
    {
        _dbContext = dbContext;
        _permissions = permissions;
        _assistantDatabases = assistantDatabases;
        _runner = runner;
        _chat = chat;
        _clock = clock;
        _statistics = statistics.Value;
        _logger = logger;
    }

    /// <summary>Checks (1) and (2): <see langword="null"/> when the assistant has no database it may
    /// use now; otherwise the databases the asker may query (possibly none).</summary>
    public async Task<ChatDatabaseQueryScope?> ScopeAsync(Assistant assistant, Guid askerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        var usable = await _assistantDatabases.UsableDatabaseIdsAsync(assistant, cancellationToken);
        if (usable.Count == 0)
        {
            return null;
        }

        var readable = (await DatabaseRecordReaders.ReadableDatabaseIdsAsync(_dbContext, _permissions, askerId, cancellationToken)).ToHashSet();
        var ids = usable.Where(readable.Contains).ToList();
        if (ids.Count == 0)
        {
            return new ChatDatabaseQueryScope([]);
        }

        var databases = await _dbContext.Databases.AsNoTracking()
            .Where(database => ids.Contains(database.Id))
            .OrderBy(database => database.Name).ThenBy(database => database.Id)
            .Select(database => new { database.Id, database.Name })
            .ToListAsync(cancellationToken);
        var versions = await _dbContext.DatabaseFormVersions.AsNoTracking()
            .Where(version => ids.Contains(version.DatabaseId))
            .Select(version => new { version.DatabaseId, version.VersionNumber, version.Fields })
            .ToListAsync(cancellationToken);
        var current = versions
            .GroupBy(version => version.DatabaseId)
            .ToDictionary(group => group.Key, group => group.MaxBy(version => version.VersionNumber)!.Fields);

        return new ChatDatabaseQueryScope(
        [
            .. databases.Select(database => new DatabaseQueryToolSource(
                database.Id,
                database.Name,
                [
                    .. (current.TryGetValue(database.Id, out var fields) ? fields : [])
                        .Select(field => new DatabaseQueryToolField(field.Id, field.Label, field.Type, field.Unit)),
                ])),
        ]);
    }

    /// <summary>The ids of the databases whose saved query answers the asker may see again in this
    /// assistant's conversation: usable by the assistant and readable by the asker, now.</summary>
    public async Task<IReadOnlySet<Guid>> VisibleDatabaseIdsAsync(Assistant assistant, Guid askerId, CancellationToken cancellationToken)
    {
        var usable = await _assistantDatabases.UsableDatabaseIdsAsync(assistant, cancellationToken);
        if (usable.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var readable = await DatabaseRecordReaders.ReadableDatabaseIdsAsync(_dbContext, _permissions, askerId, cancellationToken);
        return usable.Intersect(readable).ToHashSet();
    }

    /// <summary>
    /// Checks (3)–(6) for <paramref name="question"/>. <see langword="null"/> when the model called no
    /// tool (the question is answered as usual); otherwise the reply. Throws
    /// <see cref="ChatGenerationException"/> when the model call fails.
    /// </summary>
    public async Task<ChatDatabaseQueryAnswer?> AnswerAsync(
        ChatDatabaseQueryScope scope, string question, Guid askerId, Guid assistantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var activity = SmartAgriActivitySource.Instance.StartActivity(ActivityName);
        if (scope.Sources.Count == 0)
        {
            return Finish(activity, null, DatabaseQueryTools.NotAvailable());
        }

        var timeZone = _statistics.TryResolve() ?? throw new InvalidOperationException("Statistics:TimeZone was validated at startup.");
        var today = DatabaseFixedQueries.DayOf(_clock.GetUtcNow(), timeZone);
        var options = new ModelInvocationAttribution(ModelInvocationPurpose.DatabaseQuery, askerId, assistantId).ToChatOptions();
        options.Tools = [.. DatabaseQueryTools.Declarations(scope.Sources)];
        options.ToolMode = ChatToolMode.Auto;
        options.AllowMultipleToolCalls = false;

        ChatResponse response;
        try
        {
            response = await _chat.GetResponseAsync(
                DatabaseQueryTools.SelectionPrompt(question, today, _statistics.TimeZone), options, cancellationToken);
        }
        catch (Exception exception) when (exception is not ChatGenerationException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new ChatGenerationException(providerNotConfigured: false, exception);
        }

        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().FirstOrDefault();
        if (call is null)
        {
            activity?.SetTag("smartagri.database_query.status", "no-tool");
            return null;
        }

        var parsed = DatabaseQueryTools.Parse(call.Name, call.Arguments, scope.Sources);
        if (parsed.Match == DatabaseQueryToolCallMatch.UnknownTool)
        {
            return Finish(activity, null, DatabaseQueryTools.Rejected(null));
        }

        if (parsed.Call is not { } matched)
        {
            return Finish(activity, null, DatabaseQueryTools.NotAvailable());
        }

        DatabaseQueryOutcome<object> outcome;
        try
        {
            outcome = await _runner.RunAsync(matched.Kind, askerId, matched.Source.DatabaseId, matched.Parameters, cancellationToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(exception, "A conversation's fixed query {Query} failed.", WireNames<DatabaseQueryKind>.ToWire(matched.Kind));
            activity?.SetStatus(ActivityStatusCode.Error);
            return Finish(activity, matched, DatabaseQueryTools.Failed());
        }

        var answer = !outcome.Readable ? DatabaseQueryTools.NotAvailable()
            : outcome.Failures.Count > 0 ? DatabaseQueryTools.Rejected(matched)
            : DatabaseQueryTools.Compose(matched, outcome.Value);
        return Finish(activity, matched, answer);
    }

    private static ChatDatabaseQueryAnswer Finish(Activity? activity, DatabaseQueryToolCall? call, ChatDatabaseQueryAnswer answer)
    {
        if (call is not null)
        {
            activity?.SetTag("smartagri.database_query.query", WireNames<DatabaseQueryKind>.ToWire(call.Kind));
        }

        activity?.SetTag("smartagri.database_query.status", WireNames<ChatDatabaseQueryStatus>.ToWire(answer.View.Status));
        return answer;
    }
}
