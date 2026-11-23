using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static InventoryAggregation.Tests.Support.Records;

namespace InventoryAggregation.Tests.Upstream;

/// <summary>
/// Pagination and completeness, tested against a fake page client so no HTTP is involved: these are the
/// checks that only make sense across pages.
/// </summary>
public class PaginatedInventorySourceTests
{
    private const int PageSize = 25;

    [Fact]
    public async Task Collects_every_record_from_every_page()
    {
        var dataset = Sequence(60);
        var pages = new FakePageClient(Serving(dataset));

        var result = await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(dataset, result.Value);
        Assert.Equal([1, 2, 3], pages.Requests.Select(r => r.Page));
        Assert.All(pages.Requests, r => Assert.Equal(PageSize, r.PageSize));
    }

    [Fact]
    public async Task Holds_every_later_page_to_the_total_the_first_page_reported()
    {
        var pages = new FakePageClient(Serving(Sequence(60)));

        await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.Equal([null, 60, 60], pages.Requests.Select(r => r.ExpectedTotalRecords));
    }

    [Fact]
    public async Task Does_not_request_pages_beyond_the_last()
    {
        var pages = new FakePageClient(Serving(Sequence(PageSize))); // exactly one full page

        var result = await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(pages.Requests);
    }

    [Theory]
    [InlineData(0)] // "zero pages"
    [InlineData(1)] // "one empty page"
    public async Task Returns_an_empty_list_for_an_empty_dataset(int reportedTotalPages)
    {
        var pages = new FakePageClient(_ => Page([], 1, totalPages: reportedTotalPages, totalRecords: 0));

        var result = await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Single(pages.Requests);
    }

    [Fact]
    public async Task Returns_the_failure_and_no_records_when_any_page_cannot_be_fetched()
    {
        var failure = new FetchFailure(FetchFailureKind.Unavailable, "Page 2 failed after 4 attempt(s): the API responded 503.");
        var healthy = Serving(Sequence(60));
        var pages = new FakePageClient(request => request.Page == 2 ? failure : healthy(request));

        var result = await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(failure, result.Failure);
        Assert.Null(result.Value); // no partial dataset to aggregate by mistake
        Assert.Equal([1, 2], pages.Requests.Select(r => r.Page)); // and no point asking for page 3
    }

    [Fact]
    public async Task Fails_when_the_same_record_is_delivered_on_two_pages()
    {
        // Page 2 starts one record early: record 25 arrives twice and record 50 is pushed off the end.
        var dataset = Sequence(50);
        var pages = new FakePageClient(request => request.Page == 1
            ? Page(dataset.Take(25), 1, totalPages: 2, totalRecords: 50)
            : Page(dataset.Skip(24).Take(25), 2, totalPages: 2, totalRecords: 50));

        var result = await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.InvalidData, result.Failure.Kind);
        Assert.Contains("record 25 was returned on both page 1 and page 2", result.Failure.Detail);
    }

    [Fact]
    public async Task Fails_when_fewer_records_were_collected_than_the_api_reports()
    {
        var pages = new FakePageClient(_ => Page(Sequence(20), 1, totalPages: 1, totalRecords: 25));

        var result = await CreateSource(pages).GetAllRecordsAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.InvalidData, result.Failure.Kind);
        Assert.Contains("the API reports 25 records but 20 were collected", result.Failure.Detail);
    }

    [Fact]
    public async Task Reports_a_timeout_when_the_whole_collection_overruns_its_deadline()
    {
        var clock = new FakeTimeProvider();
        var healthy = Serving(Sequence(60));
        var stuckOnPageTwo = new TaskCompletionSource();
        var pages = new FakePageClient(async (request, cancellationToken) =>
        {
            if (request.Page == 1)
            {
                return healthy(request);
            }

            stuckOnPageTwo.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable: an infinite delay only ends by cancellation.");
        });
        var source = CreateSource(pages, clock, totalTimeout: TimeSpan.FromSeconds(30));

        var call = source.GetAllRecordsAsync(CancellationToken.None);
        await stuckOnPageTwo.Task.OrTimeout();
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await call.OrTimeout();

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.TimedOut, result.Failure.Kind);
        Assert.Contains("30 s", result.Failure.Detail);
    }

    [Fact]
    public async Task Lets_the_callers_cancellation_through_instead_of_reporting_a_timeout()
    {
        using var caller = new CancellationTokenSource();
        var pages = new FakePageClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable: an infinite delay only ends by cancellation.");
        });

        var call = CreateSource(pages).GetAllRecordsAsync(caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    private static PaginatedInventorySource CreateSource(
        IInventoryPageClient pages,
        TimeProvider? clock = null,
        TimeSpan? totalTimeout = null)
    {
        var options = new InventoryApiOptions
        {
            BaseUrl = "http://inventory.test",
            ApiKey = "test-key",
            PageSize = PageSize,
            TotalTimeout = totalTimeout ?? TimeSpan.FromSeconds(60),
        };

        return new PaginatedInventorySource(
            pages,
            Options.Create(options),
            clock ?? TimeProvider.System,
            NullLogger<PaginatedInventorySource>.Instance);
    }

    private static FetchResult<InventoryPage> Page(
        IEnumerable<InventoryRecord> records, int page, int totalPages, int totalRecords)
        => FetchResult<InventoryPage>.Success(
            new InventoryPage(records.ToList(), page, PageSize, totalPages, totalRecords));

    /// <summary>Behaves like a correct upstream: slices the dataset by page number.</summary>
    private static Func<PageRequest, FetchResult<InventoryPage>> Serving(IReadOnlyList<InventoryRecord> dataset)
        => request => Page(
            dataset.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize),
            request.Page,
            totalPages: (dataset.Count + PageSize - 1) / PageSize,
            totalRecords: dataset.Count);

    /// <summary>A page client with no HTTP behind it. It records what it was asked for.</summary>
    private sealed class FakePageClient(
        Func<PageRequest, CancellationToken, Task<FetchResult<InventoryPage>>> respond) : IInventoryPageClient
    {
        public FakePageClient(Func<PageRequest, FetchResult<InventoryPage>> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<PageRequest> Requests { get; } = [];

        public Task<FetchResult<InventoryPage>> GetPageAsync(PageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }
}
