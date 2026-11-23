using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Walks the paginated API and returns the whole dataset or nothing. The page client vouches for each
/// page on its own; this class is responsible for what only shows up across pages: that the pages add
/// up to the total the API claims, and that no record was delivered twice.
/// </summary>
public sealed class PaginatedInventorySource(
    IInventoryPageClient pages,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider,
    ILogger<PaginatedInventorySource> logger) : IInventorySource
{
    private readonly InventoryApiOptions _options = options.Value;

    public async Task<FetchResult<IReadOnlyList<InventoryRecord>>> GetAllRecordsAsync(
        CancellationToken cancellationToken)
    {
        // Per-page retries bound each page, but pages multiply. This deadline bounds the whole
        // collection so the worst case for a caller is a known number, not pages x attempts x timeout.
        using var deadline = new CancellationTokenSource(_options.TotalTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            return await CollectAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Inventory retrieval exceeded its {TotalTimeout} deadline", _options.TotalTimeout);

            return new FetchFailure(
                FetchFailureKind.TimedOut,
                $"The inventory could not be retrieved within {_options.TotalTimeout.TotalSeconds:0.###} s.");
        }
    }

    private async Task<FetchResult<IReadOnlyList<InventoryRecord>>> CollectAsync(CancellationToken cancellationToken)
    {
        var records = new List<InventoryRecord>();
        var pageSeenOn = new Dictionary<int, int>();
        int? totalRecords = null;
        var totalPages = 1;

        // Sequential on purpose: it is a handful of requests, and fetching them in parallel would
        // multiply the load on an upstream that is already struggling whenever retries are happening.
        for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
        {
            var result = await pages.GetPageAsync(
                new PageRequest(pageNumber, _options.PageSize, totalRecords),
                cancellationToken);

            if (!result.IsSuccess)
            {
                // All or nothing. Figures computed from some of the pages would look just as
                // authoritative as correct ones, which is worse than no answer.
                return result.Failure;
            }

            var page = result.Value;
            if (pageNumber == 1)
            {
                // The first page fixes the size of the run. Because the dataset is static, every later
                // page is then required to report the same total (PageRequest.ExpectedTotalRecords).
                totalRecords = page.TotalRecords;
                totalPages = page.TotalPages;
            }

            foreach (var record in page.Records)
            {
                if (!pageSeenOn.TryAdd(record.Id, pageNumber))
                {
                    // Ids are unique, so a repeat means the pages overlap, and an overlap means some
                    // other record was pushed out. Dropping the duplicate would hide that it is missing.
                    return Inconsistent(
                        $"record {record.Id} was returned on both page {pageSeenOn[record.Id]} and page {pageNumber}");
                }

                records.Add(record);
            }
        }

        // Follows from the per-page checks, but it is the property the whole design exists to
        // guarantee, so it is asserted directly instead of being left as an inference.
        if (records.Count != totalRecords)
        {
            return Inconsistent($"the API reports {totalRecords} records but {records.Count} were collected");
        }

        logger.LogInformation(
            "Retrieved {RecordCount} records from {PageCount} page(s)", records.Count, totalPages);

        return FetchResult<IReadOnlyList<InventoryRecord>>.Success(records);
    }

    private FetchFailure Inconsistent(string detail)
    {
        logger.LogError("Inventory data is inconsistent across pages: {Reason}", detail);
        return new FetchFailure(FetchFailureKind.InvalidData, $"The inventory data is inconsistent: {detail}.");
    }
}
