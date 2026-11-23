namespace InventoryAggregation.Api.Inventory;

/// <summary>The aggregation result. These records are also the endpoint's response contract.</summary>
public sealed record InventorySummary(
    IReadOnlyList<CategorySummary> Categories,
    RecordCounts RecordCounts);

/// <summary>
/// Lets a caller reconcile the response against the upstream dataset:
/// <c>Retrieved = Available + Excluded</c>, and <c>Available</c> is the sum of every category's count.
/// </summary>
public sealed record RecordCounts(int Retrieved, int Available, int Excluded);

public sealed record CategorySummary(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    RecordReference MostRecentlyUpdated,
    IReadOnlyList<CategoryItem> Items);

public sealed record RecordReference(int Id, string Name, DateTimeOffset LastUpdated);

public sealed record CategoryItem(int Id, string Name, decimal Price, int Quantity, DateTimeOffset LastUpdated);
