using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SmartFarm.Contracts;
using SmartFarm.Contracts.Configuration;
using SmartFarm.Contracts.Devices;
using SmartFarm.Contracts.History;

namespace MUGVN_SmartFarm.Application.Devices;

public sealed class DeviceService(IDeviceStore store, IDeviceStateStore statesStore, IOptions<FarmOptions> farm, TimeProvider clock)
{
    public async Task<Device> RequireAsync(string id, CancellationToken ct) =>
        await store.DeviceAsync(id, ct) ?? throw new FarmException(FarmErrorCode.NotFound, "Device not found");

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct)
    {
        var devices = await store.DevicesAsync(ct);
        var states = (await statesStore.StatesAsync(ct)).ToDictionary(x => x.DeviceId);
        return devices.Select(d => ToDto(d, states.GetValueOrDefault(d.DeviceId))).ToList();
    }

    public async Task<DeviceDto> GetAsync(string id, CancellationToken ct)
    {
        var d = await RequireAsync(id, ct);
        var state = (await statesStore.StatesAsync(ct)).FirstOrDefault(s => s.DeviceId == id);
        return ToDto(d, state);
    }

    public async Task<DeviceDto> CreateAsync(CreateDeviceCommand input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.DeviceId) || !Regex.IsMatch(input.DeviceId, @"^[a-zA-Z0-9_-]{1,64}$") ||
            input.NodeId <= 0)
            throw new FarmException(FarmErrorCode.InvalidInput, "Valid device_id and positive node_id are required");
        ValidateEditable(input.Label, input.ReportingIntervalSeconds);
        var d = new Device
        {
            DeviceId = input.DeviceId, NodeId = input.NodeId, FarmId = farm.Value.Id, Label = input.Label.Trim(),
            ReportingIntervalSeconds = input.ReportingIntervalSeconds
        };
        await store.CreateDeviceAsync(d, ct);
        return ToDto(d, null);
    }

    public async Task<DeviceDto> PatchAsync(string id, UpdateDeviceCommand input, CancellationToken ct)
    {
        var d = await RequireAsync(id, ct);
        if (d.Decommissioned) throw new FarmException(FarmErrorCode.Conflict, "Device is decommissioned");
        ValidateEditable(input.Label ?? d.Label, input.ReportingIntervalSeconds ?? d.ReportingIntervalSeconds);
        await store.SaveDeviceAsync(
            d with
            {
                Label = input.Label?.Trim() ?? d.Label,
                ReportingIntervalSeconds = input.ReportingIntervalSeconds ?? d.ReportingIntervalSeconds,
                Enabled = input.Enabled ?? d.Enabled
            }, ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var d = await RequireAsync(id, ct);
        await store.SaveDeviceAsync(d with { Enabled = false, Decommissioned = true }, ct);
    }

    private static void ValidateEditable(string label, int interval)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 120 || interval < 1 || interval > 86400)
            throw new FarmException(FarmErrorCode.InvalidInput, "Label is required (max 120 characters); interval must be 1–86400 seconds");
    }

    private DeviceDto ToDto(Device d, DeviceState? s) => new(d.DeviceId, d.FarmId, d.NodeId, d.Label,
        d.ReportingIntervalSeconds, d.Enabled, d.Decommissioned, s?.Availability ?? "unknown",
        s?.AvailabilityObservedAt is { } a ? new DateTimeOffset(a) : null,
        s?.LastReceivedAt is { } r ? new DateTimeOffset(r) : null,
        s?.LastReceivedAt is not { } last || clock.GetUtcNow() - new DateTimeOffset(last) >
        TimeSpan.FromSeconds(2L * d.ReportingIntervalSeconds));
}
