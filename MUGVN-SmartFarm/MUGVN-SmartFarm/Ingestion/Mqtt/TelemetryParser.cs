using System.Globalization;
using System.Text.Json;

namespace SmartFarm.Ingestion.Mqtt;

public sealed class InvalidTelemetryException(string message) : Exception(message);

public static class TelemetryParser
{
    public static Reading Parse(string topic, ReadOnlyMemory<byte> payload, string prefix, string farmId,
        DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var deviceId = Text(root, "device_id");
            if (deviceId.Contains('/') || topic != $"{prefix}/{deviceId}/env")
                throw new InvalidTelemetryException("Topic/payload device mismatch");
            var id = Text(root, "message_id");
            if (!Guid.TryParse(id, out var guid)) throw new InvalidTelemetryException("message_id must be a UUID");
            var source = Date(Text(root, "timestamp"));
            var emitted = Date(Text(root, "emitted_at"));
            var node = root.GetProperty("node_id").GetInt32();
            var missing = root.GetProperty("missing_flag").GetInt32();
            var violations = root.GetProperty("violated_variable_count").GetInt32();
            if (node <= 0 || missing is not (0 or 1) || violations is < 0 or > 7)
                throw new InvalidTelemetryException("Invalid source identity or quality indicators");
            return new Reading
            {
                MessageId = guid.ToString("D"),
                Sensor = new(farmId, deviceId, node),
                SourceTimestamp = source,
                EmittedAt = emitted,
                IngestedAt = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()).UtcDateTime,
                Measurements = new(Number(root, "air_temperature"), Number(root, "air_humidity"),
                    Number(root, "soil_temperature"),
                    Number(root, "soil_humidity"), Number(root, "ph"), Number(root, "ec"), Number(root, "light"),
                    missing,
                    Text(root, "anomaly_label"), violations)
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
                                      or FormatException or OverflowException)
        {
            throw new InvalidTelemetryException("Malformed telemetry or missing/invalid required field");
        }
    }

    public static (string DeviceId, string Status) ParseAvailability(string topic, ReadOnlyMemory<byte> payload,
        string prefix)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var id = Text(doc.RootElement, "device_id");
            var status = Text(doc.RootElement, "status");
            if (id.Contains('/') || topic != $"{prefix}/{id}/avail" || status is not ("online" or "offline"))
                throw new InvalidTelemetryException("Invalid availability identity/status");
            return (id, status);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidTelemetryException("Malformed availability");
        }
    }

    private static string Text(JsonElement e, string key) =>
        e.GetProperty(key).GetString() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new InvalidTelemetryException($"Missing {key}");

    private static double Number(JsonElement e, string key) =>
        e.GetProperty(key).GetDouble() is var n && double.IsFinite(n)
            ? n
            : throw new InvalidTelemetryException($"Non-finite {key}");

    // The producer emits both ISO dates and CSV dates such as "2025-07-24 00:00:00+00:00".
    private static DateTime Date(string value)
    {
        if (value.Length < 20 ||
            !(value.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d\d:\d\d$")))
            throw new InvalidTelemetryException("Timestamp must include UTC offset");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces,
            out var date)
            ? DateTimeOffset.FromUnixTimeMilliseconds(date.ToUnixTimeMilliseconds()).UtcDateTime
            : throw new InvalidTelemetryException("Invalid timestamp");
    }
}