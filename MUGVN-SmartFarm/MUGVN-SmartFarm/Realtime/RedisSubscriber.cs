using System.Text.Json;
using Datadog.Trace;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using SmartFarm.Infrastructure.Redis;

namespace MUGVN_SmartFarm.Realtime;

public sealed class RedisSubscriber(
    RedisBus bus,
    IOptions<RedisOptions> options,
    IOptions<FarmOptions> farm,
    IHubContext<TelemetryHub> hub,
    ILogger<RedisSubscriber> logger) : BackgroundService
{
    public bool LiveAvailable { get; private set; }

    private readonly Channel<(bool State, string Json)> pending =
        Channel.CreateBounded<(bool, string)>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var pump = PumpAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var connection = await bus.ConnectionAsync(ct);
                    var subscriber = connection.GetSubscriber();
                    await subscriber.SubscribeAsync(RedisChannel.Literal(options.Value.TelemetryChannel),
                        (_, value) => pending.Writer.TryWrite((false, value.ToString()))).WaitAsync(ct);
                    await subscriber.SubscribeAsync(RedisChannel.Literal(options.Value.StateChannel),
                        (_, value) => pending.Writer.TryWrite((true, value.ToString()))).WaitAsync(ct);
                    while (!ct.IsCancellationRequested)
                    {
                        var live = connection.IsConnected &&
                                   subscriber.IsConnected(RedisChannel.Literal(options.Value.TelemetryChannel));
                        if (live != LiveAvailable)
                        {
                            LiveAvailable = live;
                            await hub.Clients.Group("farm:" + farm.Value.Id)
                                .SendAsync("LiveStatus", new { available = live }, ct);
                            logger.LogInformation("Live notifications available: {LiveAvailable}", live);
                        }

                        await Task.Delay(1000, ct);
                    }
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    LiveAvailable = false;
                    logger.LogWarning("Redis subscription unavailable: {Failure}", e.GetType().Name);
                    await Task.Delay(2000, ct);
                }
            }
        }
        finally
        {
            pending.Writer.TryComplete();
            try
            {
                await pump;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }

    private async Task SendAsync<T>(string eventName, T value, CancellationToken ct)
    {
        using var scope = Tracer.Instance.StartActive("signalr.send");
        scope.Span.ResourceName = eventName;
        try { await hub.Clients.Group("farm:" + farm.Value.Id).SendAsync(eventName, value, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            scope.Span.SetTag("outcome", "cancelled");
            throw;
        }
        catch (Exception error) { scope.Span.SetException(error); throw; }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        await foreach (var message in pending.Reader.ReadAllAsync(ct))
        {
            IScope? scope = null;
            try
            {
                var notification = RedisTraceMessage.Deserialize<JsonElement>(message.Json);
                scope = RedisTraceMessage.StartConsumer(notification.Headers);
                scope.Span.ResourceName = message.State ? "device-state" : "telemetry";
                if (message.State)
                {
                    var state = notification.Value.Deserialize<DeviceStateEvent>(JsonDefaults.Options);
                    if (state is not null && state.FarmId == farm.Value.Id)
                        await SendAsync("DeviceStateChanged", state, ct);
                }
                else
                {
                    var reading = notification.Value.Deserialize<TelemetryDto>(JsonDefaults.Options);
                    if (reading is not null && reading.FarmId == farm.Value.Id)
                        await SendAsync("TelemetryPersisted", reading, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                scope?.Span.SetTag("outcome", "cancelled");
                throw;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                scope?.Span.SetException(e);
                logger.LogWarning("Live event rejected: {Failure}", e.GetType().Name);
            }
            finally { scope?.Dispose(); }
        }
    }
}