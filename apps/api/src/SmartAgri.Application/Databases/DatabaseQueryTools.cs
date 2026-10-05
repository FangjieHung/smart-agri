using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using SmartAgri.Domain;
using SmartAgri.Domain.Databases;

namespace SmartAgri.Application.Databases;

/// <summary>A field of a database a conversation may query, as listed to the model: its id
/// (the only thing the model may send back), label, type and unit.</summary>
public sealed record DatabaseQueryToolField(string Id, string Label, DatabaseFieldType Type, string Unit);

/// <summary>A database a conversation may query right now: connected to the assistant, usable by
/// its owner, and readable by the asking member (M4 #149). <see cref="Fields"/> is the current
/// form's fields.</summary>
public sealed record DatabaseQueryToolSource(Guid DatabaseId, string Name, IReadOnlyList<DatabaseQueryToolField> Fields);

/// <summary>What the model asked for, once the tool name and database were matched against what
/// was offered: the fixed query, the database, and the parameters exactly as the model sent them
/// (still to be validated by <see cref="DatabaseFixedQueries.Validate"/>).</summary>
public sealed record DatabaseQueryToolCall(
    DatabaseQueryKind Kind, DatabaseQueryToolSource Source, IReadOnlyDictionary<string, string?> Parameters);

/// <summary>How a model's tool call matched what was offered.</summary>
public enum DatabaseQueryToolCallMatch
{
    /// <summary>A fixed query tool and an offered database: <see cref="DatabaseQueryToolCallParse.Call"/> is set.</summary>
    Matched,

    /// <summary>The tool is not one of the fixed query tools (e.g. a made-up "run_sql").</summary>
    UnknownTool,

    /// <summary>The database is missing, not a GUID, or not one of those offered — the same answer as
    /// a database the member may not read (indistinguishable on purpose).</summary>
    DatabaseNotOffered,
}

public sealed record DatabaseQueryToolCallParse(DatabaseQueryToolCallMatch Match, DatabaseQueryToolCall? Call);

/// <summary>The outcome of one conversation query, as the reply shows it (<c>ChatReplyView.databaseQuery</c>).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ChatDatabaseQueryStatus>))]
public enum ChatDatabaseQueryStatus
{
    /// <summary>The query ran and found records: the figures are the server's.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("answered")]
    Answered,

    /// <summary>The query ran and the period holds no record (the figures are zero).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("no-data")]
    NoData,

    /// <summary>Too few records to compare (fewer than two, or no field with two values).</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("insufficient-data")]
    InsufficientData,

    /// <summary>No database this member may query through this assistant now, or one that does not
    /// exist, is another organization's, was disconnected or revoked — all the same answer.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("not-available")]
    NotAvailable,

    /// <summary>The model asked for something outside the fixed queries' definitions (an unknown
    /// tool, parameter or value): nothing ran.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>The query itself failed (e.g. the database was unreachable): nothing to show.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>One number of an answer: the metric's name and the server's value with its formatted
/// text, plus the previous period's (or the previous record's, for a comparison) and the change
/// when there is one. <see cref="Display"/> is exactly the text the answer quotes.</summary>
public sealed record ChatDatabaseQueryFigure(
    string Metric,
    double Value,
    string Display,
    string? PreviousDisplay,
    string? ChangeLabel);

/// <summary>
/// The structured part of a <c>database-query</c> reply: what was queried (source database and
/// fixed query), over which period, and the figures — all taken from the fixed query's result, so
/// the numbers on screen are the server's, never a model's. For <c>not-available</c> /
/// <c>failed</c> everything but the status is <see langword="null"/>/empty; <c>rejected</c> keeps the
/// source and query the model named (a database the member may read) but no figures.
/// </summary>
public sealed record ChatDatabaseQueryView(
    ChatDatabaseQueryStatus Status,
    Guid? DatabaseId,
    string? DatabaseName,
    string? Query,
    string? QueryLabel,
    DatabaseQueryPeriodView? Period,
    DatabaseQueryPeriodView? PreviousPeriod,
    bool SubjectOnly,
    IReadOnlyList<ChatDatabaseQueryFigure> Figures,
    string? Message);

/// <summary>A conversation query's reply: the text (composed by the server from the result) and
/// the structured view.</summary>
public sealed record ChatDatabaseQueryAnswer(string Text, ChatDatabaseQueryView View);

/// <summary>
/// The fixed statistics queries (#147) as tools a model may choose in a conversation (M4 #149): the
/// tool definitions, matching a model's tool call back to a fixed query, and the reply built from
/// the result. Pure — no database, no model — so it is the same for every caller and unit tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tools.</b> One tool per fixed query (<see cref="ToolName"/>). Each tool's parameters are
/// <c>databaseId</c> (an <c>enum</c> of the databases this member may query through this assistant
/// right now) plus <b>only</b> the parameters that query defines (<see cref="DatabaseFixedQueries.Definitions"/>):
/// <c>period</c> (an <c>enum</c> of the named periods), <c>from</c>/<c>to</c>, <c>fieldId</c> (an <c>enum</c>
/// of the fields that query accepts) and <c>subjectId</c>; <c>additionalProperties</c> is false. There
/// is no tool, parameter or value that carries SQL or an expression.
/// </para>
/// <para>
/// <b>Numbers.</b> The model only chooses; it never sees the result. The reply's text and figures
/// are composed here from the result record (<see cref="Compose"/>), so the numbers shown are the
/// server's, character for character (<c>display</c>, <c>changeLabel</c>, <c>period.label</c>).
/// </para>
/// </remarks>
public static partial class DatabaseQueryTools
{
    public const string DatabaseIdParameter = "databaseId";

