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
    public static StubHttpHandler.Responder Serving(IReadOnlyList<InventoryRecord> dataset)
        => Serving(dataset, repeatPreviousRecordOnEvenPages: false);

    /// <summary>
    /// Answers the way the real Inventory API was observed to: every even page starts with a repeat of
    /// the previous page's last record, so it holds one row more than the page size.
    /// </summary>
    public static StubHttpHandler.Responder ServingLikeTheRealApi(IReadOnlyList<InventoryRecord> dataset)
        => Serving(dataset, repeatPreviousRecordOnEvenPages: true);

    private static StubHttpHandler.Responder Serving(IReadOnlyList<InventoryRecord> dataset, bool repeatPreviousRecordOnEvenPages)
        => (request, cancellationToken) =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.Parse(query["page"]!);
            var pageSize = int.Parse(query["pageSize"]!);

            var start = (page - 1) * pageSize;
            var slice = dataset.Skip(start).Take(pageSize);
            if (repeatPreviousRecordOnEvenPages && page % 2 == 0 && start < dataset.Count)
            {
                slice = slice.Prepend(dataset[start - 1]);
            }

            return StubHttpHandler.Json(Page(slice, page, pageSize, dataset.Count))(request, cancellationToken);
        };
}
