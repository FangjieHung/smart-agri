using Microsoft.Extensions.Configuration;
using Shouldly;
using SmartAgri.Api.Knowledge;
using SmartAgri.Application.Knowledge.Retrieval;

namespace SmartAgri.Api.Tests.Knowledge;

/// <summary>The <c>Retrieval</c> section (M2 Slice 9): its defaults, written out in
/// <c>appsettings.json</c> for operators, and what it refuses at startup.</summary>
public sealed class RetrievalOptionsTests
{
    [Fact]
    public void The_defaults_are_the_calibrated_threshold_and_five_passages_and_appsettings_json_says_the_same()
    {
        var options = new RetrievalOptions();
        options.Validate().ShouldBeNull();
        options.ToSettings().ShouldBe(KnowledgeRetrievalSettings.Default);
        (KnowledgeRetrievalSettings.DefaultMinScore, KnowledgeRetrievalSettings.DefaultTop).ShouldBe((0.406, 5));

        var configured = new RetrievalOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ApiProjectDirectory(), "appsettings.json"), optional: false)
            .Build()
            .GetSection(RetrievalOptions.SectionName)
            .Bind(configured);
        (configured.MinScore, configured.Top).ShouldBe((0.406, 5), "change the default in both places (#192 calibrated it)");
        configured.CandidateMinScore.ShouldBe(0.35, "decided by the P5 evaluation (#304, docs/evals/2026-10-07-304-final-evaluation.md)");
        configured.Validate().ShouldBeNull();
        options.CandidateMinScore.ShouldBeNull("unset in code: no candidate band, exactly as before #302");
    }

    [Fact]
    public void Development_keeps_the_fake_models_threshold_because_it_scores_a_matching_passage_around_0_32()
    {
        var development = new RetrievalOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ApiProjectDirectory(), "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(ApiProjectDirectory(), "appsettings.Development.json"), optional: false)
            .Build()
            .GetSection(RetrievalOptions.SectionName)
            .Bind(development);
        (development.MinScore, development.Top).ShouldBe((0.3, 5), "0.406 is calibrated for text-embedding-3-small; Fake scores lower");
        development.CandidateMinScore.ShouldBeNull(
            "appsettings.json's 0.35 is above Development's MinScore, so Development turns the band off with an empty value");
        development.Validate().ShouldBeNull("otherwise the API would refuse to start in Development");
        development.ToSettings().CandidateFloor(development.MinScore).ShouldBeNull(
            "no candidate band, so the Fake-model tests answer exactly as before #302");
    }

    [Theory]
    [InlineData(0.406, 0.41)]
    [InlineData(0.406, -0.01)]
    [InlineData(0.406, double.NaN)]
    [InlineData(0.25, 0.30)]
    public void A_candidate_threshold_above_the_minimum_score_or_outside_0_1_refuses_to_start(double minScore, double candidate)
    {
        new RetrievalOptions { MinScore = minScore, CandidateMinScore = candidate }.Validate()
            .ShouldNotBeNull().ShouldStartWith("Retrieval:CandidateMinScore must be 0 up to Retrieval:MinScore");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.406)]
    public void A_candidate_threshold_from_0_up_to_the_minimum_score_or_none_is_usable(double? candidate)
    {
        var options = new RetrievalOptions { MinScore = 0.406, CandidateMinScore = candidate };
        options.Validate().ShouldBeNull();
        options.ToSettings().CandidateMinScore.ShouldBe(candidate);
    }

    [Fact]
    public void An_empty_value_unsets_the_candidate_threshold_so_an_operator_can_turn_it_off()
    {
        var options = new RetrievalOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ApiProjectDirectory(), "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retrieval:CandidateMinScore"] = string.Empty })
            .Build()
            .GetSection(RetrievalOptions.SectionName)
            .Bind(options);
        options.CandidateMinScore.ShouldBeNull();
    }

    [Theory]
    [InlineData(-0.01, 5)]
    [InlineData(1.01, 5)]
    [InlineData(double.NaN, 5)]
    [InlineData(0.3, 0)]
    [InlineData(0.3, 21)]
    public void Unusable_values_refuse_to_start(double minScore, int top)
    {
        new RetrievalOptions { MinScore = minScore, Top = top }.Validate().ShouldNotBeNull().ShouldStartWith("Retrieval:");
    }

    [Fact]
    public void The_bounds_themselves_are_usable()
    {
        new RetrievalOptions { MinScore = 0, Top = 1 }.ToSettings().ShouldBe(new KnowledgeRetrievalSettings(0, 1));
        new RetrievalOptions { MinScore = 1, Top = KnowledgeRetrievalSettings.MaxTop }.Validate().ShouldBeNull();
    }

    private static string ApiProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "src", "SmartAgri.Api");
            if (File.Exists(Path.Combine(directory.FullName, "SmartAgri.slnx")) && Directory.Exists(project))
            {
                return project;
            }
        }

        throw new DirectoryNotFoundException("apps/api (SmartAgri.slnx) not found above the test output directory.");
    }
}