    /// <summary>What a question must contain for the orchestration layer to offer the query tools
    /// (counting, totals, statistics); the model still decides whether to call one.</summary>
    private static readonly string[] StatisticsWords =
    [
        "幾筆", "幾次", "幾件", "多少筆", "多少次", "多少件", "筆數", "次數", "件數",
        "總共", "一共", "共有", "加總", "合計", "總計", "總和", "統計", "趨勢",
    ];

    public const string NotAvailableText =
        "目前無法查詢：這個助理沒有連接你可以查看的數據庫，或你的查詢權限已被收回。如需這些數字，請洽數據庫的資料管理者。";

    public const string RejectedText =
        "這個問題需要的查詢條件不在可以查詢的範圍內（統計期間、欄位或追蹤對象），所以沒有執行查詢。請換個說法，例如「近 30 天有幾筆紀錄？」或「本月的數量加總是多少？」。";

    public const string FailedText = "查詢數據庫時發生錯誤，這次沒有取得任何數字。請稍後再問一次。";

    /// <summary>What a saved query reply is replaced with in the history a later model call sees:
    /// query results never go to the model (they are composed by the server and stay out of prompts).</summary>
    public const string HistoryPlaceholder = "（數據庫查詢結果，內容不提供給模型）";

    /// <summary>The tool name of each fixed query (letters, digits and underscores only, as every
    /// provider accepts).</summary>
    public static string ToolName(DatabaseQueryKind kind) => "database_" + WireNames<DatabaseQueryKind>.ToWire(kind).Replace('-', '_');

    public static string QueryLabel(DatabaseQueryKind kind) => kind switch
    {
        DatabaseQueryKind.RecordCount => "紀錄筆數",
        DatabaseQueryKind.FieldSum => "欄位加總",
        DatabaseQueryKind.PeriodSummary => "期間統計",
        DatabaseQueryKind.SubjectComparison => "追蹤對象比較",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a declared query."),
    };

