using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Tests.Support;

/// <summary>Builds records with sensible defaults so each test only states what it cares about.</summary>
internal static class Records
{
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static InventoryRecord Record(
        int id,
        string category = "Electronics",
        decimal price = 10m,
        int quantity = 1,
        DateTimeOffset? lastUpdated = null,
        string? name = null)
        => new(id, name ?? $"Item {id}", category, price, quantity, lastUpdated ?? BaseTime.AddMinutes(id));

    /// <summary>Records with ids 1..count, all in stock.</summary>
    public static List<InventoryRecord> Sequence(int count)
        => Enumerable.Range(1, count).Select(id => Record(id)).ToList();
}
