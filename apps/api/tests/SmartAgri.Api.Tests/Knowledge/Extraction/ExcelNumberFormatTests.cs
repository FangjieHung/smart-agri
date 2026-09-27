using Shouldly;
using SmartAgri.Infrastructure.Knowledge.Extraction;

namespace SmartAgri.Api.Tests.Knowledge.Extraction;

public class ExcelNumberFormatTests
{
    // 2026-09-01 15:30:45 as an Excel serial (1900 date system).
    private static readonly double Moment = new DateTime(2026, 9, 1, 15, 30, 45).ToOADate();

    [Theory]
    [InlineData(1280, "General", "1280")]
    [InlineData(0.1 + 0.2, "General", "0.3")]
    [InlineData(123456789012345, "General", "1.23457E+14")]
    [InlineData(1280, "#,##0", "1,280")]
    [InlineData(-1280, "#,##0", "-1,280")]
    [InlineData(1280.5, "#,##0.00", "1,280.50")]
    [InlineData(1.5, "0.0", "1.5")]
    [InlineData(1, "0.0", "1.0")]
    [InlineData(0.1, "0%", "10%")]
    [InlineData(0.125, "0.00%", "12.50%")]
    [InlineData(1280, "#,##0\" 元\"", "1,280 元")]
    [InlineData(1280, "[$NT$-404]#,##0", "NT$1,280")]
    [InlineData(-1280, "#,##0;[Red](#,##0)", "(1,280)")]
    [InlineData(0, "#,##0;(#,##0);\"-\"", "-")]
    [InlineData(1280, "#,##0_);(#,##0)", "1,280 ")]
    [InlineData(1280000, "#,##0,\"K\"", "1,280K")]
    [InlineData(12345, "0.00E+00", "1.23E+04")]
    [InlineData(0.5, "# ?/?", "0.5")]
    [InlineData(42, "@", "42")]
    [InlineData(42, "0\" 件\"", "42 件")]
    public void Numbers_follow_their_format(double value, string code, string expected)
    {
        ExcelNumberFormat.Format(value, code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("yyyy/m/d", "2026/9/1")]
    [InlineData("yyyy/mm/dd", "2026/09/01")]
    [InlineData("m/d/yy", "9/1/26")]
    [InlineData("d-mmm-yy", "1-Sep-26")]
    [InlineData("yyyy/m/d h:mm", "2026/9/1 15:30")]
    [InlineData("h:mm", "15:30")]
    [InlineData("h:mm:ss AM/PM", "3:30:45 PM")]
    [InlineData("mm:ss", "30:45")]
    [InlineData("yyyy\"年\"m\"月\"d\"日\"", "2026年9月1日")]
    [InlineData("[$-404]e/m/d", "115/9/1")]
    [InlineData("[$-404]e\"年\"m\"月\"d\"日\"", "115年9月1日")]
    [InlineData("上午/下午hh\"時\"mm\"分\"", "下午03時30分")]
    [InlineData("yyyy/m/d aaaa", "2026/9/1 星期二")]
    public void Dates_and_times_follow_their_format(string code, string expected)
    {
        ExcelNumberFormat.Format(Moment, code).ShouldBe(expected);
    }

    [Fact]
    public void Elapsed_time_and_the_1904_date_system()
    {
        ExcelNumberFormat.Format(1.5, "[h]:mm:ss").ShouldBe("36:00:00");
        ExcelNumberFormat.Format(Moment - 1462, "yyyy/m/d", date1904: true).ShouldBe("2026/9/1");
    }

    [Fact]
    public void A_date_format_on_a_value_out_of_range_shows_the_number()
    {
        ExcelNumberFormat.Format(-5, "yyyy/m/d").ShouldBe("-5");
        ExcelNumberFormat.Format(1e9, "yyyy/m/d").ShouldBe("1000000000");
    }
}
