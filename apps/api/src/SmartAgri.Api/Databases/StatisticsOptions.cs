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

    internal sealed class Validator : IValidateOptions<StatisticsOptions>
    {
        public ValidateOptionsResult Validate(string? name, StatisticsOptions options) =>
            options.TryResolve() is null
                ? ValidateOptionsResult.Fail(
                    $"Statistics:TimeZone '{options.TimeZone}' is not a known IANA time zone id (e.g. Asia/Taipei); is tzdata installed?")
                : ValidateOptionsResult.Success;
    }
}
