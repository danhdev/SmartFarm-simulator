namespace SmartFarm.Contracts.History;
public sealed record HistoryPage(IReadOnlyList<TelemetryDto> Items, string? NextCursor);
public sealed record AggregateDto(
    DateTimeOffset Bucket, long ValidCount, long MissingCount, double? AirTemperature,
    double? AirHumidity, double? SoilTemperature, double? SoilHumidity, double? Ph, double? Ec, double? Light);
