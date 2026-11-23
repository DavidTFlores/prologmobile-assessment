using InventoryAggregation.Api.Inventory;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Decides whether a successfully parsed response is actually the page that was asked for. A 200 with
/// well-formed JSON can still be the wrong page, a short page, or carry broken records; none of those
/// may reach the aggregation. Every message names the rule that failed, because that message is what
/// someone debugging a misbehaving upstream will read first.
/// </summary>
internal static class InventoryPageValidator
{
    public static FetchResult<InventoryPage> Validate(PageDto? dto, PageRequest request)
    {
        if (dto is not
            {
                Records: { } records,
                Page: { } page,
                PageSize: { } pageSize,
                TotalPages: { } totalPages,
                TotalRecords: { } totalRecords,
            })
        {
            return Invalid("the response is missing its records or paging metadata");
        }

        if (page != request.Page)
        {
            return Invalid($"asked for page {request.Page} but the response is page {page}");
        }

        if (pageSize != request.PageSize)
        {
            return Invalid($"asked for pageSize {request.PageSize} but the response used {pageSize}");
        }

        if (totalRecords < 0)
        {
            return Invalid($"totalRecords is {totalRecords}");
        }

        if (request.ExpectedTotalRecords is { } expectedTotal && totalRecords != expectedTotal)
        {
            return Invalid($"totalRecords is {totalRecords} but an earlier page reported {expectedTotal}");
        }

        // An empty dataset may legitimately be described as either zero pages or one empty page.
        var expectedPages = (int)(((long)totalRecords + pageSize - 1) / pageSize);
        if (totalPages != expectedPages && !(totalRecords == 0 && totalPages == 1))
        {
            return Invalid(
                $"totalPages is {totalPages} but {totalRecords} records at {pageSize} per page makes {expectedPages}");
        }

        // Full pages everywhere except the last, which holds the remainder. This is what catches a
        // truncated page: without it a short 200 would quietly shrink the dataset.
        var expectedCount = (int)Math.Clamp(totalRecords - ((long)page - 1) * pageSize, 0, pageSize);
        if (records.Count != expectedCount)
        {
            return Invalid($"expected {expectedCount} records on this page but received {records.Count}");
        }

        var validRecords = new List<InventoryRecord>(records.Count);
        var idsOnPage = new HashSet<int>();
        foreach (var recordDto in records)
        {
            var (record, problem) = ToRecord(recordDto);
            if (record is null)
            {
                return Invalid(problem!);
            }

            if (!idsOnPage.Add(record.Id))
            {
                return Invalid($"record {record.Id} appears more than once on the page");
            }

            validRecords.Add(record);
        }

        return FetchResult<InventoryPage>.Success(
            new InventoryPage(validRecords, page, pageSize, totalPages, totalRecords));
    }

    private static (InventoryRecord? Record, string? Problem) ToRecord(RecordDto? dto)
    {
        if (dto?.Id is not { } id)
        {
            return (null, "a record has no id");
        }

        if (dto is not
            {
                Name: { } name,
                Category: { } category,
                Price: { } price,
                Quantity: { } quantity,
                LastUpdated: { } lastUpdated,
            })
        {
            return (null, $"record {id} is missing {string.Join(", ", MissingFields(dto))}");
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return (null, $"record {id} has a blank category");
        }

        // No reading of a negative unit price produces a meaningful inventory value. A negative
        // quantity is different: it is accepted here and treated as "no available stock" downstream.
        if (price < 0)
        {
            return (null, $"record {id} has a negative price ({price})");
        }

        return (new InventoryRecord(id, name, category, price, quantity, lastUpdated), null);
    }

    private static IEnumerable<string> MissingFields(RecordDto dto)
    {
        if (dto.Name is null) yield return "name";
        if (dto.Category is null) yield return "category";
        if (dto.Price is null) yield return "price";
        if (dto.Quantity is null) yield return "quantity";
        if (dto.LastUpdated is null) yield return "lastUpdated";
    }

    private static FetchFailure Invalid(string detail) => new(FetchFailureKind.InvalidData, detail);
}
