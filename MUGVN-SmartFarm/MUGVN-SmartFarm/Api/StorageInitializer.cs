using Microsoft.Extensions.Options;

namespace MUGVN_SmartFarm.Api;

public sealed class StorageInitializer(IStorageLifecycle storage, IOptions<FarmOptions> farm, ILogger<StorageInitializer> logger)
    : BackgroundService
{
    public bool Ready { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await storage.InitializeAsync(farm.Value.SeedDevices, ct);
                Ready = true;
                logger.LogInformation("Storage initialized");
                return;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Storage initialization unavailable: {Failure}", e.GetType().Name);
                await Task.Delay(3000, ct);
            }
        }
    }
}