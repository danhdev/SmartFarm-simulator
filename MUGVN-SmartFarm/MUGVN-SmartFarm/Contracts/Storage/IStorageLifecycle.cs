namespace SmartFarm.Contracts.Storage;
public interface IStorageLifecycle
{
    Task InitializeAsync(bool seedDevices, CancellationToken ct);
    Task PingAsync(CancellationToken ct);
}
