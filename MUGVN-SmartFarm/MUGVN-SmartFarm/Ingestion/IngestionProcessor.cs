using Microsoft.Extensions.Logging;
using Datadog.Trace;
using Microsoft.Extensions.Options;

namespace SmartFarm.Ingestion;

public sealed class IngestionProcessor(
    IDeviceStore devices,
    IDeviceStateStore states,
    ITelemetryWriter readings,
    INotifier notifier,
    IOptions<FarmOptions> farm,
    IOptions<MqttOptions> mqtt,
    ILogger<IngestionProcessor> logger)
{
    public async Task<string> ProcessAsync(string topic, ReadOnlyMemory<byte> payload, DateTimeOffset receivedAt,
        CancellationToken ct)
    {
        if (topic.EndsWith("/avail", StringComparison.Ordinal))
        {
            var (id, status) = TelemetryParser.ParseAvailability(topic, payload, mqtt.Value.TopicPrefix);
            Tracer.Instance.ActiveScope?.Span.SetTag("device.id", id);
            await RegisteredAsync(id, null, ct);
            var state = await states.UpdateAvailabilityAsync(id, status, receivedAt.UtcDateTime, ct);
            await NotifyAsync(() => notifier.PublishStateAsync(ToEvent(state), ct), ct);
            return "availability";
        }

        var reading = TelemetryParser.Parse(topic, payload, mqtt.Value.TopicPrefix, farm.Value.Id, receivedAt);
        var span = Tracer.Instance.ActiveScope?.Span;
        span?.SetTag("device.id", reading.Sensor.DeviceId);
        span?.SetTag("node.id", reading.Sensor.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        span?.SetTag("message.id", reading.MessageId);
        await RegisteredAsync(reading.Sensor.DeviceId, reading.Sensor.NodeId, ct);
        Reading? existing;
        using (var persistence = Tracer.Instance.StartActive("telemetry.persist"))
        {
            try
            {
                existing = await readings.FindReadingAsync(reading.Sensor.DeviceId, reading.MessageId, ct);
                if (existing is null) await readings.InsertAsync(reading, ct);
                else
                {
                    reading = existing;
                    logger.LogInformation("Duplicate telemetry {DeviceId} {MessageId}", reading.Sensor.DeviceId,
                        reading.MessageId);
                }

            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { persistence.Span.SetTag("outcome", "cancelled"); throw; }
            catch (Exception error) { persistence.Span.SetException(error); throw; }
        }
        var observed = await states.UpdateTelemetryStateAsync(reading, ct);
        await NotifyAsync(() => notifier.PublishTelemetryAsync(reading.ToDto(), ct), ct);
        await NotifyAsync(() => notifier.PublishStateAsync(ToEvent(observed), ct), ct);
        logger.LogInformation("Telemetry persisted {DeviceId} {MessageId} {SourceTimestamp} {IngestedAt}",
            reading.Sensor.DeviceId, reading.MessageId, reading.SourceTimestamp, reading.IngestedAt);
        return existing is null ? "inserted" : "duplicate";
    }

    private async Task RegisteredAsync(string id, int? node, CancellationToken ct)
    {
        using var scope = Tracer.Instance.StartActive("telemetry.validate-source");
        var device = await devices.DeviceAsync(id, ct);
        if (device is null || !device.Enabled || device.Decommissioned ||
            (node.HasValue && device.NodeId != node.Value))
            throw new InvalidTelemetryException("Unknown, disabled, decommissioned, or mismatched source device");
    }

    private DeviceStateEvent ToEvent(DeviceState s) => new(farm.Value.Id, s.DeviceId, s.Availability,
        s.AvailabilityObservedAt is { } a ? new DateTimeOffset(a) : null,
        s.LastReceivedAt is { } r ? new DateTimeOffset(r) : null);

    private async Task NotifyAsync(Func<Task> publish, CancellationToken ct)
    {
        try
        {
            await publish().WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Live notification unavailable: {Failure}", e.GetType().Name);
        }
    }
}