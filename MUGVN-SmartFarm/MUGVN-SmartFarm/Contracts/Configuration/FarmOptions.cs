using System.ComponentModel.DataAnnotations;
namespace SmartFarm.Contracts.Configuration;
public sealed class FarmOptions
{
    [Required] public string Id { get; set; } = "farm-001";
    public bool SeedDevices { get; set; } = true;
}
