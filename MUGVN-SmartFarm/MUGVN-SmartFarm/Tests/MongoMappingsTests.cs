using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Xunit;

namespace SmartFarm.Tests;

public sealed class MongoMappingsTests
{
    private static void RegisterMappings() =>
        _ = new MongoFarmStore(new MongoClient("mongodb://127.0.0.1:27018/?directConnection=true"),
            Options.Create(new MongoOptions()), Options.Create(new FarmOptions()));

    [Fact]
    public void Reading_UsesExistingTimeSeriesFieldNamesAndRoundTripsUtc()
    {
        RegisterMappings();
        var reading = TelemetryParser.Parse("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), "mugvn/farm",
            "farm-001", DateTimeOffset.Parse("2026-11-05T02:00:00Z"));
        var document = reading.ToBsonDocument();

        Assert.Equal(BsonType.DateTime, document["timeStamp"].BsonType);
        Assert.Equal("pi4-1", document["sensor"]["device_id"].AsString);
        Assert.Equal("farm-001", document["sensor"]["farm_id"].AsString);
        Assert.Equal(6, document["sensor"]["node_id"].AsInt32);
        Assert.Equal(reading.MessageId, document["message_id"].AsString);
        Assert.True(document.Contains("emitted_at"));
        Assert.True(document.Contains("ingested_at"));
        Assert.True(document.Contains("measurements"));
        document.Add("_id", ObjectId.GenerateNewId());

        var restored = BsonSerializer.Deserialize<Reading>(document);
        Assert.Equal(reading, restored);
        Assert.Equal(DateTimeKind.Utc, restored.SourceTimestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, restored.EmittedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, restored.IngestedAt.Kind);
    }

    [Fact]
    public void DeviceAndState_KeepDeviceIdAsMongoId()
    {
        RegisterMappings();
        var device = new Device { DeviceId = "pi4-1", FarmId = "farm-001", NodeId = 6, Label = "Pi" };
        var observed = new DateTime(2026, 11, 5, 2, 0, 0, DateTimeKind.Utc);
        var state = new DeviceState
            { DeviceId = "pi4-1", Availability = "online", AvailabilityObservedAt = observed, LastReceivedAt = observed };

        Assert.Equal("pi4-1", device.ToBsonDocument()["_id"].AsString);
        Assert.Equal("pi4-1", state.ToBsonDocument()["_id"].AsString);
        Assert.Equal(device, BsonSerializer.Deserialize<Device>(device.ToBsonDocument()));
        var restored = BsonSerializer.Deserialize<DeviceState>(state.ToBsonDocument());
        Assert.Equal(state, restored);
        Assert.Equal(DateTimeKind.Utc, restored.LastReceivedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, restored.AvailabilityObservedAt!.Value.Kind);
    }
}
