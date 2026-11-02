using System.Globalization;
using System.Text;

namespace SmartAgri.Domain.Coordinator;

/// <summary>
/// The values a scenario's announcement is filled with (Slice 3 works them out from the command).
/// </summary>
/// <param name="Date">The shop's local date the command is about: <c>{日期}</c>.</param>
/// <param name="Time">The new closing time (提早打烊) or opening time (延後開店): <c>{時間}</c>.</param>
/// <param name="Reopening">When the shop opens again after a 臨時店休 (<see cref="BusinessHours.FirstOpeningAfter"/>):
/// <c>{恢復營業}</c>, shown in the shop's time zone.</param>
/// <param name="Reason">The optional reason: <c>{原因}</c>. Without one, every line of the template
/// that contains <c>{原因}</c> is left out.</param>
public sealed record AnnouncementValues(
    DateOnly Date,
    TimeOnly? Time = null,
    DateTimeOffset? Reopening = null,
    string? Reason = null);

/// <summary>
/// Announcement templates (store-coordinator design §3 Q13, #373): which variables each scenario's
/// template may and must use (<see cref="Problems"/>, used by <see cref="ShopSettingsJson"/>) and
/// filling one in (<see cref="Render"/>, pure).
/// </summary>
/// <remarks>
/// A variable is written <c>{名稱}</c>; braces are only for variables (a lone <c>{</c> or <c>}</c> is
/// refused). Values are put in once, as they are: a reason containing <c>{店名}</c> stays
/// <c>{店名}</c>.
/// </remarks>
public static class ShopAnnouncements
{
    /// <summary><c>{店名}</c>: the settings' store name.</summary>
    public const string StoreName = "店名";

    /// <summary><c>{日期}</c>: <see cref="AnnouncementValues.Date"/> as 「11/2（日）」.</summary>
    public const string Date = "日期";

    /// <summary><c>{時間}</c>: <see cref="AnnouncementValues.Time"/> as 「16:00」; 提早打烊 and 延後開店 only.</summary>
    public const string Time = "時間";

    /// <summary><c>{恢復營業}</c>: <see cref="AnnouncementValues.Reopening"/> as 「11/3（一）11:00」; 臨時店休 only.</summary>
    public const string Reopening = "恢復營業";

    /// <summary><c>{原因}</c>: <see cref="AnnouncementValues.Reason"/>; optional.</summary>
    public const string Reason = "原因";

    /// <summary>Longest template (characters).</summary>
    public const int TemplateMaxLength = 1000;

    private static readonly string[] WeekdayNames = ["日", "一", "二", "三", "四", "五", "六"];

    /// <summary>The variables <paramref name="scenario"/>'s template may use, in the order they are
    /// listed to people.</summary>
    public static IReadOnlyList<string> Allowed(ShopScenario scenario) => scenario switch
    {
        ShopScenario.TemporaryClosure => [StoreName, Date, Reopening, Reason],
        ShopScenario.CloseEarly or ShopScenario.OpenLate => [StoreName, Date, Time, Reason],
        ShopScenario.Reopen => [StoreName, Date, Reason],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not a scenario."),
    };

    /// <summary>The variables <paramref name="scenario"/>'s template must use.</summary>
    public static IReadOnlyList<string> Required(ShopScenario scenario) => scenario switch
    {
        ShopScenario.TemporaryClosure or ShopScenario.Reopen => [Date],
        ShopScenario.CloseEarly or ShopScenario.OpenLate => [Time],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not a scenario."),
    };

