using Shouldly;
using SmartAgri.Application.Jobs;

namespace SmartAgri.Application.Tests.Jobs;

public class JobRetryPolicyTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(6, 960)]
    public void The_delay_doubles_after_each_failed_attempt(int attempt, int expectedSeconds)
    {
        JobRetryPolicy.Backoff(attempt, Base, Max).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(int.MaxValue)]
    public void The_delay_never_exceeds_the_maximum(int attempt)
    {
        JobRetryPolicy.Backoff(attempt, Base, Max).ShouldBe(Max);
    }

    [Fact]
    public void Nonsensical_arguments_are_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => JobRetryPolicy.Backoff(0, Base, Max));
        Should.Throw<ArgumentOutOfRangeException>(() => JobRetryPolicy.Backoff(1, TimeSpan.Zero, Max));
        Should.Throw<ArgumentOutOfRangeException>(() => JobRetryPolicy.Backoff(1, Max, Base));
    }
}
