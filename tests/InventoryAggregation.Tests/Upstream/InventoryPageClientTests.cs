using System.Net;
using System.Net.Http.Headers;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static InventoryAggregation.Tests.Support.Records;

namespace InventoryAggregation.Tests.Upstream;

/// <summary>
/// The resilience behaviour of the API client: what it retries, what it refuses to retry, when it gives
/// up, and what it will not accept even from a 200.
/// </summary>
public class InventoryPageClientTests
{
    private const int MaxAttempts = 3;

    // Every test asks for page 1 of a two-record dataset at two per page, so one full page is the whole thing.
    private static readonly PageRequest Request = new(Page: 1, PageSize: 2);
    private static readonly string ValidPage = UpstreamJson.Page([Record(1), Record(2)], page: 1, pageSize: 2, totalRecords: 2);

    [Fact]
    public async Task Returns_the_page_from_a_single_request_when_the_response_is_valid()
    {
        var handler = new StubHttpHandler().ThenJson(ValidPage);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([Record(1), Record(2)], result.Value.Records);
        Assert.Equal((1, 2, 1, 2), (result.Value.Page, result.Value.PageSize, result.Value.TotalPages, result.Value.TotalRecords));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://inventory.test/api/records?page=1&pageSize=2", request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData((HttpStatusCode)599)] // a 5xx nobody documented is still the server's problem
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Retries_statuses_that_describe_a_temporary_condition(HttpStatusCode status)
    {
        var handler = new StubHttpHandler().Then(status).ThenJson(ValidPage);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData((HttpStatusCode)418)] // not in the API's documentation, and not ours to retry
    [InlineData(HttpStatusCode.NotModified)]
    public async Task Does_not_retry_statuses_that_the_same_request_would_only_get_again(HttpStatusCode status)
    {
        var handler = new StubHttpHandler().Then(status);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.Rejected, result.Failure.Kind);
        Assert.Contains(((int)status).ToString(), result.Failure.Detail);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_number_of_attempts()
    {
        var handler = new StubHttpHandler().ThenAlways(HttpStatusCode.ServiceUnavailable);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.Unavailable, result.Failure.Kind);
        Assert.Contains("503", result.Failure.Detail);
        Assert.Contains($"{MaxAttempts} attempt", result.Failure.Detail);
        Assert.Equal(MaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_when_the_request_never_reaches_the_server()
    {
        var handler = new StubHttpHandler()
            .ThenThrow(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"))
            .ThenJson(ValidPage);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_when_the_connection_drops_mid_response()
    {
        var handler = new StubHttpHandler()
            .ThenThrow(new IOException("The response ended prematurely."))
            .ThenJson(ValidPage);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Reports_an_unreachable_api_as_a_result_instead_of_throwing()
    {
        var handler = new StubHttpHandler().ThenAlways((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (secret-host.internal:443)")));

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.Unavailable, result.Failure.Kind);
        Assert.Contains("NameResolutionError", result.Failure.Detail);
        Assert.DoesNotContain("secret-host", result.Failure.Detail); // the detail can reach a caller
        Assert.Equal(MaxAttempts, handler.Requests.Count);
    }

    // The timing tests below run on a fake clock that only moves when the test moves it, so they are
    // exact and take no real time. The stub's responses complete synchronously, which means that by the
    // time GetPageAsync hands back its task the client is already parked on its backoff delay.

    [Fact]
    public async Task Abandons_an_attempt_that_hangs_and_tries_again()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHttpHandler(clock).ThenHang().ThenJson(ValidPage);
        var client = CreateClient(handler, clock, o => o.AttemptTimeout = TimeSpan.FromSeconds(5));

        var call = client.GetPageAsync(Request, CancellationToken.None);
        await handler.NextRequestAsync();

        clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(call.IsCompleted); // still inside the time limit, so still waiting on attempt one

        clock.Advance(TimeSpan.FromSeconds(0.1));
        var result = await call.OrTimeout();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Reports_a_timeout_when_every_attempt_hangs()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHttpHandler(clock).ThenAlways(StubHttpHandler.Hang);
        var client = CreateClient(handler, clock, o => o.AttemptTimeout = TimeSpan.FromSeconds(5));

        var call = client.GetPageAsync(Request, CancellationToken.None);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await handler.NextRequestAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var result = await call.OrTimeout();

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.TimedOut, result.Failure.Kind);
        Assert.Equal(MaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public async Task Waits_as_long_as_Retry_After_asks_before_trying_again()
    {
        var clock = new FakeTimeProvider();
        var handler = new StubHttpHandler(clock)
            .Then(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)))
            .ThenJson(ValidPage);
        var client = CreateClient(handler, clock);

        var call = client.GetPageAsync(Request, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(call.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        var result = await call.OrTimeout();

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(3), handler.RequestTimes[1] - handler.RequestTimes[0]);
    }

    [Fact]
    public async Task Understands_Retry_After_given_as_a_date()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        var retryAt = clock.GetUtcNow().AddSeconds(4);
        var handler = new StubHttpHandler(clock)
            .Then(HttpStatusCode.ServiceUnavailable, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt))
            .ThenJson(ValidPage);
        var client = CreateClient(handler, clock);

        var call = client.GetPageAsync(Request, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(4));
        var result = await call.OrTimeout();

        Assert.True(result.IsSuccess);
        Assert.Equal(retryAt, handler.RequestTimes[1]);
    }

    [Fact]
    public async Task Gives_up_at_once_when_asked_to_wait_longer_than_it_is_willing_to()
    {
        var handler = new StubHttpHandler()
            .Then(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)));
        var client = CreateClient(handler, configure: o => o.MaxRetryAfter = TimeSpan.FromSeconds(10));

        var result = await client.GetPageAsync(Request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.RateLimited, result.Failure.Kind);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Failure.RetryAfter); // passed on so our own caller can be told
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Stops_without_retrying_when_the_caller_cancels()
    {
        using var caller = new CancellationTokenSource();
        var handler = new StubHttpHandler().ThenHang();

        var call = CreateClient(handler).GetPageAsync(Request, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retries_a_200_whose_page_is_invalid_and_accepts_the_next_good_one()
    {
        var shortPage = UpstreamJson.Page([Record(1)], page: 1, pageSize: 2, totalRecords: 2);
        var handler = new StubHttpHandler().ThenJson(shortPage).ThenJson(ValidPage);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Records.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(PagesThatMustNotBeTrusted))]
    public async Task Never_accepts_a_200_that_fails_validation(string reason, string body, string expectedDetail)
    {
        var handler = new StubHttpHandler().ThenAlways(StubHttpHandler.Json(body));

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.False(result.IsSuccess, reason);
        Assert.Equal(FetchFailureKind.InvalidData, result.Failure.Kind);
        Assert.Contains(expectedDetail, result.Failure.Detail);
        Assert.Equal(MaxAttempts, handler.Requests.Count); // retried, in case the fault was a one-off
    }

    public static TheoryData<string, string, string> PagesThatMustNotBeTrusted()
    {
        const string one = """{"id":1,"name":"A","category":"Tools","price":10,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"}""";
        const string two = """{"id":2,"name":"B","category":"Tools","price":10,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"}""";

        static string Envelope(string records, string metadata = "\"page\":1,\"pageSize\":2,\"totalPages\":1,\"totalRecords\":2")
            => $$"""{"records":[{{records}}],{{metadata}}}""";

        static string WithRecord(string record) => Envelope($"{one},{record}");

        return new TheoryData<string, string, string>
        {
            { "an HTML error page served with a 200", "<html><body>Service unavailable</body></html>", "could not be read" },
            { "a body cut off mid-stream", """{"records":[{"id":1,"name":"A","categ""", "could not be read" },
            { "an empty body", "", "could not be read" },
            { "a JSON null", "null", "missing" },
            { "no paging metadata", """{"records":[]}""", "missing" },
            { "no records array", """{"page":1,"pageSize":2,"totalPages":1,"totalRecords":2}""", "missing" },
            { "a different page than requested", Envelope($"{one},{two}", "\"page\":2,\"pageSize\":2,\"totalPages\":1,\"totalRecords\":2"), "asked for page 1" },
            { "a different page size than requested", Envelope($"{one},{two}", "\"page\":1,\"pageSize\":20,\"totalPages\":1,\"totalRecords\":2"), "asked for pageSize 2" },
            { "fewer records than the metadata promises", Envelope(one), "expected at least 2 records on this page but received 1" },
            { "an empty page that should be full", Envelope(""), "expected at least 2 records on this page but received 0" },
            { "totalPages that contradicts totalRecords", Envelope($"{one},{two}", "\"page\":1,\"pageSize\":2,\"totalPages\":5,\"totalRecords\":2"), "totalPages is 5" },
            { "a negative totalRecords", Envelope("", "\"page\":1,\"pageSize\":2,\"totalPages\":0,\"totalRecords\":-1"), "totalRecords is -1" },
            { "a null entry in records", WithRecord("null"), "a record has no id" },
            { "a record without an id", WithRecord("""{"name":"B","category":"Tools","price":10,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"}"""), "a record has no id" },
            { "a record without a quantity", WithRecord("""{"id":2,"name":"B","category":"Tools","price":10,"lastUpdated":"2026-10-15T12:30:00Z"}"""), "record 2 is missing quantity" },
            { "a record with a null price and null category", WithRecord("""{"id":2,"name":"B","category":null,"price":null,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"}"""), "record 2 is missing category, price" },
            { "a record with a blank category", WithRecord("""{"id":2,"name":"B","category":"  ","price":10,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"}"""), "record 2 has a blank category" },
            { "a record with a negative price", WithRecord("""{"id":2,"name":"B","category":"Tools","price":-10,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"}"""), "record 2 has a negative price" },
            { "a fractional quantity", WithRecord("""{"id":2,"name":"B","category":"Tools","price":10,"quantity":1.5,"lastUpdated":"2026-10-15T12:30:00Z"}"""), "could not be read" },
            { "a timestamp that is not a date", WithRecord("""{"id":2,"name":"B","category":"Tools","price":10,"quantity":1,"lastUpdated":"yesterday"}"""), "could not be read" },
        };
    }

    [Fact]
    public async Task Accepts_a_page_that_carries_more_rows_than_the_page_size()
    {
        // What the real API does on every even page: the previous page's last record comes first, so a
        // page of two holds three rows. That is not a reason to reject the page; the repeat is removed
        // when the pages are combined.
        var pageTwo = UpstreamJson.Page([Record(2), Record(3), Record(4)], page: 2, pageSize: 2, totalRecords: 5);
        var handler = new StubHttpHandler().ThenJson(pageTwo);

        var result = await CreateClient(handler).GetPageAsync(new PageRequest(Page: 2, PageSize: 2), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([2, 3, 4], result.Value.Records.Select(r => r.Id));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Accepts_an_empty_page_past_the_end_of_the_dataset()
    {
        // The real API answers a page beyond the last with a 200 and no records, not a 400.
        var pastTheEnd = UpstreamJson.Page([], page: 9, pageSize: 2, totalRecords: 5);
        var handler = new StubHttpHandler().ThenJson(pastTheEnd);

        var result = await CreateClient(handler).GetPageAsync(new PageRequest(Page: 9, PageSize: 2), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Records);
    }

    [Fact]
    public async Task Rejects_a_page_whose_total_disagrees_with_what_earlier_pages_reported()
    {
        var handler = new StubHttpHandler().ThenAlways(StubHttpHandler.Json(ValidPage));
        var request = Request with { ExpectedTotalRecords = 157 };

        var result = await CreateClient(handler).GetPageAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.InvalidData, result.Failure.Kind);
        Assert.Contains("totalRecords is 2 but an earlier page reported 157", result.Failure.Detail);
    }

    [Fact]
    public async Task Keeps_a_missing_quantity_distinct_from_a_real_zero()
    {
        // A real zero is valid data (the aggregation excludes it later). A missing quantity is a broken
        // record. Deserialising straight into an int would have made the two look identical.
        var zeroQuantity = UpstreamJson.Page([Record(1, quantity: 0), Record(2)], page: 1, pageSize: 2, totalRecords: 2);
        var handler = new StubHttpHandler().ThenJson(zeroQuantity);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Records[0].Quantity);
    }

    [Theory]
    [InlineData("2026-10-15T12:30:00Z")]
    [InlineData("2026-10-15T12:30:00")] // no offset: the contract says UTC, so not this machine's local time
    [InlineData("2026-10-15T14:30:00+02:00")]
    public async Task Reads_every_timestamp_as_the_same_utc_instant(string lastUpdated)
    {
        var body = $$"""
            {"records":[{"id":1,"name":"A","category":"Tools","price":10,"quantity":1,"lastUpdated":"{{lastUpdated}}"}],
             "page":1,"pageSize":2,"totalPages":1,"totalRecords":1}
            """;
        var handler = new StubHttpHandler().ThenJson(body);

        var result = await CreateClient(handler).GetPageAsync(Request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var timestamp = Assert.Single(result.Value.Records).LastUpdated;
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero), timestamp);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
    }

    private static InventoryPageClient CreateClient(
        StubHttpHandler handler,
        TimeProvider? clock = null,
        Action<InventoryApiOptions>? configure = null)
    {
        // Zero backoff by default so retry tests run instantly; tests about timing use the fake clock.
        var options = new InventoryApiOptions
        {
            BaseUrl = "http://inventory.test",
            ApiKey = "test-key",
            MaxAttemptsPerPage = MaxAttempts,
            BaseRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero,
        };
        configure?.Invoke(options);

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://inventory.test/") };

        return new InventoryPageClient(
            http,
            Options.Create(options),
            clock ?? TimeProvider.System,
            NullLogger<InventoryPageClient>.Instance);
    }
}
