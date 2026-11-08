using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace SmartFarm.Tests;

public sealed class TelemetryParserTests
{
    internal const string Id = "d019a0f3-cdef-4a21-b456-012345678901";

    internal static byte[] Payload(Action<JsonObject>? edit = null)
    {
        var obj = JsonNode.Parse(
                """{"message_id":"d019a0f3-cdef-4a21-b456-012345678901","device_id":"pi4-1","node_id":6,"timestamp":"2025-07-24 00:00:00+00:00","emitted_at":"2026-11-05T01:00:00Z","air_temperature":39.4785,"air_humidity":33.18,"soil_temperature":20.26,"soil_humidity":33.08,"ph":7.03,"ec":0.30,"light":26512.8,"missing_flag":0,"anomaly_label":"drift","violated_variable_count":2}""")
            !.AsObject();
        edit?.Invoke(obj);
        return Encoding.UTF8.GetBytes(obj.ToJsonString());
    }

    [Fact]
    public void ActualCsvFormat_PreservesSourceTimeAndAnomalousMeasurement()
    {
        var now = DateTimeOffset.Parse("2026-11-05T02:00:00Z");
        var reading = TelemetryParser.Parse("mugvn/farm/pi4-1/env", Payload(), "mugvn/farm", "farm-001", now);
        Assert.Equal(new DateTime(2025, 7, 24, 0, 0, 0, DateTimeKind.Utc), reading.SourceTimestamp);
        Assert.Equal(now.UtcDateTime, reading.IngestedAt);
        Assert.Equal("drift", reading.Measurements.AnomalyLabel);
        Assert.Equal(39.4785, reading.Measurements.AirTemperature);
        Assert.Equal(6, reading.Sensor.NodeId);
        Assert.Equal(Id, reading.MessageId);
    }

    [Fact]
    public void OffsetTimestamp_IsConvertedToUtc()
    {
        var r = TelemetryParser.Parse("mugvn/farm/pi4-1/env",
            Payload(o => o["timestamp"] = "2025-07-24T07:00:00+07:00"), "mugvn/farm", "farm", DateTimeOffset.UtcNow);
        Assert.Equal(new DateTime(2025, 7, 24, 0, 0, 0, DateTimeKind.Utc), r.SourceTimestamp);
    }

    [Theory]
    [InlineData("message_id")]
    [InlineData("timestamp")]
    [InlineData("emitted_at")]
    [InlineData("node_id")]
    [InlineData("air_temperature")]
    [InlineData("anomaly_label")]
    public void MissingRequiredField_IsRejected(string field) => Assert.Throws<InvalidTelemetryException>(() =>
        TelemetryParser.Parse("mugvn/farm/pi4-1/env", Payload(o => o.Remove(field)), "mugvn/farm", "farm",
            DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("message_id", "not-a-uuid")]
    [InlineData("device_id", "pi4-2")]
    [InlineData("timestamp", "2025-07-24T00:00:00")]
    [InlineData("timestamp", "invalidZ")]
    [InlineData("anomaly_label", "")]
    public void InvalidIdentityOrTimestamp_IsRejected(string key, string value) =>
        Assert.Throws<InvalidTelemetryException>(() => TelemetryParser.Parse("mugvn/farm/pi4-1/env",
            Payload(o => o[key] = value), "mugvn/farm", "farm", DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("missing_flag", 2)]
    [InlineData("violated_variable_count", -1)]
    [InlineData("violated_variable_count", 8)]
    [InlineData("node_id", 0)]
    public void InvalidQualityOrNode_IsRejected(string field, int value) =>
        Assert.Throws<InvalidTelemetryException>(() => TelemetryParser.Parse("mugvn/farm/pi4-1/env",
            Payload(o => o[field] = value), "mugvn/farm", "farm", DateTimeOffset.UtcNow));

    [Fact]
    public void UnavailableObservation_IsRetained() => Assert.Equal(1,
        TelemetryParser
            .Parse("mugvn/farm/pi4-1/env", Payload(o => o["missing_flag"] = 1), "mugvn/farm", "farm",
                DateTimeOffset.UtcNow).Measurements.MissingFlag);

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedJson_IsRejected(string payload) => Assert.Throws<InvalidTelemetryException>(() =>
        TelemetryParser.Parse("x", Encoding.UTF8.GetBytes(payload), "mugvn/farm", "farm", DateTimeOffset.UtcNow));

    [Fact]
    public void NonFiniteMeasurement_IsRejected() => Assert.Throws<InvalidTelemetryException>(() =>
        TelemetryParser.Parse("mugvn/farm/pi4-1/env",
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Payload()).Replace("39.4785", "1e999")), "mugvn/farm",
            "farm", DateTimeOffset.UtcNow));

    [Fact]
    public void Availability_RequiresMatchingDeviceAndKnownStatus()
    {
        var valid = Encoding.UTF8.GetBytes("""{"device_id":"pi4-1","status":"offline"}""");
        Assert.Equal(("pi4-1", "offline"),
            TelemetryParser.ParseAvailability("mugvn/farm/pi4-1/avail", valid, "mugvn/farm"));
        Assert.Throws<InvalidTelemetryException>(() =>
            TelemetryParser.ParseAvailability("mugvn/farm/pi4-2/avail", valid, "mugvn/farm"));
        Assert.Throws<InvalidTelemetryException>(() => TelemetryParser.ParseAvailability("mugvn/farm/pi4-1/avail",
            Encoding.UTF8.GetBytes("""{"device_id":"pi4-1","status":"broken"}"""), "mugvn/farm"));
    }
}