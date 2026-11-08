using SmartFarm.Contracts;
using SmartFarm.Contracts.Devices;
using SmartFarm.Contracts.History;
using SmartFarm.Contracts.Telemetry;
using MUGVN_SmartFarm.Application.Devices;

namespace MUGVN_SmartFarm.Application.Telemetry;

public sealed class TelemetryQueryService(ITelemetryQueries queries, IDeviceStore deviceStore)
{
    private async Task<Device> RequireAsync(string id, CancellationToken ct) =>
        await deviceStore.DeviceAsync(id, ct) ?? throw new FarmException(FarmErrorCode.NotFound, "Device not found");

    public async Task<IReadOnlyList<TelemetryDto>> LatestAsync(string? ids, CancellationToken ct)
    {
        var deviceIds = ids?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (deviceIds.Length > 100) throw new FarmException(FarmErrorCode.InvalidInput, "At most 100 device identities are allowed");
        if (deviceIds.Length == 0)
            deviceIds = (await deviceStore.DevicesAsync(ct)).Where(d => !d.Decommissioned).Select(d => d.DeviceId).ToArray();
        foreach (var id in deviceIds) await RequireAsync(id, ct);
        return deviceIds.Length == 0 ? [] : await queries.LatestAsync(deviceIds, ct);
    }

    public async Task<HistoryPage> HistoryAsync(HistoryQuery query, CancellationToken ct)
    {
        query.Validate();
        await RequireAsync(query.DeviceId, ct);
        return await queries.HistoryAsync(query, ct);
    }

    public async Task<HistoryPage> ReceivedHistoryAsync(ReceivedHistoryQuery query, CancellationToken ct)
    {
        query.Validate();
        await RequireAsync(query.DeviceId, ct);
        return await queries.ReceivedHistoryAsync(query, ct);
    }

    public async Task<IReadOnlyList<AggregateDto>> AggregatesAsync(string id, DateTimeOffset from, DateTimeOffset to,
        string interval, CancellationToken ct)
    {
        await RequireAsync(id, ct);
        return await queries.AggregatesAsync(id, from, to, interval, ct);
    }
}
