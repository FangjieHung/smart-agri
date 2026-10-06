using SmartAgri.Application.Databases;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Application.Organizations;

/// <summary>One organization's token usage this month (<c>GET /api/v1/organization/usage</c>).</summary>
/// <param name="Month">The month in <c>Statistics:TimeZone</c>, <c>YYYY-MM</c>.</param>
/// <param name="UsedTokens">Input + output tokens of the month's chat-model calls.</param>
/// <param name="LimitTokens">The organization's limit, or the deployment default.</param>
/// <param name="State"><see cref="OrganizationTokenUsageRules.StateOf"/>.</param>
public sealed record OrganizationTokenUsageSnapshot(string Month, long UsedTokens, long LimitTokens, TokenUsageState State);

/// <summary>
/// The pure rules of the monthly token limit (M5a plan §3 F, decisions C and E): which model calls
/// count, which instants make up a month, and when usage is <c>near</c> or <c>exceeded</c>.
/// </summary>
public static class OrganizationTokenUsageRules
{
    /// <summary>The deployment default when neither the organization nor the setting says otherwise
    /// (decision C).</summary>
    public const long DefaultMonthlyTokenLimit = 2_000_000;

    /// <summary>Usage at or above this share of the limit is <see cref="TokenUsageState.Near"/>.</summary>
    public const int NearPercent = 80;

    /// <summary>
    /// Every <see cref="ModelInvocationPurpose"/> that is a chat-model call and so counts toward the
    /// limit (decision E): the organization's own conversations, trial answers, acceptance reruns,
    /// form-request decisions, database-query tool selection, report summaries, website visitors' and
    /// LINE users' answers (M5b #232) — not only the public channels', because the cost is the
    /// organization's as a whole.
    /// </summary>
    public static readonly IReadOnlyList<ModelInvocationPurpose> CountedPurposes =
    [
        ModelInvocationPurpose.GenerateAnswer,
        ModelInvocationPurpose.TrialAnswer,
        ModelInvocationPurpose.AssistantTest,
        ModelInvocationPurpose.DatabaseQuery,
        ModelInvocationPurpose.GenerateReportSummary,
        ModelInvocationPurpose.FormRequest,
        ModelInvocationPurpose.PublicAnswer,
        ModelInvocationPurpose.LineAnswer,
    ];

    /// <summary>The embedding-model purposes, which never count. A new
    /// <see cref="ModelInvocationPurpose"/> must be added here or to <see cref="CountedPurposes"/>
    /// (a unit test enforces it).</summary>
    public static readonly IReadOnlyList<ModelInvocationPurpose> EmbeddingPurposes =
    [
        ModelInvocationPurpose.EmbedDocument,
        ModelInvocationPurpose.EmbedQuery,
    ];

    /// <summary><see cref="TokenUsageState.Exceeded"/> from the limit on (a limit of 0 is always
    /// exceeded), <see cref="TokenUsageState.Near"/> from 80% of it.</summary>
    public static TokenUsageState StateOf(long usedTokens, long limitTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usedTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(limitTokens);
        if (usedTokens >= limitTokens)
        {
            return TokenUsageState.Exceeded;
        }

        return (Int128)usedTokens * 100 >= (Int128)limitTokens * NearPercent ? TokenUsageState.Near : TokenUsageState.Normal;
    }

    /// <summary>The month that contains <paramref name="now"/> in <paramref name="timeZone"/>: its
    /// <c>YYYY-MM</c> label and the half-open UTC range <c>[Start, EndExclusive)</c> of its local days
    /// (midnight on the 1st to midnight on the 1st of the next month, in that zone).</summary>
    public static (string Label, DateTimeOffset Start, DateTimeOffset EndExclusive) MonthOf(DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var today = DatabaseFixedQueries.DayOf(now, timeZone);
        var first = new DateOnly(today.Year, today.Month, 1);
        return (
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{first.Year:0000}-{first.Month:00}"),
            DatabaseFixedQueries.StartOfDay(first, timeZone),
            DatabaseFixedQueries.StartOfDay(first.AddMonths(1), timeZone));
    }
}
