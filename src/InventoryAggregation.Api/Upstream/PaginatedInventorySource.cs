using InventoryAggregation.Api.Inventory;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Walks the paginated API and returns the whole dataset or nothing. The page client vouches for each
/// page on its own; this class is responsible for what only shows up across pages: records the API
/// delivers more than once, and whether the distinct records add up to the total the API claims.
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
        var firstSeen = new Dictionary<int, (InventoryRecord Record, int Page)>();
        var repeatedRows = 0;
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
            logger.LogInformation(
                "Page {Page} of {TotalPages}: {RowCount} row(s)", pageNumber, page.TotalPages, page.Records.Count);

            if (pageNumber == 1)
            {
                // The first page fixes the size of the run. Because the dataset is static, every later
                // page is then required to report the same total (PageRequest.ExpectedTotalRecords).
                totalRecords = page.TotalRecords;
                totalPages = page.TotalPages;
            }

            foreach (var record in page.Records)
            {
                if (firstSeen.TryAdd(record.Id, (record, pageNumber)))
                {
                    records.Add(record);
                    continue;
                }

                // The id is documented as unique, so this row is a second delivery of a record we
                // already hold. The real API does this on every even page. An identical copy is
                // dropped: counting it would double that record's stock value.
                var (original, originalPage) = firstSeen[record.Id];
                if (record != original)
                {
                    // Two different versions of one record. There is no basis for choosing between
                    // them, and either choice changes a money total, so this is an error.
                    return Inconsistent(
                        $"record {record.Id} was returned with different contents on page {originalPage} and page {pageNumber}");
                }

                repeatedRows++;
            }
        }

        // The completeness check. Counting rows would not do, because rows include the repeats above:
        // the claim being verified is that we hold as many distinct records as the API says exist. If
        // a repeat had displaced a real record anywhere, this is where it shows.
        if (records.Count != totalRecords)
        {
            return Inconsistent(
                $"the API reports {totalRecords} records but {records.Count} distinct records were returned");
        }

        logger.LogInformation(
            "Retrieved {RecordCount} distinct records from {PageCount} page(s); dropped {RepeatedRows} repeated row(s)",
            records.Count, totalPages, repeatedRows);

        return FetchResult<IReadOnlyList<InventoryRecord>>.Success(records);
    }

    private FetchFailure Inconsistent(string detail)
    {
        logger.LogError("Inventory data is inconsistent across pages: {Reason}", detail);
        return new FetchFailure(FetchFailureKind.InvalidData, $"The inventory data is inconsistent: {detail}.");
    }
}
