using System.ComponentModel.DataAnnotations;
namespace SmartFarm.Infrastructure.Mongo;
public sealed class MongoOptions
{
    [Required] public string ConnectionString { get; set; } = "";
    [Required] public string Database { get; set; } = "MUGVN-Hackathon";
    [Required] public string Collection { get; set; } = "SmartFarm";
}
