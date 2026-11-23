using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api.Upstream;

/// <summary>Fetches one page of the Inventory API and only hands it back once it has been validated.</summary>
public interface IInventoryPageClient
{
    Task<FetchResult<InventoryPage>> GetPageAsync(PageRequest request, CancellationToken cancellationToken);
}

/// <param name="ExpectedTotalRecords">
/// The total an earlier page reported. The dataset is static, so a page that reports a different total is
/// treated as a bad response rather than as news.
/// </param>
public readonly record struct PageRequest(int Page, int PageSize, int? ExpectedTotalRecords = null);

/// <summary>A page that has passed validation: its metadata is self-consistent and every record is well-formed.</summary>
public sealed record InventoryPage(
    IReadOnlyList<InventoryRecord> Records,
    int Page,
    int PageSize,
    int TotalPages,
    int TotalRecords);
