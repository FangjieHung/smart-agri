using Shouldly;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;

namespace SmartAgri.Application.Tests.Organizations;

/// <summary>The monthly token limit's rules and its 30 second cache (M5a plan §3 F, issue #195).</summary>
public class OrganizationTokenUsageTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // --- State ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 100, TokenUsageState.Normal)]
    [InlineData(79, 100, TokenUsageState.Normal)]
    [InlineData(80, 100, TokenUsageState.Near)]
    [InlineData(99, 100, TokenUsageState.Near)]
    [InlineData(100, 100, TokenUsageState.Exceeded)]
    [InlineData(101, 100, TokenUsageState.Exceeded)]
    [InlineData(1_599_999, 2_000_000, TokenUsageState.Normal)]
    [InlineData(1_600_000, 2_000_000, TokenUsageState.Near)]
    [InlineData(2_000_000, 2_000_000, TokenUsageState.Exceeded)]
    [InlineData(0, 0, TokenUsageState.Exceeded)]
    [InlineData(long.MaxValue - 1, long.MaxValue, TokenUsageState.Near)]
    public void State_is_normal_under_80_percent_near_from_80_and_exceeded_from_the_limit(long used, long limit, TokenUsageState expected)
    {
        OrganizationTokenUsageRules.StateOf(used, limit).ShouldBe(expected);
    }

    // --- Which calls count ---------------------------------------------------------------------

    [Fact]
    public void Every_model_purpose_is_either_counted_or_an_embedding_never_both()
    {
        var counted = OrganizationTokenUsageRules.CountedPurposes;
        var embeddings = OrganizationTokenUsageRules.EmbeddingPurposes;

        counted.Intersect(embeddings).ShouldBeEmpty();
        counted.Concat(embeddings).Order().ToList().ShouldBe([.. Enum.GetValues<ModelInvocationPurpose>().Order()],
            "a new ModelInvocationPurpose must be classified as chat (counts) or embedding (does not)");
        embeddings.ShouldBe([ModelInvocationPurpose.EmbedDocument, ModelInvocationPurpose.EmbedQuery], ignoreOrder: true);
    }

    // --- Months in Statistics:TimeZone -----------------------------------------------------------

    [Fact]
    public void The_month_runs_from_local_midnight_on_the_1st_to_local_midnight_on_the_1st_of_the_next_month()
    {
        // 2026-10-31 16:30 UTC is 2026-11-01 00:30 in Taipei: already November there.
        var (label, start, end) = OrganizationTokenUsageRules.MonthOf(new DateTimeOffset(2026, 10, 31, 16, 30, 0, TimeSpan.Zero), Taipei);

        label.ShouldBe("2026-11");
        start.ShouldBe(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero));
        end.ShouldBe(new DateTimeOffset(2026, 11, 30, 16, 0, 0, TimeSpan.Zero));

        // One minute earlier it is still October in Taipei.
        var october = OrganizationTokenUsageRules.MonthOf(new DateTimeOffset(2026, 10, 31, 15, 59, 0, TimeSpan.Zero), Taipei);
        october.Label.ShouldBe("2026-10");
        october.Start.ShouldBe(new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero));
        october.EndExclusive.ShouldBe(start);
    }

    [Fact]
    public void A_december_month_ends_in_january_of_the_next_year()
    {
        var (label, start, end) = OrganizationTokenUsageRules.MonthOf(new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        label.ShouldBe("2026-12");
        start.ShouldBe(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        end.ShouldBe(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    // --- Limit and cache -------------------------------------------------------------------------

    [Fact]
    public async Task Without_an_own_limit_the_deployment_default_applies_and_the_range_is_this_months()
    {
        var source = new FakeSource { Used = 1_700_000 };
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero));
        var usage = new OrganizationTokenUsage(source, clock, Taipei, 2_000_000);
        var organizationId = Guid.NewGuid();

        var snapshot = await usage.GetAsync(organizationId, CancellationToken);

        snapshot.ShouldBe(new OrganizationTokenUsageSnapshot("2026-10", 1_700_000, 2_000_000, TokenUsageState.Near));
        source.Ranges.ShouldBe([(organizationId, new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero))]);
    }

    [Fact]
    public async Task An_own_limit_wins_over_the_default_including_zero()
    {
        var source = new FakeSource { Used = 0, Limit = 0 };
        var usage = new OrganizationTokenUsage(source, new ManualClock(DateTimeOffset.UnixEpoch.AddYears(56)), Taipei, 2_000_000);

        var snapshot = await usage.GetAsync(Guid.NewGuid(), CancellationToken);

        snapshot.LimitTokens.ShouldBe(0);
        snapshot.State.ShouldBe(TokenUsageState.Exceeded);
    }

    [Fact]
    public async Task The_answer_is_cached_for_30_seconds_per_organization_then_read_again()
    {
        var source = new FakeSource { Used = 10, Limit = 100 };
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero));
        var usage = new OrganizationTokenUsage(source, clock, Taipei, 2_000_000);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await usage.GetAsync(first, CancellationToken)).State.ShouldBe(TokenUsageState.Normal);
        source.Used = 100;
        source.Limit = 1_000;
        clock.Advance(TimeSpan.FromSeconds(29));
        (await usage.GetAsync(first, CancellationToken)).UsedTokens.ShouldBe(10, "still the cached answer");
        source.Reads.ShouldBe(1);

        // Another organization has its own entry.
        (await usage.GetAsync(second, CancellationToken)).UsedTokens.ShouldBe(100);
        source.Reads.ShouldBe(2);

        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await usage.GetAsync(first, CancellationToken);
        refreshed.UsedTokens.ShouldBe(100);
        refreshed.LimitTokens.ShouldBe(1_000);
        refreshed.State.ShouldBe(TokenUsageState.Normal);
        source.Reads.ShouldBe(3);
    }

    [Fact]
    public void Constructing_with_a_negative_default_limit_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new OrganizationTokenUsage(new FakeSource(), TimeProvider.System, Taipei, -1));
    }

    private sealed class FakeSource : IOrganizationTokenUsageSource
    {
        public long Used { get; set; }

        public long? Limit { get; set; }

        public int Reads { get; private set; }

        public List<(Guid OrganizationId, DateTimeOffset Start, DateTimeOffset End)> Ranges { get; } = [];

        public Task<long?> GetMonthlyTokenLimitAsync(Guid organizationId, CancellationToken cancellationToken) => Task.FromResult(Limit);

        public Task<long> SumChatTokensAsync(Guid organizationId, DateTimeOffset startInclusive, DateTimeOffset endExclusive, CancellationToken cancellationToken)
        {
            Reads++;
            Ranges.Add((organizationId, startInclusive, endExclusive));
            return Task.FromResult(Used);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
