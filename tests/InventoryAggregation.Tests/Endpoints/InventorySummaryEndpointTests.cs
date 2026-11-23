using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static InventoryAggregation.Tests.Support.Records;

namespace InventoryAggregation.Tests.Endpoints;

/// <summary>
/// End to end through the real pipeline, with a stub standing in for the upstream API. These tests pin
/// down what a caller of this service actually sees, on good days and bad ones.
/// </summary>
public class InventorySummaryEndpointTests
{
    private const string Route = "/api/inventory/summary";

    private static readonly List<InventoryRecord> Dataset =
    [
        new(1001, "Thermal Receipt Printer", "Electronics", 149.99m, 12, At("2026-10-15T12:30:00Z")),
        new(1002, "Barcode Scanner", "Electronics", 89.50m, 0, At("2026-10-20T09:00:00Z")),
        new(1003, "Stapler", "Office Supplies", 4.25m, 4, At("2026-10-16T08:00:00Z")),
    ];

    [Fact]
    public async Task Returns_the_aggregated_categories_in_the_documented_shape()
    {
        var upstream = new StubHttpHandler().ThenAlways(UpstreamJson.Serving(Dataset));
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertJsonEqual(
            """
            {
              "categories": [
                {
                  "category": "Electronics",
                  "totalInventoryValue": 1799.88,
                  "availableRecordCount": 1,
                  "mostRecentlyUpdated": {
                    "id": 1001,
                    "name": "Thermal Receipt Printer",
                    "lastUpdated": "2026-10-15T12:30:00Z"
                  },
                  "items": [
                    {
                      "id": 1001,
                      "name": "Thermal Receipt Printer",
                      "price": 149.99,
                      "quantity": 12,
                      "lastUpdated": "2026-10-15T12:30:00Z"
                    }
                  ]
                },
                {
                  "category": "Office Supplies",
                  "totalInventoryValue": 17.00,
                  "availableRecordCount": 1,
                  "mostRecentlyUpdated": {
                    "id": 1003,
                    "name": "Stapler",
                    "lastUpdated": "2026-10-16T08:00:00Z"
                  },
                  "items": [
                    {
                      "id": 1003,
                      "name": "Stapler",
                      "price": 4.25,
                      "quantity": 4,
                      "lastUpdated": "2026-10-16T08:00:00Z"
                    }
                  ]
                }
              ],
              "recordCounts": { "retrieved": 3, "available": 2, "excluded": 1 }
            }
            """,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reads_every_page_and_authenticates_each_request()
    {
        var upstream = new StubHttpHandler().ThenAlways(UpstreamJson.Serving(Dataset));
        using var app = new InventoryApp(upstream);

        await app.CreateClient().GetAsync(Route);

        // Three records at two per page is two requests, resolved under the base URL's own path.
        Assert.Equal(
            ["http://inventory.test/v1/api/records?page=1&pageSize=2", "http://inventory.test/v1/api/records?page=2&pageSize=2"],
            upstream.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.All(upstream.Requests, request =>
        {
            Assert.Equal(["test-key"], request.Headers.GetValues("X-Api-Key"));
            Assert.Contains(new MediaTypeWithQualityHeaderValue("application/json"), request.Headers.Accept);
        });
    }

    [Fact]
    public async Task Rides_out_transient_upstream_faults_and_still_returns_the_complete_result()
    {
        var shortPage = UpstreamJson.Page(Dataset.Take(1), page: 1, pageSize: 2, totalRecords: 3);
        var upstream = new StubHttpHandler()
            .Then(HttpStatusCode.InternalServerError)                                         // page 1, attempt 1
            .ThenJson(shortPage)                                                              // page 1, attempt 2: a 200 that is one record short
            .Then(UpstreamJson.Serving(Dataset))                                              // page 1, attempt 3
            .ThenThrow(new HttpRequestException(HttpRequestError.ConnectionError, "reset"))   // page 2, attempt 1
            .Then(HttpStatusCode.TooManyRequests)                                             // page 2, attempt 2
            .ThenAlways(UpstreamJson.Serving(Dataset));                                       // page 2, attempt 3
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(3, body["recordCounts"]!["retrieved"]!.GetValue<int>());
        Assert.Equal(6, upstream.Requests.Count);
    }

    [Fact]
    public async Task Returns_502_when_the_upstream_keeps_failing()
    {
        var upstream = new StubHttpHandler().ThenAlways(HttpStatusCode.InternalServerError);
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadGateway, "Unavailable");
        Assert.Equal("Page 1 failed after 3 attempt(s): the API responded 500.", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Returns_502_when_the_upstream_cannot_be_reached()
    {
        var upstream = new StubHttpHandler().ThenAlways((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (inventory.test:80)")));
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        await AssertProblemAsync(response, HttpStatusCode.BadGateway, "Unavailable");
    }

    [Fact]
    public async Task Returns_502_rather_than_partial_figures_when_a_later_page_is_lost()
    {
        var upstream = new StubHttpHandler()
            .Then(UpstreamJson.Serving(Dataset))                    // page 1 arrives intact
            .ThenAlways(HttpStatusCode.ServiceUnavailable);         // page 2 never does
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadGateway, "Unavailable");
        Assert.StartsWith("Page 2 failed", problem["detail"]!.GetValue<string>());
        Assert.Null(problem["categories"]);
    }

    [Fact]
    public async Task Returns_502_when_the_pages_do_not_add_up_to_a_consistent_dataset()
    {
        // Both pages are individually valid, but page 2 repeats record 1002 where 1003 should be.
        var upstream = new StubHttpHandler()
            .ThenJson(UpstreamJson.Page(Dataset.Take(2), page: 1, pageSize: 2, totalRecords: 3))
            .ThenJson(UpstreamJson.Page(Dataset.Skip(1).Take(1), page: 2, pageSize: 2, totalRecords: 3));
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadGateway, "InvalidData");
        Assert.Contains("record 1002 was returned on both page 1 and page 2", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Returns_502_without_retrying_when_the_upstream_rejects_the_api_key()
    {
        var upstream = new StubHttpHandler().ThenAlways(HttpStatusCode.Unauthorized);
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadGateway, "Rejected");
        Assert.DoesNotContain("test-key", problem.ToJsonString());
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task Returns_503_and_passes_on_Retry_After_when_the_upstream_says_to_back_off_for_long()
    {
        var upstream = new StubHttpHandler().ThenAlways(
            HttpStatusCode.TooManyRequests,
            r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));
        using var app = new InventoryApp(upstream);

        var response = await app.CreateClient().GetAsync(Route);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "RateLimited");
        Assert.Equal(TimeSpan.FromSeconds(120), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Returns_504_when_the_upstream_does_not_answer_in_time()
    {
        var upstream = new StubHttpHandler().ThenAlways(StubHttpHandler.Hang);
        using var app = new InventoryApp(upstream, options =>
        {
            options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
            options.MaxAttemptsPerPage = 2;
        });

        var response = await app.CreateClient().GetAsync(Route);

        await AssertProblemAsync(response, HttpStatusCode.GatewayTimeout, "TimedOut");
    }

    [Fact]
    public async Task Hides_the_details_of_an_unexpected_bug_behind_a_plain_500()
    {
        using var app = new InventoryApp(
            new StubHttpHandler(),
            configureServices: services => services.AddTransient<IInventorySource, ExplodingSource>());

        var response = await app.CreateClient().GetAsync(Route);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("the bug's own message", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
    }

    [Fact]
    public void Refuses_to_start_without_an_api_key()
    {
        using var app = new InventoryApp(new StubHttpHandler(), options => options.ApiKey = "");

        var exception = Assert.Throws<OptionsValidationException>(() => app.CreateClient());

        Assert.Contains("ApiKey", exception.Message);
    }

    [Fact]
    public void Refuses_to_start_with_a_base_url_that_is_not_a_url()
    {
        using var app = new InventoryApp(new StubHttpHandler(), options => options.BaseUrl = "inventory.test");

        var exception = Assert.Throws<OptionsValidationException>(() => app.CreateClient());

        Assert.Contains("BaseUrl", exception.Message);
    }

    private static DateTimeOffset At(string iso8601) => DateTimeOffset.Parse(iso8601);

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static async Task<JsonNode> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedFailureKind)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadJsonAsync(response);
        Assert.Equal((int)expectedStatus, problem["status"]!.GetValue<int>());
        Assert.Equal(expectedFailureKind, problem["failureKind"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(problem["title"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(problem["detail"]!.GetValue<string>()));
        return problem;
    }

    /// <summary>Compares JSON by content, ignoring whitespace, and shows a readable diff when it differs.</summary>
    private static void AssertJsonEqual(string expected, string actual)
    {
        var indented = new JsonSerializerOptions { WriteIndented = true };
        Assert.Equal(
            JsonNode.Parse(expected)!.ToJsonString(indented),
            JsonNode.Parse(actual)!.ToJsonString(indented));
    }

    private sealed class ExplodingSource : IInventorySource
    {
        public Task<FetchResult<IReadOnlyList<InventoryRecord>>> GetAllRecordsAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("the bug's own message");
    }
}
