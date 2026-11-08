using System.Text.Json;
using SmartFarm.Contracts;
using SmartFarm.Contracts.History;
using SmartFarm.Contracts.Telemetry;
namespace SmartFarm.Infrastructure.Mongo;
public sealed record HistoryCursor(
    string DeviceId,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTime Source,
    DateTime Emitted,
    string MessageId);

public static class CursorCodec
{
    public static string Encode(HistoryQuery query, Reading reading) => Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(
            new HistoryCursor(query.DeviceId, query.From, query.To, reading.SourceTimestamp, reading.EmittedAt,
                reading.MessageId)));

    public static HistoryCursor? Decode(HistoryQuery query)
    {
        if (query.Cursor is null) return null;
        try
        {
            if (query.Cursor.Length > 2048) throw new FormatException();
            var c = JsonSerializer.Deserialize<HistoryCursor>(Convert.FromBase64String(query.Cursor));
            if (c is null || c.DeviceId != query.DeviceId || c.From != query.From || c.To != query.To ||
                c.Source < query.From.UtcDateTime || c.Source >= query.To.UtcDateTime ||
                !Guid.TryParse(c.MessageId, out _)) throw new FormatException();
            return c;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw new FarmException(FarmErrorCode.InvalidInput, "Invalid cursor for this query");
        }
    }
}