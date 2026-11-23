using System.Globalization;

namespace InventoryAggregation.Api.Inventory;

/// <summary>
/// Turns inventory records into one summary per category. Pure and synchronous: it has no idea where
/// the records came from, which is what lets it be tested with plain objects.
/// </summary>
public sealed class InventoryAggregator
{
    public InventorySummary Aggregate(IReadOnlyCollection<InventoryRecord> records)
    {
        // Sorting by id up front makes the output deterministic: items come out in id order whatever
        // order the records arrived in.
        var available = records
            .Where(IsAvailable)
            .OrderBy(record => record.Id)
            .ToList();

        var categories = available
            .GroupBy(record => CategoryName(record.Category))
            .Select(Summarise)
            .OrderBy(category => category.Category, StringComparer.Ordinal)
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

    /// <summary>
    /// The one name a category is grouped and reported under. The real data spells its six categories
    /// twenty-four ways ("Home &amp; Kitchen", "  home &amp; kitchen ", "OFFICE SUPPLIES", "outdoor"), and no
    /// single casing dominates, so there is no existing spelling to prefer. Whitespace is trimmed and
    /// collapsed, case is ignored, and the result is written in Title Case, the style of the brief's
    /// own example. The cost is that an acronym would be flattened ("TV" becomes "Tv").
    /// </summary>
    private static string CategoryName(string raw)
    {
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var lowered = string.Join(' ', words).ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lowered);
    }

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
