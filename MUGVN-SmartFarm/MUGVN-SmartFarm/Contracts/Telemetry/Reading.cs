namespace SmartFarm.Contracts.Telemetry;
public sealed record Measurements(
    double AirTemperature, double AirHumidity, double SoilTemperature, double SoilHumidity,
    double Ph, double Ec, double Light, int MissingFlag, string AnomalyLabel, int ViolatedVariableCount);

public sealed record SensorMetadata(string FarmId, string DeviceId, int NodeId);

public sealed record Reading
{
    public required DateTime SourceTimestamp { get; init; }
    public required SensorMetadata Sensor { get; init; }
    public required string MessageId { get; init; }
    public required DateTime EmittedAt { get; init; }
    public required DateTime IngestedAt { get; init; }
    public required Measurements Measurements { get; init; }

    public TelemetryDto ToDto() => new(MessageId, Sensor.DeviceId, Sensor.NodeId, Sensor.FarmId,
        new(SourceTimestamp, TimeSpan.Zero), new(EmittedAt, TimeSpan.Zero), new(IngestedAt, TimeSpan.Zero),
        Measurements);
}

public sealed record TelemetryDto(
    string MessageId, string DeviceId, int NodeId, string FarmId, DateTimeOffset SourceTimestamp,
    DateTimeOffset EmittedAt, DateTimeOffset IngestedAt, Measurements Measurements);

public sealed record DeviceStateEvent(
    string FarmId, string DeviceId, string Availability,
    DateTimeOffset? AvailabilityObservedAt, DateTimeOffset? LastReceivedAt);
