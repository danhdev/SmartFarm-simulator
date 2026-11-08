using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace SmartFarm.Tests;

public sealed class MongoIntegrationFactAttribute : FactAttribute
{
    public MongoIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTFARM_TEST_MONGO_URI")))
            Skip = "Set SMARTFARM_TEST_MONGO_URI to explicitly enable isolated MongoDB integration tests";
    }
}

public sealed class MongoIntegrationTests
{
    [MongoIntegrationFact]
    public async Task ReceivedHistory_FiltersFarmDeviceAndTimeAndPaginatesTies()
    {
        var database = "smartfarm_test_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(Environment.GetEnvironmentVariable("SMARTFARM_TEST_MONGO_URI"));
        var store = new MongoFarmStore(client, Options.Create(new MongoOptions { Database = database }),
            Options.Create(new FarmOptions()));
        try
        {
            await store.InitializeAsync(true, default);
            var from = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
            var to = from.AddHours(1);
            var sample = TelemetryParser.Parse("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(),
                "mugvn/farm", "farm-001", from);
            Reading At(int id, DateTimeOffset time) => sample with
            {
                MessageId = $"00000000-0000-4000-8000-{id:000000000000}", IngestedAt = time.UtcDateTime
            };
            await store.InsertAsync(At(2, from.AddMinutes(10)), default);
            await store.InsertAsync(At(1, from), default);
            await store.InsertAsync(At(3, from.AddMinutes(10)), default);
            await store.InsertAsync(At(4, to), default);
            await store.InsertAsync(At(5, from.AddMinutes(-1)), default);
            await store.InsertAsync(At(6, from.AddMinutes(5)) with
                { Sensor = sample.Sensor with { DeviceId = "pi4-2", NodeId = 18 } }, default);
            await store.InsertAsync(At(7, from.AddMinutes(5)) with
                { Sensor = sample.Sensor with { FarmId = "other-farm" } }, default);

            var first = await store.ReceivedHistoryAsync(new("pi4-1", from, to, 1), default);
            Assert.Equal(At(1, from).MessageId, Assert.Single(first.Items).MessageId);
            Assert.NotNull(first.NextCursor);
            var second = await store.ReceivedHistoryAsync(new("pi4-1", from, to, 1, first.NextCursor), default);
            Assert.Equal(At(2, from).MessageId, Assert.Single(second.Items).MessageId);
            var third = await store.ReceivedHistoryAsync(new("pi4-1", from, to, 1, second.NextCursor), default);
            Assert.Equal(At(3, from).MessageId, Assert.Single(third.Items).MessageId);
            Assert.Null(third.NextCursor);
            Assert.Empty((await store.ReceivedHistoryAsync(new("pi4-2", from.AddMinutes(10), to), default)).Items);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    [MongoIntegrationFact]
    public async Task MongoStorage_MappingCrudOrderingPaginationAndAggregation()
    {
        var database = "smartfarm_test_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(Environment.GetEnvironmentVariable("SMARTFARM_TEST_MONGO_URI"));
        var store = new MongoFarmStore(client, Options.Create(new MongoOptions { Database = database }),
            Options.Create(new FarmOptions()));
        try
        {
            await store.InitializeAsync(true, default);
            await store.InitializeAsync(true, default);
            Assert.Equal(4, (await store.DevicesAsync(default)).Count);
            var device = (await store.DeviceAsync("pi4-1", default))!;
            await store.SaveDeviceAsync(device with { Label = "Edited" }, default);
            await store.InitializeAsync(true, default);
            Assert.Equal("Edited", (await store.DeviceAsync("pi4-1", default))!.Label);
            await Assert.ThrowsAsync<FarmException>(() => store.CreateDeviceAsync(device, default));
            var day = new DateTimeOffset(2025, 7, 24, 0, 0, 0, TimeSpan.Zero);
            var baseReading = TelemetryParser.Parse("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(),
                "mugvn/farm", "farm-001", DateTimeOffset.UtcNow);
            var a = baseReading with
            {
                MessageId = "00000000-0000-4000-8000-000000000001",
                Measurements = baseReading.Measurements with { AirTemperature = 20 }
            };
            var b = baseReading with
            {
                MessageId = "00000000-0000-4000-8000-000000000002", SourceTimestamp = day.AddMinutes(5).UtcDateTime,
                Measurements = baseReading.Measurements with { AirTemperature = 30 }
            };
            var missing = baseReading with
            {
                MessageId = "00000000-0000-4000-8000-000000000003",
                SourceTimestamp = day.AddMinutes(10).UtcDateTime,
                Measurements = baseReading.Measurements with { AirTemperature = 999, MissingFlag = 1 }
            };
            var boundary = baseReading with
            {
                MessageId = "00000000-0000-4000-8000-000000000004", SourceTimestamp = day.AddDays(1).UtcDateTime
            };
            await store.InsertAsync(b, default);
            await store.InsertAsync(a, default);
            await store.InsertAsync(missing, default);
            await store.InsertAsync(boundary, default);
            var raw = await client.GetDatabase(database).GetCollection<BsonDocument>("SmartFarm")
                .Find(new BsonDocument("message_id", a.MessageId)).FirstAsync();
            Assert.Equal(BsonType.DateTime, raw["timeStamp"].BsonType);
            Assert.Equal("pi4-1", raw["sensor"]["device_id"].AsString);
            Assert.Equal(6, raw["sensor"]["node_id"].AsInt32);
            Assert.Equal(a.IngestedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                raw["ingested_at"].ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff"));
            var page = await store.HistoryAsync(new("pi4-1", day, day.AddDays(1), 2), default);
            Assert.Equal(new[] { a.MessageId, b.MessageId }, page.Items.Select(x => x.MessageId));
            Assert.NotNull(page.NextCursor);
            var next = await store.HistoryAsync(new("pi4-1", day, day.AddDays(1), 2, page.NextCursor), default);
            Assert.Single(next.Items);
            Assert.Equal(missing.MessageId, next.Items[0].MessageId);
            Assert.Null(next.NextCursor);
            Assert.Empty((await store.HistoryAsync(new("pi4-2", day, day.AddDays(1)), default)).Items);
            var aggregate = Assert.Single(await store.AggregatesAsync("pi4-1", day, day.AddDays(1), "hour", default));
            Assert.Equal(2, aggregate.ValidCount);
            Assert.Equal(1, aggregate.MissingCount);
            Assert.Equal(25d, aggregate.AirTemperature);
            Assert.Equal(day, aggregate.Bucket);
            Assert.Equal(33.18, aggregate.AirHumidity);
            Assert.Equal(20.26, aggregate.SoilTemperature);
            Assert.Equal(33.08, aggregate.SoilHumidity);
            Assert.Equal(7.03, aggregate.Ph);
            Assert.Equal(0.30, aggregate.Ec);
            Assert.Equal(26512.8, aggregate.Light);
            var onlyMissing =
                Assert.Single(await store.AggregatesAsync("pi4-1", day.AddMinutes(10), day.AddMinutes(11), "day",
                    default));
            Assert.Null(onlyMissing.AirTemperature);
            Assert.Equal(0, onlyMissing.ValidCount);
            Assert.Equal(1, onlyMissing.MissingCount);
            await Assert.ThrowsAsync<FarmException>(() =>
                store.AggregatesAsync("pi4-1", day, day.AddDays(1), "minute", default));
            Assert.Equal(boundary.MessageId, Assert.Single(await store.LatestAsync(["pi4-1"], default)).MessageId);
            await store.UpdateTelemetryStateAsync(b, default);
            await store.UpdateTelemetryStateAsync(a with { IngestedAt = a.IngestedAt.AddHours(-1) }, default);
            Assert.Equal(new DateTimeOffset(b.IngestedAt).ToUnixTimeMilliseconds(),
                new DateTimeOffset(Assert.Single(await store.StatesAsync(default)).LastReceivedAt!.Value)
                    .ToUnixTimeMilliseconds());
            await store.SaveDeviceAsync(device with { Decommissioned = true, Enabled = false }, default);
            Assert.Equal(3, (await store.HistoryAsync(new("pi4-1", day, day.AddDays(1)), default)).Items.Count);
            var tie1 = boundary with
            {
                MessageId = "00000000-0000-4000-8000-000000000005", EmittedAt = boundary.EmittedAt.AddHours(1)
            };
            var tie2 = tie1 with { MessageId = "00000000-0000-4000-8000-000000000006" };
            await store.InsertAsync(tie2, default);
            await store.InsertAsync(tie1, default);
            Assert.Equal(tie2.MessageId, Assert.Single(await store.LatestAsync(["pi4-1"], default)).MessageId);
            var tiePage = await store.HistoryAsync(new("pi4-1", day.AddDays(1), day.AddDays(2), 1), default);
            Assert.Equal(boundary.MessageId, Assert.Single(tiePage.Items).MessageId);
            var tiePage2 = await store.HistoryAsync(new("pi4-1", day.AddDays(1), day.AddDays(2), 1, tiePage.NextCursor),
                default);
            Assert.Equal(tie1.MessageId, Assert.Single(tiePage2.Items).MessageId);
            var tiePage3 =
                await store.HistoryAsync(new("pi4-1", day.AddDays(1), day.AddDays(2), 1, tiePage2.NextCursor), default);
            Assert.Equal(tie2.MessageId, Assert.Single(tiePage3.Items).MessageId);
            Assert.Null(tiePage3.NextCursor);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    [MongoIntegrationFact]
    public async Task IncompatibleCollection_IsRejectedWithoutChangingItsData()
    {
        var database = "smartfarm_test_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(Environment.GetEnvironmentVariable("SMARTFARM_TEST_MONGO_URI"));
        try
        {
            await client.GetDatabase(database).GetCollection<BsonDocument>("SmartFarm")
                .InsertOneAsync(new BsonDocument("preserve", true));
            var store = new MongoFarmStore(client, Options.Create(new MongoOptions { Database = database }),
                Options.Create(new FarmOptions()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(false, default));
            Assert.Equal(1,
                await client.GetDatabase(database).GetCollection<BsonDocument>("SmartFarm")
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }
}
