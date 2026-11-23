namespace InventoryAggregation.Api.Inventory;

/// <summary>
/// Where inventory records come from, as far as the rest of the application is concerned. Nothing on
/// this interface mentions pages, HTTP or retries, which is the seam that keeps the business logic
/// ignorant of how the data is obtained.
/// </summary>
public interface IInventorySource
{
    /// <summary>
    /// Every record in the inventory, or the reason the complete set could not be obtained.
    /// Never a partial set.
    /// </summary>
    Task<FetchResult<IReadOnlyList<InventoryRecord>>> GetAllRecordsAsync(CancellationToken cancellationToken);
}
