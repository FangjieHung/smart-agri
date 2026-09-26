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
    public void The_defaults_are_the_placeholder_threshold_and_five_passages_and_appsettings_json_says_the_same()
    {
        var options = new RetrievalOptions();
        options.Validate().ShouldBeNull();
        options.ToSettings().ShouldBe(KnowledgeRetrievalSettings.Default);
        (KnowledgeRetrievalSettings.DefaultMinScore, KnowledgeRetrievalSettings.DefaultTop).ShouldBe((0.3, 5));

        var configured = new RetrievalOptions();
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ApiProjectDirectory(), "appsettings.json"), optional: false)
            .Build()
            .GetSection(RetrievalOptions.SectionName)
            .Bind(configured);
        (configured.MinScore, configured.Top).ShouldBe((0.3, 5), "change the default in both places (#50 calibrates it)");
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
