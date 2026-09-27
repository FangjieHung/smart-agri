using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartAgri.Infrastructure.Knowledge.Extraction;

/// <summary>
/// Shows a numeric cell as Excel would, from its number format: <c>#,##0</c> as 1,280,
/// <c>0%</c> as 10%, <c>yyyy/m/d</c> as 2026/9/1, <c>h:mm</c> as 15:00, and the Taiwanese
/// built-ins (民國 years with <c>[$-404]e</c>, 「上午/下午」). "Where feasible" (M2 plan,
/// Slice 6): digit placeholders, literals, thousands and percent follow .NET's custom numeric
/// format, which matches Excel's for these; fractions, conditions and anything unparsable fall
/// back to Excel's <c>General</c>. Built-in format 14 (a short date whose look depends on the
/// viewer's locale) is shown the way Traditional Chinese Excel shows it, <c>yyyy/m/d</c>.
/// </summary>
internal static class ExcelNumberFormat
{
    private const string General = "General";

    /// <summary>ECMA-376 Part 1 §18.8.30, plus the zh-TW built-ins it lists.</summary>
    private static readonly Dictionary<uint, string> BuiltIn = new()
    {
        [0] = General,
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "yyyy/m/d",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "yyyy/m/d h:mm",
        [27] = "[$-404]e/m/d",
        [28] = "[$-404]e\"年\"m\"月\"d\"日\"",
        [29] = "[$-404]e\"年\"m\"月\"d\"日\"",
        [30] = "m/d/yy",
        [31] = "yyyy\"年\"m\"月\"d\"日\"",
        [32] = "hh\"時\"mm\"分\"",
        [33] = "hh\"時\"mm\"分\"ss\"秒\"",
        [34] = "上午/下午hh\"時\"mm\"分\"",
        [35] = "上午/下午hh\"時\"mm\"分\"ss\"秒\"",
        [36] = "[$-404]e/m/d",
        [37] = "#,##0 ;(#,##0)",
        [38] = "#,##0 ;[Red](#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;[Red](#,##0.00)",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mmss.0",
        [48] = "##0.0E+0",
        [49] = "@",
        [50] = "[$-404]e/m/d",
        [51] = "[$-404]e\"年\"m\"月\"d\"日\"",
        [52] = "上午/下午hh\"時\"mm\"分\"",
        [53] = "上午/下午hh\"時\"mm\"分\"ss\"秒\"",
        [54] = "[$-404]e\"年\"m\"月\"d\"日\"",
        [55] = "上午/下午hh\"時\"mm\"分\"",
        [56] = "上午/下午hh\"時\"mm\"分\"ss\"秒\"",
        [57] = "[$-404]e/m/d",
        [58] = "[$-404]e\"年\"m\"月\"d\"日\"",
    };

    private static readonly string[] ChineseWeekdays = ["日", "一", "二", "三", "四", "五", "六"];

    /// <summary>The Excel serial of <paramref name="date"/> (days since 1899-12-30, or since
    /// 1904-01-01 for a 1904-based workbook).</summary>
    public static double ToSerial(DateTime date, bool date1904) => date.ToOADate() - (date1904 ? 1462 : 0);

