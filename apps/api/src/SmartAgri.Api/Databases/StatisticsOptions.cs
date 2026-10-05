using Microsoft.Extensions.Options;

namespace SmartAgri.Api.Databases;

/// <summary>
/// Configuration section <c>Statistics</c> (M4 #147): the time zone whose calendar days the fixed
/// statistics queries (<see cref="DatabaseFixedQueryService"/>) count in. Every named period,
/// every <c>from</c>/<c>to</c>, the Monday week start, the "previous period" and the dates shown in
/// a result (<c>period.label</c>, the dates of first/previous/current) are days of this zone, so a
/// record submitted at 07:30 in Taipei belongs to that Taipei day, not to the UTC day before. The
/// SQL filter is the UTC half-open range <c>[start, end)</c> of those days. #150's scheduled report
/// must read the same setting.
/// </summary>
public sealed class StatisticsOptions
{
    public const string SectionName = "Statistics";

    public const string DefaultTimeZone = "Asia/Taipei";

    /// <summary>An IANA time zone id (e.g. <c>Asia/Taipei</c>).</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    /// <summary>The zone, or <see langword="null"/> when <see cref="TimeZone"/> is not a known id.</summary>
    public TimeZoneInfo? TryResolve() =>
        TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone ?? string.Empty, out var zone) ? zone : null;

    /// <summary>
    /// The configured zone as an IANA id, the form the SPA's <c>Intl.DateTimeFormat</c> accepts
    /// (<c>GET /api/v1/me</c>'s <c>statisticsTimeZone</c>, #177). A Windows id that the runtime
    /// also resolves (e.g. <c>Taipei Standard Time</c>) is converted; <see langword="null"/> when
    /// the setting does not resolve (startup validation rejects that).
    /// </summary>
    public string? TryResolveIanaId()
    {
        if (TryResolve() is not { } zone)
        {
            return null;
        }

        return zone.HasIanaId
            ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) ? ianaId : zone.Id;
    }

    internal sealed class Validator : IValidateOptions<StatisticsOptions>
    {
        public ValidateOptionsResult Validate(string? name, StatisticsOptions options) =>
            options.TryResolve() is null
                ? ValidateOptionsResult.Fail(
                    $"Statistics:TimeZone '{options.TimeZone}' is not a known IANA time zone id (e.g. Asia/Taipei); is tzdata installed?")
                : ValidateOptionsResult.Success;
    }
}
