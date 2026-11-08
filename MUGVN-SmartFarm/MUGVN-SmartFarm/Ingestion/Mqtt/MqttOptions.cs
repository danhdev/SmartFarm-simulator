using System.ComponentModel.DataAnnotations;
namespace SmartFarm.Ingestion.Mqtt;
public sealed class MqttOptions
{
    [Required] public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 1883;
    [Required] public string TopicPrefix { get; set; } = "mugvn/farm";
    [Required] public string ClientId { get; set; } = "smartfarm-ingestion";
    [Range(1, 604800)] public uint SessionExpirySeconds { get; set; } = 86400;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
