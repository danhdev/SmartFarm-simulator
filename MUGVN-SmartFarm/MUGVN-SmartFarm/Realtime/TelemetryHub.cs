using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace MUGVN_SmartFarm.Realtime;

[Authorize(Policy = "FarmAdmin")]
public sealed class TelemetryHub(IOptions<FarmOptions> farm) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "farm:" + farm.Value.Id);
        await base.OnConnectedAsync();
    }
}