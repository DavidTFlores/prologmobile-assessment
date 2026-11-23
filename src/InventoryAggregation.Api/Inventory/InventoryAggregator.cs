namespace InventoryAggregation.Api.Inventory;

/// <summary>
/// Turns inventory records into one summary per category. Pure and synchronous: it has no idea where
/// the records came from, which is what lets it be tested with plain objects.
/// </summary>
public sealed class InventoryAggregator
{
    public InventorySummary Aggregate(IReadOnlyCollection<InventoryRecord> records)
    {
        // Sorting by id up front makes everything downstream deterministic: items come out in id order,
        // and when one category is spelled two ways, the lowest id's spelling is the one reported.
        var available = records
            .Where(IsAvailable)
            .OrderBy(record => record.Id)
            .ToList();

        var categories = available
            .GroupBy(record => record.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(Summarise)
            .OrderBy(category => category.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InventorySummary(
            categories,
            new RecordCounts(
                Retrieved: records.Count,
                Available: available.Count,
                Excluded: records.Count - available.Count));
    }

    /// <summary>
    /// The brief excludes zero quantities because "they are not available stock". A negative quantity is
    /// not available stock either, so the same rule covers it.
    /// </summary>
    private static bool IsAvailable(InventoryRecord record) => record.Quantity > 0;

    private static CategorySummary Summarise(IGrouping<string, InventoryRecord> group)
    {
        var items = group.ToList();

        // Ties on LastUpdated go to the lowest id so the answer never depends on input order.
        var mostRecent = items
            .OrderByDescending(record => record.LastUpdated)
            .ThenBy(record => record.Id)
            .First();

        return new CategorySummary(
            Category: group.Key,
            TotalInventoryValue: items.Sum(record => record.Price * record.Quantity),
            AvailableRecordCount: items.Count,
            MostRecentlyUpdated: new RecordReference(mostRecent.Id, mostRecent.Name, mostRecent.LastUpdated),
            Items: items
                .Select(record => new CategoryItem(
                    record.Id, record.Name, record.Price, record.Quantity, record.LastUpdated))
                .ToList());
    }
}
