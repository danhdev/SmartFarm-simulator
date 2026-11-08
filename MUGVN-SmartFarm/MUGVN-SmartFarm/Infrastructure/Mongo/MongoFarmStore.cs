using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace SmartFarm.Infrastructure.Mongo;

public sealed class MongoFarmStore : IDeviceStore, IDeviceStateStore, ITelemetryWriter, ITelemetryQueries, IStorageLifecycle
{
    private readonly IMongoDatabase database;
    private readonly IMongoCollection<Reading> readings;
    private readonly IMongoCollection<Device> devices;
    private readonly IMongoCollection<DeviceState> states;
    private readonly MongoOptions mongo;
    private readonly string farm;

    public MongoFarmStore(IMongoClient client, IOptions<MongoOptions> mongoOptions, IOptions<FarmOptions> farmOptions)
    {
        MongoMappings.EnsureRegistered();
        mongo = mongoOptions.Value;
        farm = farmOptions.Value.Id;
        database = client.GetDatabase(mongo.Database);
        readings = database.GetCollection<Reading>(mongo.Collection);
        devices = database.GetCollection<Device>("devices");
        states = database.GetCollection<DeviceState>("device_states");
    }

    public async Task InitializeAsync(bool seedDevices, CancellationToken ct)
    {
        using var cursor = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", mongo.Collection) }, ct);
        var info = await cursor.FirstOrDefaultAsync(ct);
        if (info is null)
        {
            try
            {
                await database.CreateCollectionAsync(mongo.Collection,
                    new CreateCollectionOptions
                    {
                        TimeSeriesOptions = new TimeSeriesOptions("timeStamp", "sensor", TimeSeriesGranularity.Minutes)
                    }, ct);
            }
            catch (MongoCommandException e) when (e.Code == 48)
            {
            } // Concurrent API/worker startup.
        }
        else if (info.GetValue("type", "") != "timeseries" ||
                 info["options"]["timeseries"]["timeField"] != "timeStamp" ||
                 info["options"]["timeseries"]["metaField"] != "sensor")
            throw new InvalidOperationException(
                "Telemetry collection must be Time Series with timeStamp/sensor; existing data was not modified");

        await readings.Indexes.CreateManyAsync([
            new CreateIndexModel<Reading>(Builders<Reading>.IndexKeys.Ascending("sensor.farm_id")
                .Ascending("sensor.device_id").Descending(x => x.SourceTimestamp).Descending(x => x.EmittedAt)
                .Descending(x => x.MessageId)),
            new CreateIndexModel<Reading>(Builders<Reading>.IndexKeys.Ascending("sensor.device_id")
                .Ascending(x => x.MessageId)),
            new CreateIndexModel<Reading>(Builders<Reading>.IndexKeys.Ascending("sensor.farm_id")
                .Ascending("sensor.device_id").Ascending(x => x.IngestedAt).Ascending(x => x.MessageId))
        ], ct);
        await devices.Indexes.CreateOneAsync(
            new CreateIndexModel<Device>(Builders<Device>.IndexKeys.Ascending(x => x.FarmId).Ascending(x => x.NodeId),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        if (seedDevices)
        {
            int[] nodes = [6, 18, 24, 38];
            for (var i = 0; i < nodes.Length; i++)
            {
                var device = new Device
                    { DeviceId = $"pi4-{i + 1}", FarmId = farm, NodeId = nodes[i], Label = $"Raspberry Pi {i + 1}" };
                var update = new BsonDocument("$setOnInsert", device.ToBsonDocument());
                await devices.UpdateOneAsync(x => x.DeviceId == device.DeviceId, update,
                    new UpdateOptions { IsUpsert = true }, ct);
            }
        }
    }

    public async Task PingAsync(CancellationToken ct) =>
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);

    public async Task<IReadOnlyList<Device>> DevicesAsync(CancellationToken ct) =>
        await devices.Find(x => x.FarmId == farm).SortBy(x => x.DeviceId).ToListAsync(ct);

    public Task<Device?> DeviceAsync(string id, CancellationToken ct) =>
        devices.Find(x => x.FarmId == farm && x.DeviceId == id).FirstOrDefaultAsync(ct)!;

    public async Task CreateDeviceAsync(Device device, CancellationToken ct)
    {
        try
        {
            await devices.InsertOneAsync(device, cancellationToken: ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new FarmException(FarmErrorCode.Conflict, "Device identity or source node is already registered");
        }
    }

    public async Task SaveDeviceAsync(Device device, CancellationToken ct) =>
        await devices.ReplaceOneAsync(x => x.DeviceId == device.DeviceId && x.FarmId == farm, device,
            cancellationToken: ct);

    public async Task<IReadOnlyList<DeviceState>> StatesAsync(CancellationToken ct) =>
        await states.Find(FilterDefinition<DeviceState>.Empty).ToListAsync(ct);

    private FilterDefinition<Reading> DeviceFilter(string id) => Builders<Reading>.Filter.Eq("sensor.farm_id", farm) &
                                                                 Builders<Reading>.Filter.Eq("sensor.device_id", id);

    public Task<Reading?> FindReadingAsync(string deviceId, string messageId, CancellationToken ct) => readings
        .Find(DeviceFilter(deviceId) & Builders<Reading>.Filter.Eq(x => x.MessageId, messageId))
        .FirstOrDefaultAsync(ct)!;

    public Task InsertAsync(Reading reading, CancellationToken ct) =>
        readings.InsertOneAsync(reading, cancellationToken: ct);

    public Task<DeviceState> UpdateTelemetryStateAsync(Reading reading, CancellationToken ct) =>
        states.FindOneAndUpdateAsync(x => x.DeviceId == reading.Sensor.DeviceId,
            Builders<DeviceState>.Update.Max(x => x.LastReceivedAt, reading.IngestedAt)
                .SetOnInsert(x => x.Availability, "unknown"),
            new FindOneAndUpdateOptions<DeviceState> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);

    public Task<DeviceState> UpdateAvailabilityAsync(string id, string status, DateTime now, CancellationToken ct) =>
        states.FindOneAndUpdateAsync(x => x.DeviceId == id,
            Builders<DeviceState>.Update.Set(x => x.Availability, status).Set(x => x.AvailabilityObservedAt, now),
            new FindOneAndUpdateOptions<DeviceState> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);

    public async Task<IReadOnlyList<TelemetryDto>> LatestAsync(string[] deviceIds, CancellationToken ct)
    {
        var match = new BsonDocument("sensor.farm_id", farm);
        if (deviceIds.Length > 0) match.Add("sensor.device_id", new BsonDocument("$in", new BsonArray(deviceIds)));
        BsonDocument[] pipeline =
        [
            new("$match", match),
            new("$sort", new BsonDocument { { "timeStamp", -1 }, { "emitted_at", -1 }, { "message_id", -1 } }),
            new("$group",
                new BsonDocument
                    { { "_id", "$sensor.device_id" }, { "reading", new BsonDocument("$first", "$$ROOT") } }),
            new("$replaceRoot", new BsonDocument("newRoot", "$reading"))
        ];
        var result = await readings.Aggregate<Reading>(pipeline).ToListAsync(ct);
        return result.Select(x => x.ToDto()).OrderBy(x => x.DeviceId, StringComparer.Ordinal).ToList();
    }

    public async Task<HistoryPage> HistoryAsync(HistoryQuery query, CancellationToken ct)
    {
        query.Validate();
        var cursor = CursorCodec.Decode(query);
        var f = Builders<Reading>.Filter;
        var filter = DeviceFilter(query.DeviceId) & f.Gte(x => x.SourceTimestamp, query.From.UtcDateTime) &
                     f.Lt(x => x.SourceTimestamp, query.To.UtcDateTime);
        if (cursor is not null)
            filter &= f.Gt(x => x.SourceTimestamp, cursor.Source) | (f.Eq(x => x.SourceTimestamp, cursor.Source) &
                                                                     f.Gt(x => x.EmittedAt, cursor.Emitted)) |
                      (f.Eq(x => x.SourceTimestamp, cursor.Source) & f.Eq(x => x.EmittedAt, cursor.Emitted) &
                       f.Gt(x => x.MessageId, cursor.MessageId));
        var rows = await readings.Find(filter).SortBy(x => x.SourceTimestamp).ThenBy(x => x.EmittedAt)
            .ThenBy(x => x.MessageId).Limit(query.Limit + 1).ToListAsync(ct);
        var more = rows.Count > query.Limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows.Select(x => x.ToDto()).ToList(), more ? CursorCodec.Encode(query, rows[^1]) : null);
    }

    public async Task<HistoryPage> ReceivedHistoryAsync(ReceivedHistoryQuery query, CancellationToken ct)
    {
        query.Validate();
        var cursor = ReceivedHistoryCursorCodec.Decode(query);
        var f = Builders<Reading>.Filter;
        var filter = DeviceFilter(query.DeviceId) & f.Gte(x => x.IngestedAt, query.From.UtcDateTime) &
                     f.Lt(x => x.IngestedAt, query.To.UtcDateTime);
        if (cursor is not null)
            filter &= f.Gt(x => x.IngestedAt, cursor.Ingested) |
                      (f.Eq(x => x.IngestedAt, cursor.Ingested) & f.Gt(x => x.MessageId, cursor.MessageId));
        var rows = await readings.Find(filter).SortBy(x => x.IngestedAt).ThenBy(x => x.MessageId)
            .Limit(query.Limit + 1).ToListAsync(ct);
        var more = rows.Count > query.Limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(rows.Select(x => x.ToDto()).ToList(),
            more ? ReceivedHistoryCursorCodec.Encode(query, rows[^1]) : null);
    }

    public async Task<IReadOnlyList<AggregateDto>> AggregatesAsync(string id, DateTimeOffset from, DateTimeOffset to,
        string interval, CancellationToken ct)
    {
        new HistoryQuery(id, from, to).Validate();
        if (interval is not ("hour" or "day")) throw new FarmException(FarmErrorCode.InvalidInput, "interval must be hour or day");
        var valid = new BsonDocument("$eq", new BsonArray { "$measurements.MissingFlag", 0 });
        var group = new BsonDocument
        {
            {
                "_id",
                new BsonDocument("$dateTrunc",
                    new BsonDocument { { "date", "$timeStamp" }, { "unit", interval }, { "timezone", "UTC" } })
            },
            { "valid", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { valid, 1, 0 })) },
            { "missing", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { valid, 0, 1 })) }
        };
        string[] fields = ["AirTemperature", "AirHumidity", "SoilTemperature", "SoilHumidity", "Ph", "Ec", "Light"];
        foreach (var field in fields)
            group.Add(field,
                new BsonDocument("$avg",
                    new BsonDocument("$cond", new BsonArray { valid, "$measurements." + field, BsonNull.Value })));
        BsonDocument[] pipeline =
        [
            new("$match",
                new BsonDocument
                {
                    { "sensor.farm_id", farm }, { "sensor.device_id", id },
                    {
                        "timeStamp",
                        new BsonDocument
                        {
                            { "$gte", new BsonDateTime(from.UtcDateTime) }, { "$lt", new BsonDateTime(to.UtcDateTime) }
                        }
                    }
                }),
            new("$group", group), new("$sort", new BsonDocument("_id", 1))
        ];
        var rows = await readings.Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
        return rows.Select(r => new AggregateDto(new DateTimeOffset(r["_id"].ToUniversalTime()), r["valid"].ToInt64(),
            r["missing"].ToInt64(),
            Value(r, fields[0]), Value(r, fields[1]), Value(r, fields[2]), Value(r, fields[3]), Value(r, fields[4]),
            Value(r, fields[5]), Value(r, fields[6]))).ToList();
    }

    private static double? Value(BsonDocument r, string key) => r[key].IsBsonNull ? null : r[key].ToDouble();
}
