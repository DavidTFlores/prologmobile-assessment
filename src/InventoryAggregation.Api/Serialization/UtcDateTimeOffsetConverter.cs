using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventoryAggregation.Api.Serialization;

/// <summary>
/// Reads and writes timestamps the way the Inventory API documents them: ISO 8601, UTC.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTime(out var value))
        {
            throw new JsonException("Expected an ISO 8601 timestamp.");
        }

        // The default DateTimeOffset reader would silently apply this machine's time zone to a value
        // with no offset. Reading a DateTime first keeps that case visible (Kind is Unspecified), and
        // the contract says such a value is UTC.
        return value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : new DateTimeOffset(value.ToUniversalTime());
    }

    /// <summary>Always writes UTC with a "Z" suffix, matching the format the upstream uses.</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime);
}
