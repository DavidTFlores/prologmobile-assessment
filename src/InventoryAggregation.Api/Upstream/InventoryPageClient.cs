using System.Text.Json;
using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// The only class that speaks HTTP to the Inventory API. One call to <see cref="GetPageAsync"/> is one
/// logical page: it is attempted until a response passes validation, a failure turns out to be one that
/// a retry cannot fix, or the attempts run out.
///
/// The retry loop is written out by hand rather than delegated to a resilience handler because the
/// decision to retry depends on the parsed body (a 200 can carry a bad page), and a handler in the
/// HttpClient pipeline only sees status codes and exceptions.
/// </summary>
public sealed class InventoryPageClient(
    HttpClient http,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider,
    ILogger<InventoryPageClient> logger) : IInventoryPageClient
{
    private readonly InventoryApiOptions _options = options.Value;

    public async Task<FetchResult<InventoryPage>> GetPageAsync(
        PageRequest request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (result, canRetry) = await AttemptAsync(request, cancellationToken);
            if (result.IsSuccess)
            {
                return result;
            }

            var failure = result.Failure;
            var outOfAttempts = attempt >= _options.MaxAttemptsPerPage;
            var askedToWaitTooLong = failure.RetryAfter > _options.MaxRetryAfter;

            if (!canRetry || outOfAttempts || askedToWaitTooLong)
            {
                logger.LogError(
                    "Giving up on page {Page} after {Attempts} attempt(s): {Reason}",
                    request.Page, attempt, failure.Detail);

                return failure with
                {
                    Detail = $"Page {request.Page} failed after {attempt} attempt(s): {failure.Detail}.",
                };
            }

            var delay = RetryBackoff.Delay(
                attempt,
                _options.BaseRetryDelay,
                _options.MaxRetryDelay,
                Random.Shared.NextDouble(),
                failure.RetryAfter);

            logger.LogWarning(
                "Page {Page} attempt {Attempt} of {MaxAttempts} failed: {Reason}. Retrying in {DelayMs} ms",
                request.Page, attempt, _options.MaxAttemptsPerPage, failure.Detail, (int)delay.TotalMilliseconds);

            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    /// <summary>One request, classified into success, a failure worth retrying, or a failure that is not.</summary>
    private async Task<(FetchResult<InventoryPage> Result, bool CanRetry)> AttemptAsync(
        PageRequest request,
        CancellationToken cancellationToken)
    {
        // Each attempt gets its own time limit, covering the body as well as the headers. It is linked
        // to the caller's token so that a caller who goes away still cancels the request.
        using var attemptTimeout = new CancellationTokenSource(_options.AttemptTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);

        try
        {
            using var response = await http.GetAsync(
                $"api/records?page={request.Page}&pageSize={request.PageSize}",
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);

            if (!response.IsSuccessStatusCode)
            {
                return ClassifyErrorStatus(response);
            }

            await using var body = await response.Content.ReadAsStreamAsync(linked.Token);
            var dto = await JsonSerializer.DeserializeAsync<PageDto>(body, WireFormat.JsonOptions, linked.Token);

            // A 200 is not proof of a good page. A page that fails validation is retried like any other
            // transient fault, because the dataset is static and a correct answer does exist.
            return (InventoryPageValidator.Validate(dto, request), CanRetry: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller is still waiting, so this was our own per-attempt timeout. If the caller had
            // cancelled, the filter would not match and the cancellation would propagate untouched.
            return Transient(
                FetchFailureKind.TimedOut,
                $"no complete response within {_options.AttemptTimeout.TotalSeconds:0.###} s");
        }
        catch (HttpRequestException exception)
        {
            // DNS, connection refused or reset, TLS. Only the error category goes into the failure
            // detail, since that can end up in front of a caller; the full exception goes to the log.
            logger.LogDebug(exception, "Transport failure requesting page {Page}", request.Page);
            return Transient(
                FetchFailureKind.Unavailable,
                $"the request failed in transit ({exception.HttpRequestError})");
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Connection lost while reading page {Page}", request.Page);
            return Transient(FetchFailureKind.Unavailable, "the connection dropped while the response was being read");
        }
        catch (JsonException exception)
        {
            // Truncated JSON, an HTML error page served with a 200, a field of the wrong type.
            return Transient(
                FetchFailureKind.InvalidData,
                $"the response body could not be read as a records page ({exception.Message})");
        }
    }

    private (FetchResult<InventoryPage> Result, bool CanRetry) ClassifyErrorStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var retryAfter = ReadRetryAfter(response);

        return status switch
        {
            429 => Transient(FetchFailureKind.RateLimited, "the API is rate limiting us (429)", retryAfter),

            // 408 and every 5xx describe the server's state at that moment, not our request, so the same
            // request can succeed later. That includes 5xx codes the API documentation does not list.
            408 or >= 500 => Transient(FetchFailureKind.Unavailable, $"the API responded {status}", retryAfter),

            401 or 403 => Permanent($"the API rejected our credentials ({status})"),

            // Any other 4xx (or an unfollowed 3xx) says the request itself is wrong. Repeating it
            // unchanged would only add load, so fail now and say why.
            _ => Permanent($"the API responded {status}, which retrying the same request will not fix"),
        };
    }

    /// <summary>Retry-After can be a number of seconds or an HTTP date. Either becomes a wait from now.</summary>
    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? header?.Date - timeProvider.GetUtcNow();
        return wait > TimeSpan.Zero ? wait : null;
    }

    private static (FetchResult<InventoryPage>, bool) Transient(
        FetchFailureKind kind, string detail, TimeSpan? retryAfter = null)
        => (new FetchFailure(kind, detail, retryAfter), true);

    private static (FetchResult<InventoryPage>, bool) Permanent(string detail)
        => (new FetchFailure(FetchFailureKind.Rejected, detail), false);
}
