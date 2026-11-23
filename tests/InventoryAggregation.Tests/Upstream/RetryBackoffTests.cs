using InventoryAggregation.Api.Upstream;

namespace InventoryAggregation.Tests.Upstream;

public class RetryBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(5);

    // Just under 1: the top of the jitter range, where the delay is (all but) the full backoff.
    private const double TopOfJitter = 0.999999;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 5)] // 8 s, capped
    [InlineData(9, 5)]
    public void Doubles_with_each_failed_attempt_up_to_the_cap(int failedAttempt, double expectedSeconds)
    {
        var delay = RetryBackoff.Delay(failedAttempt, Base, Max, TopOfJitter);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(0.5, 3.0)]
    [InlineData(TopOfJitter, 4.0)]
    public void Jitter_spreads_the_delay_between_half_and_all_of_the_backoff(double jitter, double expectedSeconds)
    {
        var delay = RetryBackoff.Delay(failedAttempt: 3, Base, Max, jitter);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 3);
    }

    [Fact]
    public void A_longer_Retry_After_overrides_the_backoff()
    {
        var delay = RetryBackoff.Delay(failedAttempt: 1, Base, Max, jitter: 0.5, retryAfter: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void A_shorter_Retry_After_never_shortens_the_backoff()
    {
        var delay = RetryBackoff.Delay(failedAttempt: 3, Base, Max, jitter: 0.5, retryAfter: TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }
}
