using System.Security.Claims;
using System.Text.Json;

namespace MUGVN_SmartFarm.Api;

public static class FarmAuthorization
{
    public static bool HasRole(ClaimsPrincipal user, string role)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        foreach (var claim in user.FindAll("realm_access"))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("roles", out var roles) &&
                    roles.ValueKind == JsonValueKind.Array && roles.EnumerateArray()
                        .Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == role)) return true;
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }
}