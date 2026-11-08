using FastEndpoints;
using MUGVN_SmartFarm.Realtime;

namespace MUGVN_SmartFarm.Api;

public sealed class StatusEndpoint(IStorageLifecycle storage, RedisSubscriber redis, StorageInitializer initializer)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/status");
        Policies("FarmAdmin");
        Description(x => x.WithSummary("Storage and best-effort live delivery status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var database = true;
        try
        {
            await storage.PingAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            database = false;
        }

        await HttpContext.Response.WriteAsJsonAsync(
            new { storage_ready = initializer.Ready && database, live_available = redis.LiveAvailable }, ct);
    }
}