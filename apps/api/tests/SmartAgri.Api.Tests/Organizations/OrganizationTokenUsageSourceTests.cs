using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SmartAgri.Api.Organizations;
using SmartAgri.Api.Tests.Infrastructure;
using SmartAgri.Application.Organizations;
using SmartAgri.Domain.Ai;
using SmartAgri.Domain.Organizations;
using SmartAgri.Infrastructure;

namespace SmartAgri.Api.Tests.Organizations;

/// <summary>
/// The month's token sum against real PostgreSQL (M5a plan §3 F, issue #195): month boundaries in
/// <c>Asia/Taipei</c>, embedding calls excluded, null counts as 0, other organizations' calls never
/// mixed in, and the limit column (<c>CK_Organizations_MonthlyTokenLimit</c>).
/// </summary>
[Trait("Category", TestCategories.Docker)]
public sealed class OrganizationTokenUsageSourceTests : IAsyncLifetime
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private readonly PostgresFixture _postgres = new();
    private ServiceProvider? _services;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await using (var dbContext = _postgres.CreateDbContext())
        {
            await dbContext.Database.MigrateAsync(CancellationToken);
        }

        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseNpgsql(_postgres.ConnectionString))
            .AddSingleton<IOrganizationTokenUsageSource, EfOrganizationTokenUsageSource>()
            .BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    private OrganizationTokenUsage Usage(DateTimeOffset now, long defaultLimit = 2_000_000) =>
        new(_services!.GetRequiredService<IOrganizationTokenUsageSource>(), new FixedClock(now), Taipei, defaultLimit);

    [Fact]
    public async Task The_month_is_summed_between_local_midnights_in_the_statistics_time_zone()
    {
        var organization = await CreateOrganizationAsync();
        // November 2026 in Taipei is [2026-10-31 16:00 UTC, 2026-11-30 16:00 UTC).
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 1, 1, At(2026, 10, 31, 15, 59, 59, 999));   // 23:59:59.999 on Oct 31 local: October
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 10, 20, At(2026, 10, 31, 16, 0, 0, 0));     // 00:00:00 on Nov 1 local: first instant of November
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 100, 200, At(2026, 11, 30, 15, 59, 59, 999)); // last instant of November
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 1_000, 2_000, At(2026, 11, 30, 16, 0, 0, 0)); // 00:00 on Dec 1 local: December

        var november = await Usage(At(2026, 11, 15, 4, 0, 0, 0)).GetAsync(organization.Id, CancellationToken);
        var october = await Usage(At(2026, 10, 31, 15, 0, 0, 0)).GetAsync(organization.Id, CancellationToken);
        var december = await Usage(At(2026, 12, 1, 0, 0, 0, 0)).GetAsync(organization.Id, CancellationToken);

        november.Month.ShouldBe("2026-11");
        november.UsedTokens.ShouldBe(10 + 20 + 100 + 200);
        october.Month.ShouldBe("2026-10");
        october.UsedTokens.ShouldBe(2);
        december.Month.ShouldBe("2026-12");
        december.UsedTokens.ShouldBe(3_000);
    }

    [Fact]
    public async Task Embedding_calls_never_count_and_every_chat_purpose_does()
    {
        var organization = await CreateOrganizationAsync();
        var at = At(2026, 11, 10, 2, 0, 0, 0);
        await AddCallAsync(organization, ModelInvocationPurpose.EmbedDocument, 9_000, null, at);
        await AddCallAsync(organization, ModelInvocationPurpose.EmbedQuery, 9_000, null, at);
        var expected = 0L;
        var next = 1L;
        foreach (var purpose in OrganizationTokenUsageRules.CountedPurposes)
        {
            await AddCallAsync(organization, purpose, next, next * 2, at);
            expected += next * 3;
            next *= 10;
        }

        var usage = await Usage(at).GetAsync(organization.Id, CancellationToken);

        usage.UsedTokens.ShouldBe(expected);
        OrganizationTokenUsageRules.CountedPurposes.Count.ShouldBe(6);
    }

    [Fact]
    public async Task A_null_input_or_output_count_is_zero_and_a_failed_call_without_usage_adds_nothing()
    {
        var organization = await CreateOrganizationAsync();
        var at = At(2026, 11, 10, 2, 0, 0, 0);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, null, 40, at);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, 7, null, at);
        await AddCallAsync(organization, ModelInvocationPurpose.GenerateAnswer, null, null, at, succeeded: false);

        var usage = await Usage(at).GetAsync(organization.Id, CancellationToken);

        usage.UsedTokens.ShouldBe(47);
    }

    [Fact]
    public async Task Only_the_organizations_own_calls_count_and_nothing_is_zero_normal()
    {
        var mine = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var at = At(2026, 11, 10, 2, 0, 0, 0);
        await AddCallAsync(other, ModelInvocationPurpose.GenerateAnswer, 5_000, 5_000, at);

        var empty = await Usage(at).GetAsync(mine.Id, CancellationToken);
        empty.UsedTokens.ShouldBe(0);
        empty.State.ShouldBe(TokenUsageState.Normal);
        empty.LimitTokens.ShouldBe(2_000_000);

        await AddCallAsync(mine, ModelInvocationPurpose.GenerateAnswer, 1, 2, at);
        (await Usage(at).GetAsync(mine.Id, CancellationToken)).UsedTokens.ShouldBe(3);
    }

    [Fact]
    public async Task The_organizations_own_limit_beats_the_default_and_states_follow_80_and_100_percent()
    {
        var organization = await CreateOrganizationAsync();
        await SetLimitAsync(organization, 1_000);
        var at = At(2026, 11, 10, 2, 0, 0, 0);

        (await Usage(at, defaultLimit: 50).GetAsync(organization.Id, CancellationToken)).ShouldBe(
            new OrganizationTokenUsageSnapshot("2026-11", 0, 1_000, TokenUsageState.Normal));

        await AddCallAsync(organization, ModelInvocationPurpose.TrialAnswer, 400, 400, at);
        (await Usage(at).GetAsync(organization.Id, CancellationToken)).State.ShouldBe(TokenUsageState.Near);

        await AddCallAsync(organization, ModelInvocationPurpose.AssistantTest, 100, 100, at);
        (await Usage(at).GetAsync(organization.Id, CancellationToken)).State.ShouldBe(TokenUsageState.Exceeded);

        await SetLimitAsync(organization, null);
        var back = await Usage(at, defaultLimit: 5_000).GetAsync(organization.Id, CancellationToken);
        back.LimitTokens.ShouldBe(5_000);
        back.State.ShouldBe(TokenUsageState.Normal);
    }

    [Fact]
    public async Task A_negative_limit_is_refused_by_the_database()
    {
        var organization = await CreateOrganizationAsync();

        var exception = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
            await connection.OpenAsync(CancellationToken);
            await using var command = new NpgsqlCommand("UPDATE \"Organizations\" SET \"MonthlyTokenLimit\" = -1 WHERE \"Id\" = @id", connection);
            command.Parameters.AddWithValue("id", organization.Id);
            await command.ExecuteNonQueryAsync(CancellationToken);
        });

        exception.ConstraintName.ShouldBe("CK_Organizations_MonthlyTokenLimit");
    }

    /// <summary>The month's query reaches <c>ModelInvocations</c> through the organization filter and a
    /// range on <c>At</c> — the <c>(OrganizationId, At)</c> index — and sums in the database.</summary>
    [Fact]
    public async Task The_sum_query_filters_by_organization_range_and_purpose_in_sql()
    {
        var organization = await CreateOrganizationAsync();
        await using var dbContext = _postgres.CreateDbContext(organization.Id);

        var sql = EfOrganizationTokenUsageSource
            .ChatTokens(dbContext, At(2026, 10, 31, 16, 0, 0, 0), At(2026, 11, 30, 16, 0, 0, 0))
            .ToQueryString();

        TestContext.Current.SendDiagnosticMessage(sql);
        sql.ShouldContain("FROM \"ModelInvocations\" AS m");
        sql.ShouldContain("m.\"OrganizationId\" = @ef_filter__CurrentOrganizationId");
        sql.ShouldContain("m.\"At\" >= @startInclusive AND m.\"At\" < @endExclusive");
        sql.ShouldContain("m.\"Purpose\" = ANY (@purposes)");
        sql.ShouldContain("COALESCE(m.\"InputTokens\", 0) + COALESCE(m.\"OutputTokens\", 0)");
    }

    private async Task<Organization> CreateOrganizationAsync()
    {
        var organization = new Organization(Guid.CreateVersion7(), "安心商行", "t" + Guid.NewGuid().ToString("N")[..12]);
        await using var dbContext = _postgres.CreateDbContext();
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(CancellationToken);
        return organization;
    }

    private async Task SetLimitAsync(Organization organization, long? limit)
    {
        await using var dbContext = _postgres.CreateDbContext();
        var tracked = await dbContext.Organizations.SingleAsync(candidate => candidate.Id == organization.Id, CancellationToken);
        tracked.SetMonthlyTokenLimit(limit);
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private async Task AddCallAsync(
        Organization organization, ModelInvocationPurpose purpose, long? input, long? output, DateTimeOffset at, bool succeeded = true)
    {
        await using var dbContext = _postgres.CreateDbContext(organization.Id);
        dbContext.ModelInvocations.Add(ModelInvocation.Record(
            organization.Id, null, null, purpose, "fake", "fake-model", input, output, 12, succeeded, at));
        await dbContext.SaveChangesAsync(CancellationToken);
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second, int millisecond) =>
        new(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
