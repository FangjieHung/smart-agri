using Shouldly;
using SmartAgri.Application.Knowledge.Processing;

namespace SmartAgri.Application.Tests.Knowledge.Processing;

public class ExtractedTextTests
{
    [Fact]
    public void Line_breaks_become_newlines_trailing_space_goes_and_blank_lines_shrink_to_one()
    {
        ExtractedText.Clean("\r\n  一、退貨條件  \r\n收到商品後\r七天內\u2028可退貨\n\n\n\n生鮮恕不退貨\t \n\n")
            .ShouldBe("一、退貨條件\n收到商品後\n七天內\n可退貨\n\n生鮮恕不退貨");
    }

    [Fact]
    public void Control_characters_and_broken_surrogates_become_replacement_characters_tabs_stay()
    {
        ExtractedText.Clean("退\u0000貨\u0007條\u001B件\uD800\t完").ShouldBe("退\uFFFD貨\uFFFD條\uFFFD件\uFFFD\t完");
    }

    [Fact]
    public void Text_is_nfc_normalized_so_compatibility_ideographs_become_unified_ones()
    {
        // U+F900 is a CJK compatibility ideograph whose canonical form is U+8C48.
        ExtractedText.Clean("\uF900").ShouldBe("\u8C48");
        ExtractedText.Clean("e\u0301").ShouldBe("\u00E9");
    }

    [Fact]
    public void A_line_collapses_every_white_space_run_to_one_space()
    {
        ExtractedText.CleanLine("  2.1\t退貨\n條件 \u3000 ").ShouldBe("2.1 退貨 條件");
        ExtractedText.CleanLine(" \n ").ShouldBe(string.Empty);
    }
}