    /// <summary>
    /// What is wrong with <paramref name="template"/> as <paramref name="scenario"/>'s template, in
    /// Chinese for the operator; empty when nothing is. Length and blankness are checked by the caller.
    /// Besides unknown, misplaced and missing variables: a line with <c>{原因}</c> must not hold a
    /// required variable, because that whole line is left out when there is no reason.
    /// </summary>
    public static IReadOnlyList<string> Problems(ShopScenario scenario, string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var problems = new List<string>();
        var allowed = Allowed(scenario);
        var required = Required(scenario);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, number) in Lines(template).Select((line, index) => (line, index + 1)))
        {
            var tokens = Tokenize(line, out var braceProblems);
            problems.AddRange(braceProblems.Select(problem => $"第 {number} 行：{problem}"));
            var variables = tokens.Where(token => token.IsVariable).Select(token => token.Text).ToList();
            foreach (var variable in variables)
            {
                if (allowed.Contains(variable))
                {
                    used.Add(variable);
                }
                else if (IsKnown(variable))
                {
                    problems.Add($"這個情境不能用 {{{variable}}}；可用：{List(allowed)}。");
                }
                else
                {
                    problems.Add($"不認得的變數 {{{variable}}}；可用：{List(allowed)}。");
                }
            }

            if (variables.Contains(Reason))
            {
                foreach (var variable in variables.Where(required.Contains).Distinct())
                {
                    problems.Add(
                        $"第 {number} 行：{{{Reason}}} 要和 {{{variable}}} 分開放在不同行；沒有原因時含 {{{Reason}}} 的整行會省略，{{{variable}}} 也會跟著不見。");
                }
            }
        }

        foreach (var variable in required.Where(variable => !used.Contains(variable)))
        {
            problems.Add($"必須包含 {{{variable}}}。");
        }

        return [.. problems.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <paramref name="settings"/>' announcement of <paramref name="scenario"/> with
    /// <paramref name="values"/> put in. Lines containing <c>{原因}</c> are left out when there is no
    /// reason (blank counts as none). Throws when the scenario has no announcement, or the template
    /// uses <c>{時間}</c> or <c>{恢復營業}</c> and <paramref name="values"/> lacks it.
    /// </summary>
    public static string Render(ShopSettingsDocument settings, ShopScenario scenario, AnnouncementValues values)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);
        var announcement = settings[scenario].Announcement
            ?? throw new InvalidOperationException($"The shop has no announcement for {WireNames<ShopScenario>.ToWire(scenario)}.");
        var reason = string.IsNullOrWhiteSpace(values.Reason) ? null : values.Reason.Trim();
        var lines = new List<string>();
        foreach (var line in Lines(announcement.Template))
        {
            var tokens = Tokenize(line, out var problems);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"The template is not valid: {problems[0]}");
            }

            if (reason is null && tokens.Any(token => token.IsVariable && token.Text == Reason))
            {
                continue;
            }

            var text = new StringBuilder();
            foreach (var token in tokens)
            {
                text.Append(token.IsVariable ? Value(token.Text, settings, values, reason) : token.Text);
            }

            lines.Add(text.ToString());
        }

        return string.Join('\n', lines);
    }

    /// <summary>「11/2（日）」.</summary>
    public static string FormatDate(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.Month}/{date.Day}（{WeekdayNames[(int)date.DayOfWeek]}）");

    /// <summary>「16:00」.</summary>
    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>「11/3（一）11:00」: <paramref name="instant"/> in <paramref name="zone"/>.</summary>
    public static string FormatDateTime(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        return $"{FormatDate(DateOnly.FromDateTime(local))}{FormatTime(TimeOnly.FromDateTime(local))}";
    }

    private static string Value(string variable, ShopSettingsDocument settings, AnnouncementValues values, string? reason) => variable switch
    {
        StoreName => settings.StoreName,
        Date => FormatDate(values.Date),
        Time => values.Time is { } time
            ? FormatTime(time)
            : throw new ArgumentException("The template uses {時間}; give the time.", nameof(values)),
        Reopening => values.Reopening is { } reopening
            ? FormatDateTime(reopening, settings.TimeZone)
            : throw new ArgumentException("The template uses {恢復營業}; give the reopening.", nameof(values)),
        Reason => reason ?? string.Empty,
        _ => throw new InvalidOperationException($"Unknown template variable {{{variable}}}."),
    };

    private static bool IsKnown(string variable) => variable is StoreName or Date or Time or Reopening or Reason;

    private static string List(IReadOnlyList<string> variables) => string.Join('、', variables.Select(variable => $"{{{variable}}}"));

    private static string[] Lines(string template) => template.Split('\n');

    /// <summary>Splits <paramref name="line"/> into text and <c>{variable}</c> tokens. A brace that does
    /// not belong to a variable is reported in <paramref name="problems"/> and kept as text, so the
    /// variables around it are still seen.</summary>
    private static List<Token> Tokenize(string line, out List<string> problems)
    {
        var tokens = new List<Token>();
        problems = [];
        var text = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var end = line[i] == '{' ? line.IndexOfAny(['{', '}'], i + 1) : -1;
            if (line[i] == '{' && (end < 0 || line[end] == '{'))
            {
                problems.Add("「{」沒有對應的「}」；大括號只能用來寫變數，例如 {日期}。");
                text.Append(line[i]);
            }
            else if (line[i] == '{' && end == i + 1)
            {
                problems.Add("「{}」裡沒有變數名稱。");
                i = end;
            }
            else if (line[i] == '{')
            {
                if (text.Length > 0)
                {
                    tokens.Add(new Token(text.ToString(), IsVariable: false));
                    text.Clear();
                }

                tokens.Add(new Token(line[(i + 1)..end], IsVariable: true));
                i = end;
            }
            else
            {
                if (line[i] == '}')
                {
                    problems.Add("「}」沒有對應的「{」；大括號只能用來寫變數，例如 {日期}。");
                }

                text.Append(line[i]);
            }
        }

        if (text.Length > 0)
        {
            tokens.Add(new Token(text.ToString(), IsVariable: false));
        }

        return tokens;
    }

    private sealed record Token(string Text, bool IsVariable);
}
