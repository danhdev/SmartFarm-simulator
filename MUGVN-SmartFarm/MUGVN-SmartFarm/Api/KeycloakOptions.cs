using System.ComponentModel.DataAnnotations;
namespace MUGVN_SmartFarm.Api;
public sealed class KeycloakOptions
{
    [Required, Url] public string Authority { get; set; } = "";
    public string? MetadataAddress { get; set; }
    [Required] public string Audience { get; set; } = "smartfarm-api";
    [Required] public string BrowserClientId { get; set; } = "smartfarm-demo";
    [Required] public string AdminRole { get; set; } = "farm-admin";
}
