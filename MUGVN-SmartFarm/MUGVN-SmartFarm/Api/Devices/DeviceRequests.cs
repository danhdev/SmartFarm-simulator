namespace MUGVN_SmartFarm.Api;

/// <summary>Device registration input; source identity is immutable after creation.</summary>
public sealed record CreateDeviceRequest(string DeviceId, int NodeId, string Label, int ReportingIntervalSeconds = 300);

/// <summary>Editable device metadata; omitted properties retain their values.</summary>
public sealed record PatchDeviceRequest(
    string? Label = null,
    int? ReportingIntervalSeconds = null,
    bool? Enabled = null);

