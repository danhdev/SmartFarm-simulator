namespace SmartFarm.Contracts.Devices;
public sealed record Device
{
    public required string DeviceId { get; init; }
    public required string FarmId { get; init; }
    public required int NodeId { get; init; }
    public required string Label { get; init; }
    public int ReportingIntervalSeconds { get; init; } = 300;
    public bool Enabled { get; init; } = true;
    public bool Decommissioned { get; init; }
}

public sealed record DeviceState
{
    public required string DeviceId { get; init; }
    public string Availability { get; init; } = "unknown";
    public DateTime? AvailabilityObservedAt { get; init; }
    public DateTime? LastReceivedAt { get; init; }
}

public sealed record DeviceDto(
    string DeviceId, string FarmId, int NodeId, string Label, int ReportingIntervalSeconds,
    bool Enabled, bool Decommissioned, string Availability, DateTimeOffset? AvailabilityObservedAt,
    DateTimeOffset? LastReceivedAt, bool IsStale);
