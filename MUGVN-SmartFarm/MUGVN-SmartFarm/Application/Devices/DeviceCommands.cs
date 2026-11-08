namespace MUGVN_SmartFarm.Application.Devices;

public sealed record CreateDeviceCommand(string DeviceId, int NodeId, string Label, int ReportingIntervalSeconds = 300);
public sealed record UpdateDeviceCommand(string? Label = null, int? ReportingIntervalSeconds = null, bool? Enabled = null);
