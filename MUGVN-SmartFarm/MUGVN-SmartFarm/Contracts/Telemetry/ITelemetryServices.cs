namespace SmartFarm.Contracts.Telemetry;
public interface ITelemetryWriter
{
    Task<Reading?> FindReadingAsync(string deviceId, string messageId, CancellationToken ct);
    Task InsertAsync(Reading reading, CancellationToken ct);
}

public interface ITelemetryQueries
{
    Task<IReadOnlyList<TelemetryDto>> LatestAsync(string[] deviceIds, CancellationToken ct);
    Task<HistoryPage> HistoryAsync(HistoryQuery query, CancellationToken ct);
    Task<HistoryPage> ReceivedHistoryAsync(ReceivedHistoryQuery query, CancellationToken ct);
    Task<IReadOnlyList<AggregateDto>> AggregatesAsync(string id, DateTimeOffset from, DateTimeOffset to,
        string interval, CancellationToken ct);
}

public interface INotifier
{
    Task PublishTelemetryAsync(TelemetryDto reading, CancellationToken ct);
    Task PublishStateAsync(DeviceStateEvent state, CancellationToken ct);
}