    /// <summary><paramref name="value"/> formatted with the Excel format code <paramref name="code"/>.</summary>
    public static string Format(double value, string code, bool date1904 = false)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return FormatGeneral(value);
        }

        var sections = SplitSections(code);
        var section = sections[0];
        if (value < 0 && sections.Count >= 2 && !HasCondition(sections[0]))
        {
            // Excel shows the negative section with the absolute value: any sign is a literal.
            section = sections[1];
            value = -value;
        }
        else if (value == 0 && sections.Count >= 3 && !HasCondition(sections[0]))
        {
            section = sections[2];
        }

        try
        {
            if (IsDateTime(section))
            {
                return FormatDateTime(value, section, date1904) ?? FormatGeneral(value);
            }

            return FormatNumber(value, section) ?? FormatGeneral(value);
        }
        catch (FormatException)
        {
            return FormatGeneral(value);
        }
    }

    /// <summary>Excel's <c>General</c>: integers as is, otherwise up to 10 decimals, very large
    /// or small numbers in scientific notation.</summary>
    public static string FormatGeneral(double value)
    {
        var magnitude = Math.Abs(value);
        return magnitude != 0 && (magnitude >= 1e11 || magnitude < 1e-9)
            ? value.ToString("0.#####E+00", CultureInfo.InvariantCulture)
            : value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static string? FormatNumber(double value, string section)
    {
        var net = new StringBuilder();
        var digits = false;
        for (var i = 0; i < section.Length; i++)
        {
            var character = section[i];
            switch (character)
            {
                case '"':
                    var end = section.IndexOf('"', i + 1);
                    end = end < 0 ? section.Length : end;
                    AppendLiteral(net, section[(i + 1)..end]);
                    i = end;
                    break;
                case '\\' when i + 1 < section.Length:
                    AppendLiteral(net, section[i + 1].ToString());
                    i++;
                    break;
                case '[':
                    var close = section.IndexOf(']', i + 1);
                    close = close < 0 ? section.Length : close;
                    AppendLiteral(net, CurrencySymbol(section[(i + 1)..close]));
                    i = close;
                    break;
                case '_':
                    // Padding the width of the next character: a space.
                    net.Append(' ');
                    i++;
                    break;
                case '*':
                    // Repeat the next character to fill the cell: nothing, in text.
                    i++;
                    break;
                case '0' or '#' or '.' or ',' or '%':
                    digits |= character is '0' or '#';
                    net.Append(character);
                    break;
                case 'E' or 'e' when i + 1 < section.Length && section[i + 1] is '+' or '-':
                    net.Append(character).Append(section[i + 1]);
                    i++;
                    break;
                case '?' or '/' or '@':
                    // Fractions and text placeholders have no .NET equivalent.
                    return null;
                case 'G' or 'g' when section[i..].StartsWith(General, StringComparison.OrdinalIgnoreCase):
                    return FormatGeneral(value);
                default:
                    AppendLiteral(net, character.ToString());
                    break;
            }
        }

        if (digits)
        {
            return value.ToString(net.ToString(), CultureInfo.InvariantCulture);
        }

        // No digit placeholder: a section of literals only (e.g. a zero section of "-") shows
        // just them; an empty one shows the number as General.
        var literal = Unescape(net);
        return literal.Length > 0 ? literal : null;
    }

    private static string Unescape(StringBuilder net)
    {
        var plain = new StringBuilder(net.Length);
        for (var i = 0; i < net.Length; i++)
        {
            if (net[i] == '\\' && i + 1 < net.Length)
            {
                i++;
            }

            plain.Append(net[i]);
        }

        return plain.ToString();
    }

    private static string? FormatDateTime(double serial, string section, bool date1904)
    {
        // 1900-based serials below 61 are off by Excel's phantom 1900-02-29.
        var days = serial + (date1904 ? 1462 : 0) + (!date1904 && serial < 61 ? 1 : 0);
        if (days is < 0 or > 2958465)
        {
            return null;
        }

        var moment = DateTime.FromOADate(days);
        var elapsed = TimeSpan.FromDays(serial);
        var rocYears = section.Contains("[$-404]", StringComparison.OrdinalIgnoreCase)
            || section.Contains("zh-TW", StringComparison.OrdinalIgnoreCase);
        var twelveHour = section.Contains("AM/PM", StringComparison.OrdinalIgnoreCase)
            || section.Contains("A/P", StringComparison.OrdinalIgnoreCase)
            || section.Contains("上午/下午", StringComparison.Ordinal);

        var output = new StringBuilder();
        var lastWasHour = false;
        for (var i = 0; i < section.Length; i++)
        {
            var character = section[i];
            var run = RunLength(section, i);
            switch (char.ToLowerInvariant(character))
            {
                case '"':
                    var end = section.IndexOf('"', i + 1);
                    end = end < 0 ? section.Length : end;
                    output.Append(section, i + 1, end - i - 1);
                    i = end;
                    continue;
                case '\\' when i + 1 < section.Length:
                    output.Append(section[i + 1]);
                    i++;
                    continue;
                case '[':
                    var close = section.IndexOf(']', i + 1);
                    close = close < 0 ? section.Length : close;
                    var inside = section[(i + 1)..close].ToLowerInvariant();
                    if (inside.Length > 0 && inside.All(letter => letter is 'h' or 'm' or 's'))
                    {
                        var total = inside[0] switch
                        {
                            'h' => Math.Floor(elapsed.TotalHours),
                            'm' => Math.Floor(elapsed.TotalMinutes),
                            _ => Math.Floor(elapsed.TotalSeconds),
                        };
                        output.Append(total.ToString(new string('0', inside.Length), CultureInfo.InvariantCulture));
                        lastWasHour = inside[0] == 'h';
                    }

                    i = close;
                    continue;
                case 'y':
                    lastWasHour = false;
                    output.Append(run <= 2 ? (moment.Year % 100).ToString("00", CultureInfo.InvariantCulture) : moment.Year.ToString(CultureInfo.InvariantCulture));
                    break;
                case 'e':
                    output.Append((rocYears ? moment.Year - 1911 : moment.Year).ToString(CultureInfo.InvariantCulture));
                    break;
                case 'm' when lastWasHour || NextTokenIsSeconds(section, i + run):
                    output.Append(moment.Minute.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    lastWasHour = false;
                    break;
                case 'm':
                    output.Append(run switch
                    {
                        1 => moment.Month.ToString(CultureInfo.InvariantCulture),
                        2 => moment.Month.ToString("00", CultureInfo.InvariantCulture),
                        3 => moment.ToString("MMM", CultureInfo.InvariantCulture),
                        4 => moment.ToString("MMMM", CultureInfo.InvariantCulture),
                        _ => moment.ToString("MMMM", CultureInfo.InvariantCulture)[..1],
                    });
                    break;
                case 'd':
                    lastWasHour = false;
                    output.Append(run switch
                    {
                        1 => moment.Day.ToString(CultureInfo.InvariantCulture),
                        2 => moment.Day.ToString("00", CultureInfo.InvariantCulture),
                        3 => moment.ToString("ddd", CultureInfo.InvariantCulture),
                        _ => moment.ToString("dddd", CultureInfo.InvariantCulture),
                    });
                    break;
                case 'a' when section[i..].StartsWith("am/pm", StringComparison.OrdinalIgnoreCase):
                    output.Append(moment.Hour < 12 ? "AM" : "PM");
                    i += 4;
                    continue;
                case 'a' when section[i..].StartsWith("a/p", StringComparison.OrdinalIgnoreCase):
                    output.Append(moment.Hour < 12 ? "A" : "P");
                    i += 2;
                    continue;
                case 'a' when run >= 3:
                    // aaa / aaaa: the weekday in Chinese (一 / 星期一).
                    var weekday = ChineseWeekdays[(int)moment.DayOfWeek];
                    output.Append(run == 3 ? weekday : "星期" + weekday);
                    break;
                case '上' when section[i..].StartsWith("上午/下午", StringComparison.Ordinal):
                    output.Append(moment.Hour < 12 ? "上午" : "下午");
                    i += 4;
                    continue;
                case 'h':
                    var hour = twelveHour ? ((moment.Hour + 11) % 12) + 1 : moment.Hour;
                    output.Append(hour.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    lastWasHour = true;
                    i += run - 1;
                    continue;
                case 's':
                    output.Append(moment.Second.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture));
                    break;
                case '_':
                    output.Append(' ');
                    i++;
                    continue;
                case '*':
                    i++;
                    continue;
                default:
                    output.Append(character);
                    continue;
            }

            i += run - 1;
        }

        return output.ToString();
    }

    /// <summary>Whether the section formats dates or times: y, m, d, h, s, e, AM/PM or 上午/下午
    /// outside quotes, escapes and brackets (elapsed-time brackets such as [h] count).</summary>
    private static bool IsDateTime(string section)
    {
        for (var i = 0; i < section.Length; i++)
        {
            switch (section[i])
            {
                case '"':
                    var end = section.IndexOf('"', i + 1);
                    i = end < 0 ? section.Length : end;
                    break;
                case '\\':
                    i++;
                    break;
                case '[':
                    var close = section.IndexOf(']', i + 1);
                    var inside = close < 0 ? string.Empty : section[(i + 1)..close].ToLowerInvariant();
                    if (inside.Length > 0 && inside.All(letter => letter is 'h' or 'm' or 's'))
                    {
                        return true;
                    }

                    i = close < 0 ? section.Length : close;
                    break;
                case 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'e':
                    return true;
                case '上' when section[i..].StartsWith("上午/下午", StringComparison.Ordinal):
                    return true;
                case 'G' or 'g' when section[i..].StartsWith(General, StringComparison.OrdinalIgnoreCase):
                    i += General.Length - 1;
                    break;
                case 'E' when i + 1 < section.Length && section[i + 1] is '+' or '-':
                    i++;
                    break;
            }
        }

        return false;
    }

    /// <summary>The format's sections (positive; negative; zero; text), split at <c>;</c>
    /// outside quotes, escapes and brackets.</summary>
    private static List<string> SplitSections(string code)
    {
        var sections = new List<string>();
        var start = 0;
        for (var i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '"':
                    var end = code.IndexOf('"', i + 1);
                    i = end < 0 ? code.Length : end;
                    break;
                case '\\':
                    i++;
                    break;
                case '[':
                    var close = code.IndexOf(']', i + 1);
                    i = close < 0 ? code.Length : close;
                    break;
                case ';':
                    sections.Add(code[start..i]);
                    start = i + 1;
                    break;
            }
        }

        sections.Add(code[start..]);
        return sections;
    }

    private static bool HasCondition(string section)
    {
        var open = section.IndexOf('[', StringComparison.Ordinal);
        return open >= 0 && open + 1 < section.Length && section[open + 1] is '<' or '>' or '=';
    }

    /// <summary><c>[$NT$-404]</c> is the currency symbol NT$; colors, locales and conditions
    /// show nothing.</summary>
    private static string CurrencySymbol(string bracket)
    {
        if (!bracket.StartsWith('$'))
        {
            return string.Empty;
        }

        var dash = bracket.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? bracket[1..] : bracket[1..dash];
    }

    private static void AppendLiteral(StringBuilder net, string literal)
    {
        foreach (var character in literal)
        {
            // Escape everything .NET might read as a placeholder.
            net.Append('\\').Append(character);
        }
    }

    private static int RunLength(string section, int start)
    {
        var character = char.ToLowerInvariant(section[start]);
        var end = start;
        while (end < section.Length && char.ToLowerInvariant(section[end]) == character)
        {
            end++;
        }

        return end - start;
    }

    /// <summary>Whether the next date/time token after <paramref name="index"/> is seconds,
    /// which makes an m (or mm) before it minutes.</summary>
    private static bool NextTokenIsSeconds(string section, int index)
    {
        for (var i = index; i < section.Length; i++)
        {
            var character = char.ToLowerInvariant(section[i]);
            if (character is 's')
            {
                return true;
            }

            if (character is 'y' or 'm' or 'd' or 'h' or 'e')
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Number formats of a workbook's cell styles (<c>cellXfs</c>), by style index.</summary>
    internal sealed class Styles
    {
        private readonly List<string> _codes;

        private Styles(List<string> codes)
        {
            _codes = codes;
        }

        public static Styles From(Stylesheet? stylesheet)
        {
            var custom = new Dictionary<uint, string>();
            foreach (var format in stylesheet?.NumberingFormats?.Elements<NumberingFormat>() ?? [])
            {
                if (format.NumberFormatId?.Value is { } id && format.FormatCode?.Value is { } code)
                {
                    custom[id] = code;
                }
            }

            var codes = new List<string>();
            foreach (var cellFormat in stylesheet?.CellFormats?.Elements<CellFormat>() ?? [])
            {
                var id = cellFormat.NumberFormatId?.Value ?? 0;
                codes.Add(custom.GetValueOrDefault(id) ?? BuiltIn.GetValueOrDefault(id) ?? General);
            }

            return new Styles(codes);
        }

        /// <param name="dateByDefault">An ISO date cell without a date format still shows as a date.</param>
        public string Format(double value, uint? styleIndex, bool date1904, bool dateByDefault = false)
        {
            var code = styleIndex is { } index && index < _codes.Count ? _codes[(int)index] : General;
            if (dateByDefault && !IsDateTime(code))
            {
                code = BuiltIn[14];
            }

            return ExcelNumberFormat.Format(value, code, date1904);
        }
    }
}
