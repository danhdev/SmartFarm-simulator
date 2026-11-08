using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace SmartFarm.Infrastructure.Mongo;

internal static class MongoMappings
{
    private static readonly object Sync = new();
    private static bool registered;

    public static void EnsureRegistered()
    {
        lock (Sync)
        {
            if (registered) return;
            BsonClassMap.RegisterClassMap<SensorMetadata>(map =>
            {
                map.AutoMap();
                map.MapMember(x => x.FarmId).SetElementName("farm_id");
                map.MapMember(x => x.DeviceId).SetElementName("device_id");
                map.MapMember(x => x.NodeId).SetElementName("node_id");
            });
            BsonClassMap.RegisterClassMap<Reading>(map =>
            {
                map.AutoMap();
                map.SetIgnoreExtraElements(true);
                map.MapMember(x => x.SourceTimestamp).SetElementName("timeStamp")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
                map.MapMember(x => x.Sensor).SetElementName("sensor");
                map.MapMember(x => x.MessageId).SetElementName("message_id");
                map.MapMember(x => x.EmittedAt).SetElementName("emitted_at")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
                map.MapMember(x => x.IngestedAt).SetElementName("ingested_at")
                    .SetSerializer(new DateTimeSerializer(DateTimeKind.Utc));
                map.MapMember(x => x.Measurements).SetElementName("measurements");
            });
            BsonClassMap.RegisterClassMap<Device>(map =>
            {
                map.AutoMap();
                map.SetIgnoreExtraElements(true);
                map.MapIdMember(x => x.DeviceId);
            });
            BsonClassMap.RegisterClassMap<DeviceState>(map =>
            {
                map.AutoMap();
                map.SetIgnoreExtraElements(true);
                map.MapIdMember(x => x.DeviceId);
            });
            registered = true;
        }
    }
}
