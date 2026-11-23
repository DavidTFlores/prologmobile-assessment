namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// How long to wait before the next attempt. Kept as a pure function, with the random sample passed in,
/// so the policy can be tested exactly instead of statistically.
/// </summary>
public static class RetryBackoff
{
    /// <param name="failedAttempt">The attempt that just failed, starting at 1.</param>
    /// <param name="jitter">A random sample in [0, 1).</param>
    /// <param name="retryAfter">The wait the server asked for, if it sent a Retry-After header.</param>
    public static TimeSpan Delay(
        int failedAttempt,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        double jitter,
        TimeSpan? retryAfter = null)
    {
        // Exponential: base, 2x, 4x, ... capped so a long run of failures cannot grow without bound.
        var exponential = baseDelay * Math.Pow(2, failedAttempt - 1);
        var capped = exponential < maxDelay ? exponential : maxDelay;

        // Jitter spreads the wait over [50%, 100%) so callers that failed together do not all retry at
        // the same instant and knock the upstream over again.
        var backoff = capped * (0.5 + jitter / 2);

        // A server-supplied Retry-After is a floor: we may wait longer than asked, never less.
        return retryAfter > backoff ? retryAfter.Value : backoff;
    }
}
