using System.Text.Json;
using SmartFarm.Contracts;
using SmartFarm.Contracts.History;
using SmartFarm.Contracts.Telemetry;

namespace SmartFarm.Infrastructure.Mongo;

public sealed record ReceivedHistoryCursor(
    string DeviceId, DateTimeOffset From, DateTimeOffset To, DateTime Ingested, string MessageId);

public static class ReceivedHistoryCursorCodec
{
    public static string Encode(ReceivedHistoryQuery query, Reading reading) => Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new ReceivedHistoryCursor(query.DeviceId, query.From, query.To,
            reading.IngestedAt, reading.MessageId)));

    public static ReceivedHistoryCursor? Decode(ReceivedHistoryQuery query)
    {
        if (query.Cursor is null) return null;
        try
        {
            if (query.Cursor.Length > 2048) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<ReceivedHistoryCursor>(Convert.FromBase64String(query.Cursor));
            if (cursor is null || cursor.DeviceId != query.DeviceId || cursor.From != query.From ||
                cursor.To != query.To || cursor.Ingested < query.From.UtcDateTime ||
                cursor.Ingested >= query.To.UtcDateTime || !Guid.TryParse(cursor.MessageId, out _))
                throw new FormatException();
            return cursor;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw new FarmException(FarmErrorCode.InvalidInput, "Invalid cursor for this query");
        }
    }
}
