using System.Text.Json;
using Datadog.Trace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace SmartFarm.Infrastructure.Redis;

public sealed class RedisBus(IOptions<RedisOptions> options, ILogger<RedisBus> logger) : INotifier, IAsyncDisposable
{
    private readonly object sync = new();
    private Task<ConnectionMultiplexer>? connecting;
    private ConnectionMultiplexer? connection;
    public bool IsConnected => connection?.IsConnected == true;

    public Task<ConnectionMultiplexer> ConnectionAsync(CancellationToken ct)
    {
        Task<ConnectionMultiplexer> task;
        lock (sync)
        {
            if (connecting is null || connecting.IsFaulted || connecting.IsCanceled) connecting = ConnectAsync();
            task = connecting;
        }

        // A caller timeout must not abandon a multiplexer which is still connecting.
        return task.WaitAsync(ct);
    }

    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var config = ConfigurationOptions.Parse(options.Value.ConnectionString);
        config.AbortOnConnectFail = false;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        config.ConnectRetry = 0;
        var connected = await ConnectionMultiplexer.ConnectAsync(config);
        connected.ConnectionFailed +=
            (_, e) => logger.LogWarning("Redis connection lost: {FailureType}", e.FailureType);
        connected.ConnectionRestored += (_, _) => logger.LogInformation("Redis connection restored");
        connection = connected;
        return connected;
    }

    public Task PublishTelemetryAsync(TelemetryDto reading, CancellationToken ct) =>
        PublishAsync(options.Value.TelemetryChannel, reading, ct);

    public Task PublishStateAsync(DeviceStateEvent state, CancellationToken ct) =>
        PublishAsync(options.Value.StateChannel, state, ct);

    private async Task PublishAsync<T>(string channel, T value, CancellationToken ct)
    {
        using var scope = Tracer.Instance.StartActive("redis.notification.publish");
        scope.Span.ResourceName = channel;
        scope.Span.SetTag("span.kind", "producer");
        scope.Span.SetTag("messaging.system", "redis");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.PublishTimeoutMs);
            var c = await ConnectionAsync(timeout.Token);
            if (!c.IsConnected) throw new InvalidOperationException("Redis disconnected");
            await c.GetSubscriber().PublishAsync(RedisChannel.Literal(channel),
                RedisTraceMessage.Serialize(value), CommandFlags.None).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            scope.Span.SetTag("outcome", "cancelled");
            throw;
        }
        catch (Exception error)
        {
            scope.Span.SetException(error);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<ConnectionMultiplexer>? task;
        lock (sync) task = connecting;
        if (task is null) return;
        try
        {
            var c = await task;
            await c.DisposeAsync();
        }
        catch (Exception e)
        {
            logger.LogDebug("Redis disposal: {Failure}", e.GetType().Name);
        }
    }
}
