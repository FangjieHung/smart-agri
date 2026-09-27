using Shouldly;
using SmartAgri.Application.Ai;
using SmartAgri.Application.Answers;

namespace SmartAgri.Application.Tests.Answers;

/// <summary><see cref="CitationMarkers"/>: lenient about the form of a citation, strict about its
/// number (M3 plan §3 step 5, §7 risk 2).</summary>
public class CitationMarkersTests
{
    [Theory]
    [InlineData("甲 [1]。", new[] { 1 })]
    [InlineData("甲［2］乙【1】", new[] { 2, 1 })]
    [InlineData("甲 [ 2 ] 乙 [2] 丙 [1]", new[] { 2, 1 })]
    [InlineData("甲 [1, 2]、乙 [2，1]、丙 [1、2]", new[] { 1, 2 })]
    [InlineData("甲 ［１］ 乙 【２】", new[] { 1, 2 })]
    [InlineData("甲 [01]", new[] { 1 })]
    public void Every_citation_form_is_read_in_order_of_first_appearance(string text, int[] cited)
    {
        var analysis = CitationMarkers.Analyze(text, passageCount: 2);

        analysis.Cited.ShouldBe(cited);
        (analysis.OutOfRange, analysis.CannotAnswer).ShouldBe((false, false));
    }

    [Theory]
    [InlineData("甲 [3]")]
    [InlineData("甲 [0]")]
    [InlineData("甲 [1, 3]")]
    [InlineData("甲 [99999999999999999999]")]
    public void A_number_outside_1_to_k_is_out_of_range(string text)
    {
        CitationMarkers.Analyze(text, passageCount: 2).OutOfRange.ShouldBeTrue();
    }

    [Theory]
    [InlineData("甲 [a]")]
    [InlineData("甲 (1)")]
    [InlineData("甲 [[1]]x")]
    public void Other_brackets_are_not_citations_but_a_bracketed_number_inside_them_is(string text)
    {
        var analysis = CitationMarkers.Analyze(text, passageCount: 2);

        analysis.Cited.ShouldBe(text.Contains("[[1]]", StringComparison.Ordinal) ? [1] : []);
    }

    [Fact]
    public void The_cannot_answer_marker_is_recognized_and_is_not_a_citation()
    {
        var analysis = CitationMarkers.Analyze(ChatAnswerMarkers.CannotAnswer, passageCount: 2);

        (analysis.CannotAnswer, analysis.Cited.Count, analysis.OutOfRange).ShouldBe((true, 0, false));
    }

    [Fact]
    public void Renumbering_rewrites_every_marker_in_half_width_brackets()
    {
        CitationMarkers.Renumber("甲【2】乙 [1, 2] 丙［２］", new Dictionary<int, int> { [2] = 1, [1] = 2 })
            .ShouldBe("甲[1]乙 [2][1] 丙[1]");
    }

    [Fact]
    public void Stripping_removes_every_marker_with_the_spaces_before_it_and_the_cannot_answer_marker()
    {
        CitationMarkers.Strip("  七天 [1] 內【2】可以退［3、4］。" + ChatAnswerMarkers.CannotAnswer + "\n  - 保留縮排  ")
            .ShouldBe("七天 內可以退。\n  - 保留縮排");
    }
}