    /// <summary>The fixed query a tool name stands for, or <see langword="null"/>.</summary>
    public static DatabaseQueryKind? KindOf(string? toolName) =>
        DatabaseFixedQueries.Definitions.Select(definition => (DatabaseQueryKind?)definition.Kind)
            .FirstOrDefault(kind => string.Equals(ToolName(kind!.Value), toolName, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="question"/> asks for a count, a total or statistics;
    /// whitespace is ignored.</summary>
    public static bool AsksForStatistics(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var normalized = Whitespace().Replace(question, string.Empty);
        return StatisticsWords.Any(word => normalized.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>The fields a query's <c>fieldId</c> accepts: numbers for a sum, numbers and scales
    /// for a comparison.</summary>
    private static bool Accepts(DatabaseQueryKind kind, DatabaseFieldType type) => kind switch
    {
        DatabaseQueryKind.FieldSum => type == DatabaseFieldType.Number,
        DatabaseQueryKind.SubjectComparison => type is DatabaseFieldType.Number or DatabaseFieldType.Scale,
        _ => false,
    };

    /// <summary>
    /// The tools offered for <paramref name="sources"/> (at least one): one per fixed query, except
    /// <c>field-sum</c> when no source has a number field.
    /// </summary>
    public static IReadOnlyList<AIFunctionDeclaration> Declarations(IReadOnlyList<DatabaseQueryToolSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one database must be offered.", nameof(sources));
        }

        var tools = new List<AIFunctionDeclaration>();
        foreach (var definition in DatabaseFixedQueries.Definitions)
        {
            var schema = Schema(definition, sources);
            if (schema is null)
            {
                continue;
            }

            tools.Add(AIFunctionFactory.CreateDeclaration(
                ToolName(definition.Kind), Description(definition, sources), schema.Value));
        }

        return tools;
    }

    private static string Description(DatabaseQueryDefinition definition, IReadOnlyList<DatabaseQueryToolSource> sources)
    {
        var databases = string.Join("；", sources.Select(source => $"{source.DatabaseId}＝「{source.Name}」"));
        return $"{definition.Description}只能查詢這些數據庫：{databases}。伺服器會依提問者的權限執行，不接受 SQL 或運算式。";
    }

    private static JsonElement? Schema(DatabaseQueryDefinition definition, IReadOnlyList<DatabaseQueryToolSource> sources)
    {
        var properties = new JsonObject
        {
            [DatabaseIdParameter] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "要查詢的數據庫 id。",
                ["enum"] = new JsonArray([.. sources.Select(source => JsonValue.Create(source.DatabaseId.ToString()))]),
            },
        };
        var required = new JsonArray(JsonValue.Create(DatabaseIdParameter));

        foreach (var parameter in definition.AllowedParameters)
        {
            JsonObject? property = parameter switch
            {
                DatabaseFixedQueries.PeriodKey => new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "具名的統計期間（以統計時區的曆日計算）；與 from／to 擇一。",
                    ["enum"] = new JsonArray([.. WireNames<DatabaseQueryPeriodName>.All.Select(name => JsonValue.Create(name))]),
                },
                DatabaseFixedQueries.FromKey => DateProperty("自訂期間的起始日（含），yyyy-MM-dd；與 to 一起指定。"),
                DatabaseFixedQueries.ToKey => DateProperty("自訂期間的結束日（含），yyyy-MM-dd；與 from 一起指定。"),
                DatabaseFixedQueries.FieldIdKey => FieldProperty(definition.Kind, sources),
                DatabaseFixedQueries.SubjectIdKey => new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "追蹤對象（提交紀錄的帳號）的 id；只能使用對話中已提供的 id，不要自行編造。",
                },
                _ => throw new InvalidOperationException($"No schema for the parameter '{parameter}'."),
            };

            if (property is null)
            {
                if (definition.RequiredParameters.Contains(parameter))
                {
                    // A required field with nothing to choose from: the tool cannot be used at all.
                    return null;
                }

                continue;
            }

            properties[parameter] = property;
            if (definition.RequiredParameters.Contains(parameter))
            {
                required.Add(JsonValue.Create(parameter));
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject DateProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["pattern"] = "^\\d{4}-\\d{2}-\\d{2}$",
    };

    private static JsonObject? FieldProperty(DatabaseQueryKind kind, IReadOnlyList<DatabaseQueryToolSource> sources)
    {
        var fields = sources
            .SelectMany(source => source.Fields.Where(field => Accepts(kind, field.Type)).Select(field => (source, field)))
            .ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        var listing = string.Join("；", fields.Select(pair =>
            $"「{pair.source.Name}」的 {pair.field.Id}＝{pair.field.Label}" + (pair.field.Unit.Length > 0 ? $"（{pair.field.Unit}）" : string.Empty)));
        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = $"欄位 id：{listing}。",
            ["enum"] = new JsonArray([.. fields.Select(pair => pair.field.Id).Distinct(StringComparer.Ordinal).Select(id => JsonValue.Create(id))]),
        };
    }

    /// <summary>
    /// Matches a model's tool call against what was offered. The arguments become the parameter
    /// dictionary <see cref="DatabaseFixedQueries.Validate"/> checks — every key but
    /// <c>databaseId</c> is passed on as is, so an undefined one is rejected there, not dropped
    /// here. A value that is not a JSON string becomes its raw JSON text (and fails validation).
    /// </summary>
    public static DatabaseQueryToolCallParse Parse(
        string toolName, IDictionary<string, object?>? arguments, IReadOnlyList<DatabaseQueryToolSource> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        if (KindOf(toolName) is not { } kind)
        {
            return new DatabaseQueryToolCallParse(DatabaseQueryToolCallMatch.UnknownTool, null);
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments ?? new Dictionary<string, object?>())
        {
            values[key] = AsText(value);
        }

        if (!values.Remove(DatabaseIdParameter, out var rawDatabaseId)
            || !Guid.TryParse(rawDatabaseId, out var databaseId)
            || offered.FirstOrDefault(source => source.DatabaseId == databaseId) is not { } source)
        {
            return new DatabaseQueryToolCallParse(DatabaseQueryToolCallMatch.DatabaseNotOffered, null);
        }

        return new DatabaseQueryToolCallParse(DatabaseQueryToolCallMatch.Matched, new DatabaseQueryToolCall(kind, source, values));
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement element => element.GetRawText(),
        JsonNode node => node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : node.ToJsonString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>The messages of the tool-selection call: the role (choose a fixed query or none,
    /// never SQL), today's date in the statistics time zone, and the question. Nothing from the
    /// databases' records is in them — only the tool definitions (names, fields) go with the call.</summary>
    public static IReadOnlyList<ChatMessage> SelectionPrompt(string question, DateOnly today, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var system =
            "你是農業與小型企業助理的查詢工具選擇器。若使用者在問已連接數據庫的紀錄筆數、加總、期間統計或追蹤對象的變化，" +
            "請從提供的固定查詢工具中選擇一個，並只填入工具定義內的參數；不要產生 SQL、運算式或任何未定義的參數。" +
            "若問題與這些數據庫的統計無關，請不要呼叫工具，直接回覆「不需要查詢」。" +
            $"今天是 {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}（{timeZoneId}）；未指定期間時使用 last-30-days。";
        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, question.Trim())];
    }

    public static ChatDatabaseQueryAnswer NotAvailable() =>
        new(NotAvailableText, new ChatDatabaseQueryView(ChatDatabaseQueryStatus.NotAvailable, null, null, null, null, null, null, false, [], null));

    public static ChatDatabaseQueryAnswer Failed() =>
        new(FailedText, new ChatDatabaseQueryView(ChatDatabaseQueryStatus.Failed, null, null, null, null, null, null, false, [], null));

    /// <summary>Parameters outside the definition. With <paramref name="call"/> (the tool and the
    /// database were offered) the reply names them; without (an unknown tool) it names nothing.</summary>
    public static ChatDatabaseQueryAnswer Rejected(DatabaseQueryToolCall? call) =>
        new(RejectedText, new ChatDatabaseQueryView(
            ChatDatabaseQueryStatus.Rejected,
            call?.Source.DatabaseId,
            call?.Source.Name,
            call is null ? null : WireNames<DatabaseQueryKind>.ToWire(call.Kind),
            call is null ? null : QueryLabel(call.Kind),
            null,
            null,
            false,
            [],
            null));

    /// <summary>The reply for a fixed query's <paramref name="result"/> (the kind's result record).
    /// Every number in the text is a <c>display</c>/<c>changeLabel</c> of the result, and the same
    /// strings are the figures.</summary>
    public static ChatDatabaseQueryAnswer Compose(DatabaseQueryToolCall call, object result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        var source = $"根據「{call.Source.Name}」的{QueryLabel(call.Kind)}查詢";
        return result switch
        {
            DatabaseRecordCountResult count => ComposeCount(call, source, count),
            DatabaseFieldSumResult sum => ComposeSum(call, source, sum),
            DatabasePeriodSummaryResult summary => ComposeSummary(call, source, summary),
            DatabaseSubjectComparisonResult comparison => ComposeComparison(call, source, comparison),
            _ => throw new ArgumentException($"Not a fixed query result: {result.GetType().Name}.", nameof(result)),
        };
    }

    private static string Scope(bool subjectOnly) => subjectOnly ? "（只算這位追蹤對象）" : string.Empty;

    private static string Previous(DatabaseQueryPeriodView previous) => $"前一期（{previous.From} 至 {previous.To}）";

    private static ChatDatabaseQueryView View(
        DatabaseQueryToolCall call,
        ChatDatabaseQueryStatus status,
        DatabaseQueryPeriodView? period,
        DatabaseQueryPeriodView? previous,
        bool subjectOnly,
        IReadOnlyList<ChatDatabaseQueryFigure> figures,
        string? message) =>
        new(status, call.Source.DatabaseId, call.Source.Name, WireNames<DatabaseQueryKind>.ToWire(call.Kind), QueryLabel(call.Kind),
            period, previous, subjectOnly, figures, message);

    private static string CountDisplay(int count) => DatabaseAnswerRules.FormatNumber(count, DatabaseQueryResults.RecordUnit);

    private static ChatDatabaseQueryAnswer ComposeCount(DatabaseQueryToolCall call, string source, DatabaseRecordCountResult result)
    {
        var subjectOnly = result.SubjectId is not null;
        var figure = new ChatDatabaseQueryFigure("有效紀錄筆數", result.Count, CountDisplay(result.Count), CountDisplay(result.PreviousCount), result.ChangeLabel);
        var status = result.Count == 0 ? ChatDatabaseQueryStatus.NoData : ChatDatabaseQueryStatus.Answered;
        var text = status == ChatDatabaseQueryStatus.NoData
            ? $"{source}：{result.Period.Label}沒有任何有效紀錄{Scope(subjectOnly)}，共 {figure.Display}；{Previous(result.PreviousPeriod)}為 {figure.PreviousDisplay}。"
            : $"{source}：{result.Period.Label}共有 {figure.Display}有效紀錄{Scope(subjectOnly)}；{Previous(result.PreviousPeriod)}為 {figure.PreviousDisplay}，變化 {result.ChangeLabel}。";
        return new(text, View(call, status, result.Period, result.PreviousPeriod, subjectOnly, [figure], null));
    }

    private static ChatDatabaseQueryFigure SumFigure(DatabaseFieldSum sum) =>
        new($"「{sum.Label}」加總", sum.Sum, sum.Display, sum.PreviousDisplay, sum.ChangeLabel);

    private static ChatDatabaseQueryAnswer ComposeSum(DatabaseQueryToolCall call, string source, DatabaseFieldSumResult result)
    {
        var subjectOnly = result.SubjectId is not null;
        var figure = SumFigure(result.Field);
        var status = result.Field.RecordCount == 0 ? ChatDatabaseQueryStatus.NoData : ChatDatabaseQueryStatus.Answered;
        var text = status == ChatDatabaseQueryStatus.NoData
            ? $"{source}：{result.Period.Label}沒有任何紀錄填寫「{result.Field.Label}」{Scope(subjectOnly)}，加總為 {figure.Display}；{Previous(result.PreviousPeriod)}為 {figure.PreviousDisplay}。"
            : $"{source}：{result.Period.Label}「{result.Field.Label}」加總為 {figure.Display}（{CountDisplay(result.Field.RecordCount)}有填寫）{Scope(subjectOnly)}；{Previous(result.PreviousPeriod)}為 {figure.PreviousDisplay}，變化 {figure.ChangeLabel}。";
        return new(text, View(call, status, result.Period, result.PreviousPeriod, subjectOnly, [figure], null));
    }

    private static ChatDatabaseQueryAnswer ComposeSummary(DatabaseQueryToolCall call, string source, DatabasePeriodSummaryResult result)
    {
        var subjectOnly = result.SubjectId is not null;
        List<ChatDatabaseQueryFigure> figures =
        [
            new("有效紀錄筆數", result.RecordCount, CountDisplay(result.RecordCount), CountDisplay(result.PreviousRecordCount), result.RecordCountChangeLabel),
            .. result.Sums.Select(SumFigure),
        ];
        var status = result.RecordCount == 0 ? ChatDatabaseQueryStatus.NoData : ChatDatabaseQueryStatus.Answered;
        var sums = string.Concat(result.Sums.Select(sum => $"；「{sum.Label}」加總 {sum.Display}（前一期 {sum.PreviousDisplay}，變化 {sum.ChangeLabel}）"));
        var text = status == ChatDatabaseQueryStatus.NoData
            ? $"{source}：{result.Period.Label}沒有任何有效紀錄{Scope(subjectOnly)}，共 {figures[0].Display}；{Previous(result.PreviousPeriod)}為 {figures[0].PreviousDisplay}。"
            : $"{source}：{result.Period.Label}共有 {figures[0].Display}有效紀錄{Scope(subjectOnly)}（前一期 {figures[0].PreviousDisplay}，變化 {result.RecordCountChangeLabel}）{sums}。";
        return new(text, View(call, status, result.Period, result.PreviousPeriod, subjectOnly, figures, null));
    }

    private static ChatDatabaseQueryAnswer ComposeComparison(DatabaseQueryToolCall call, string source, DatabaseSubjectComparisonResult result)
    {
        var comparison = result.Comparison;
        if (comparison.Status == DatabaseComparisonStatus.InsufficientRecords)
        {
            var message = comparison.Message ?? string.Empty;
            return new($"{source}：資料不足，無法比較。{message}",
                View(call, ChatDatabaseQueryStatus.InsufficientData, null, null, true, [], message));
        }

        var figures = comparison.Metrics
            .Select(metric => new ChatDatabaseQueryFigure(
                $"「{metric.Label}」本次", metric.Current.Value, metric.Current.Display, metric.Previous.Display, metric.ChangeFromPreviousLabel))
            .ToList();
        var summaries = string.Concat(comparison.Metrics.Select(metric => metric.Summary));
        return new($"{source}：{comparison.Summary}{summaries}",
            View(call, ChatDatabaseQueryStatus.Answered, null, null, true, figures, comparison.Summary));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
