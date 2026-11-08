namespace SmartFarm.Contracts.Devices;
public interface IDeviceStore
{
    Task<IReadOnlyList<Device>> DevicesAsync(CancellationToken ct);
    Task<Device?> DeviceAsync(string id, CancellationToken ct);
    Task CreateDeviceAsync(Device device, CancellationToken ct);
    Task SaveDeviceAsync(Device device, CancellationToken ct);
}

public interface IDeviceStateStore
{
    Task<IReadOnlyList<DeviceState>> StatesAsync(CancellationToken ct);
    Task<DeviceState> UpdateTelemetryStateAsync(Reading reading, CancellationToken ct);
    Task<DeviceState> UpdateAvailabilityAsync(string id, string status, DateTime now, CancellationToken ct);
}
