using InventoryAggregation.Api.Inventory;
using static InventoryAggregation.Tests.Support.Records;

namespace InventoryAggregation.Tests.Inventory;

public class InventoryAggregatorTests
{
    private readonly InventoryAggregator _aggregator = new();

    [Fact]
    public void Excludes_zero_quantity_records_from_every_figure()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, price: 10m, quantity: 2),
            Record(2, price: 999m, quantity: 0, lastUpdated: BaseTime.AddYears(1)),
        ]);

        var category = Assert.Single(summary.Categories);
        Assert.Equal(20m, category.TotalInventoryValue);
        Assert.Equal(1, category.AvailableRecordCount);
        Assert.Equal(1, category.MostRecentlyUpdated.Id);
        Assert.Equal([1], category.Items.Select(item => item.Id));
    }

    [Fact]
    public void Treats_negative_quantity_as_unavailable_stock()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, price: 10m, quantity: 3),
            Record(2, price: 10m, quantity: -5),
        ]);

        var category = Assert.Single(summary.Categories);
        Assert.Equal(30m, category.TotalInventoryValue);
        Assert.Equal([1], category.Items.Select(item => item.Id));
    }

    [Fact]
    public void Sums_price_times_quantity_exactly()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, price: 149.99m, quantity: 12),
            Record(2, price: 0.10m, quantity: 3),
            Record(3, price: 0.20m, quantity: 3),
        ]);

        // 1799.88 + 0.30 + 0.60. Money is summed as decimal, so there is no binary rounding drift
        // (in double, 0.1 * 3 alone is already 0.30000000000000004).
        Assert.Equal(1800.78m, Assert.Single(summary.Categories).TotalInventoryValue);
    }

    [Fact]
    public void Keeps_each_categorys_figures_separate()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, category: "Electronics", price: 100m, quantity: 1),
            Record(2, category: "Furniture", price: 50m, quantity: 2),
            Record(3, category: "Electronics", price: 25m, quantity: 4),
        ]);

        var electronics = summary.Categories.Single(c => c.Category == "Electronics");
        var furniture = summary.Categories.Single(c => c.Category == "Furniture");

        Assert.Equal(200m, electronics.TotalInventoryValue);
        Assert.Equal(2, electronics.AvailableRecordCount);
        Assert.Equal(100m, furniture.TotalInventoryValue);
        Assert.Equal(1, furniture.AvailableRecordCount);
    }

    [Fact]
    public void Reports_the_most_recently_updated_record_of_each_category()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, lastUpdated: BaseTime.AddDays(1)),
            Record(2, lastUpdated: BaseTime.AddDays(3), name: "Newest"),
            Record(3, lastUpdated: BaseTime.AddDays(2)),
        ]);

        var mostRecent = Assert.Single(summary.Categories).MostRecentlyUpdated;
        Assert.Equal(new RecordReference(2, "Newest", BaseTime.AddDays(3)), mostRecent);
    }

    [Fact]
    public void Compares_timestamps_as_instants_not_as_text()
    {
        // 23:00+02:00 is 21:00 UTC, so the 22:00Z record is the later one despite sorting first as a string.
        var earlier = new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.FromHours(2));
        var later = new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

        var summary = _aggregator.Aggregate([Record(1, lastUpdated: earlier), Record(2, lastUpdated: later)]);

        Assert.Equal(2, Assert.Single(summary.Categories).MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Breaks_last_updated_ties_by_lowest_id()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(7, lastUpdated: BaseTime),
            Record(3, lastUpdated: BaseTime),
            Record(5, lastUpdated: BaseTime),
        ]);

        Assert.Equal(3, Assert.Single(summary.Categories).MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Omits_a_category_whose_records_are_all_out_of_stock()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, category: "Electronics", quantity: 1),
            Record(2, category: "Furniture", quantity: 0),
        ]);

        Assert.Equal(["Electronics"], summary.Categories.Select(c => c.Category));
    }

    [Fact]
    public void Groups_categories_ignoring_case_and_surrounding_whitespace()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(2, category: "electronics"),
            Record(1, category: " Electronics "),
            Record(3, category: "ELECTRONICS"),
        ]);

        var category = Assert.Single(summary.Categories);
        Assert.Equal("Electronics", category.Category);
        Assert.Equal(3, category.AvailableRecordCount);
    }

    [Theory]
    [InlineData("  home & kitchen ", "Home & Kitchen")]
    [InlineData("OFFICE SUPPLIES", "Office Supplies")]
    [InlineData("outdoor", "Outdoor")]
    [InlineData("Toys ", "Toys")]
    [InlineData("office   supplies", "Office Supplies")] // inner runs of whitespace collapse too
    public void Reports_every_spelling_of_a_category_under_one_title_case_name(string spelling, string expected)
    {
        // These spellings are taken from the real dataset, which has 24 of them for 6 categories.
        var summary = _aggregator.Aggregate([Record(1, category: spelling)]);

        Assert.Equal(expected, Assert.Single(summary.Categories).Category);
    }

    [Fact]
    public void Merges_the_real_datasets_mixed_spellings_into_single_categories()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, category: "Home & Kitchen", price: 10m, quantity: 1),
            Record(2, category: "  home & kitchen ", price: 10m, quantity: 2),
            Record(3, category: " Home & Kitchen", price: 10m, quantity: 3),
            Record(4, category: "OFFICE SUPPLIES", price: 5m, quantity: 1),
            Record(5, category: "Office Supplies ", price: 5m, quantity: 1),
        ]);

        Assert.Equal(["Home & Kitchen", "Office Supplies"], summary.Categories.Select(c => c.Category));
        Assert.Equal(60m, summary.Categories[0].TotalInventoryValue);
        Assert.Equal(3, summary.Categories[0].AvailableRecordCount);
        Assert.Equal(10m, summary.Categories[1].TotalInventoryValue);
    }

    [Fact]
    public void Orders_categories_by_name_and_items_by_id_whatever_the_input_order()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(4, category: "Tools"),
            Record(9, category: "Apparel"),
            Record(2, category: "Tools"),
            Record(1, category: "Apparel"),
        ]);

        Assert.Equal(["Apparel", "Tools"], summary.Categories.Select(c => c.Category));
        Assert.Equal([1, 9], summary.Categories[0].Items.Select(item => item.Id));
        Assert.Equal([2, 4], summary.Categories[1].Items.Select(item => item.Id));
    }

    [Fact]
    public void Carries_the_full_item_detail_its_figures_were_computed_from()
    {
        var record = Record(1001, price: 149.99m, quantity: 12, name: "Thermal Receipt Printer");

        var summary = _aggregator.Aggregate([record]);

        var item = Assert.Single(Assert.Single(summary.Categories).Items);
        Assert.Equal(new CategoryItem(1001, "Thermal Receipt Printer", 149.99m, 12, record.LastUpdated), item);
    }

    [Fact]
    public void Every_category_reconciles_with_its_own_items()
    {
        var records = Enumerable.Range(1, 200)
            .Select(id => Record(id, category: $"Category {id % 7}", price: id * 1.25m, quantity: id % 5))
            .ToList();

        var summary = _aggregator.Aggregate(records);

        Assert.All(summary.Categories, category =>
        {
            Assert.Equal(category.Items.Sum(item => item.Price * item.Quantity), category.TotalInventoryValue);
            Assert.Equal(category.Items.Count, category.AvailableRecordCount);
            Assert.Equal(category.Items.Max(item => item.LastUpdated), category.MostRecentlyUpdated.LastUpdated);
            Assert.All(category.Items, item => Assert.True(item.Quantity > 0));
        });
        Assert.Equal(summary.RecordCounts.Available, summary.Categories.Sum(c => c.AvailableRecordCount));
    }

    [Fact]
    public void Counts_retrieved_available_and_excluded_records()
    {
        var summary = _aggregator.Aggregate(
        [
            Record(1, quantity: 5),
            Record(2, quantity: 0),
            Record(3, quantity: 0),
        ]);

        Assert.Equal(new RecordCounts(Retrieved: 3, Available: 1, Excluded: 2), summary.RecordCounts);
    }

    [Fact]
    public void Returns_no_categories_for_an_empty_dataset()
    {
        var summary = _aggregator.Aggregate([]);

        Assert.Empty(summary.Categories);
        Assert.Equal(new RecordCounts(0, 0, 0), summary.RecordCounts);
    }
}
