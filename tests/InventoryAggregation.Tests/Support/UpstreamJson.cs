using System.Text.Json;
using System.Web;
using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Tests.Support;

/// <summary>Builds Inventory API response bodies in the shape the real API documents.</summary>
internal static class UpstreamJson
{
    public static string Page(
        IEnumerable<InventoryRecord> records,
        int page,
        int pageSize,
        int totalRecords,
        int? totalPages = null)
        => JsonSerializer.Serialize(new
        {
            records = records.Select(Record),
            page,
            pageSize,
            totalPages = totalPages ?? (totalRecords + pageSize - 1) / pageSize,
            totalRecords,
        });

    public static object Record(InventoryRecord record) => new
    {
        id = record.Id,
        name = record.Name,
        category = record.Category,
        price = record.Price,
        quantity = record.Quantity,
        lastUpdated = record.LastUpdated.UtcDateTime,
    };

    /// <summary>Answers like a well-behaved Inventory API: slices the dataset by the request's page and pageSize.</summary>
    public static StubHttpHandler.Responder Serving(IReadOnlyList<InventoryRecord> dataset) => (request, cancellationToken) =>
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var page = int.Parse(query["page"]!);
        var pageSize = int.Parse(query["pageSize"]!);
        var slice = dataset.Skip((page - 1) * pageSize).Take(pageSize);

        return StubHttpHandler.Json(Page(slice, page, pageSize, dataset.Count))(request, cancellationToken);
    };
}
