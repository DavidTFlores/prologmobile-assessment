using System.Text.Json;
using InventoryAggregation.Api.Serialization;

namespace InventoryAggregation.Api.Upstream;

// The JSON exactly as the Inventory API sends it. Every member is nullable on purpose: with
// non-nullable types System.Text.Json fills a missing field with its default, so a record that arrived
// without "quantity" would become quantity 0 and then be silently dropped as out of stock. Nullable
// members keep "absent" distinguishable from "zero" until InventoryPageValidator has checked it.

internal sealed record PageDto(
    List<RecordDto?>? Records,
    int? Page,
    int? PageSize,
    int? TotalPages,
    int? TotalRecords);

internal sealed record RecordDto(
    int? Id,
    string? Name,
    string? Category,
    decimal? Price,
    int? Quantity,
    DateTimeOffset? LastUpdated);

internal static class WireFormat
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new UtcDateTimeOffsetConverter() },
    };
}
