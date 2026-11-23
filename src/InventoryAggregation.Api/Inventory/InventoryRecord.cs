namespace InventoryAggregation.Api.Inventory;

/// <summary>
/// An inventory record that has already passed validation at the upstream boundary, so code that
/// receives one does not need to re-check for missing fields.
/// </summary>
public sealed record InventoryRecord(
    int Id,
    string Name,
    string Category,
    decimal Price,
    int Quantity,
    DateTimeOffset LastUpdated);
