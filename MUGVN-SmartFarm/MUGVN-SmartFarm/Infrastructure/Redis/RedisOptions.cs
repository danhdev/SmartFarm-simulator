using System.ComponentModel.DataAnnotations;
namespace SmartFarm.Infrastructure.Redis;
public sealed class RedisOptions
{
    [Required] public string ConnectionString { get; set; } = "";
    [Required] public string ChannelPrefix { get; set; } = "smartfarm";
    [Range(100, 10000)] public int PublishTimeoutMs { get; set; } = 1500;
    public string TelemetryChannel => $"{ChannelPrefix}:telemetry:persisted:v1";
    public string StateChannel => $"{ChannelPrefix}:device-state:changed:v1";
}
