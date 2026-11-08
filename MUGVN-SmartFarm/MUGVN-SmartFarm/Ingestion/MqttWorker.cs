using System.Buffers;
using Datadog.Trace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace SmartFarm.Ingestion;

public sealed class MqttWorker(
    IngestionProcessor processor,
    IStorageLifecycle storage,
    IOptions<MqttOptions> options,
    TimeProvider clock,
    ILogger<MqttWorker> logger) : BackgroundService
{
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Min(60, Math.Pow(2, Math.Min(attempt, 6))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await storage.InitializeAsync(false, stoppingToken);
                break;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Ingestion storage initialization unavailable: {Failure}", e.GetType().Name);
                await Task.Delay(Backoff(attempt++), stoppingToken);
            }
        }

        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += async args =>
        {
            args.AutoAcknowledge = false;
            var receivedAt = clock.GetUtcNow();
            var payload = args.ApplicationMessage.Payload.ToArray();
            var retry = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var retryDelay = TimeSpan.Zero;
                using (var scope = Tracer.Instance.StartActive("mqtt.message.process",
                           new SpanCreationSettings { Parent = SpanContext.None }))
                {
                    scope.Span.ResourceName = args.ApplicationMessage.Topic.EndsWith("/avail", StringComparison.Ordinal)
                        ? "mqtt.availability" : "mqtt.telemetry";
                    scope.Span.SetTag("span.kind", "consumer");
                    scope.Span.SetTag("messaging.system", "mqtt");
                    scope.Span.SetTag("retry.number", retry.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    try
                    {
                        var outcome = await processor.ProcessAsync(args.ApplicationMessage.Topic, payload, receivedAt, stoppingToken);
                        scope.Span.SetTag("mqtt.outcome", outcome);
                        await args.AcknowledgeAsync(stoppingToken);
                        return;
                    }
                    catch (InvalidTelemetryException e)
                    {
                        scope.Span.SetTag("mqtt.outcome", "rejected");
                        logger.LogWarning("Rejected MQTT message {Topic}: {Reason}", args.ApplicationMessage.Topic,
                            e.Message);
                        await args.AcknowledgeAsync(stoppingToken);
                        return;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        scope.Span.SetTag("mqtt.outcome", "cancelled");
                        return;
                    }
                    catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                    {
                        scope.Span.SetTag("mqtt.outcome", "retry");
                        scope.Span.SetException(e);
                        logger.LogWarning("Ingestion retry {Topic}: {Failure}", args.ApplicationMessage.Topic,
                            e.GetType().Name);
                        retryDelay = Backoff(retry++);
                    }
                }
                await Task.Delay(retryDelay, stoppingToken);
            }
        };
        var config = options.Value;
        var builder = new MqttClientOptionsBuilder().WithTcpServer(config.Host, config.Port)
            .WithClientId(config.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V500).WithCleanSession(false)
            .WithSessionExpiryInterval(config.SessionExpirySeconds);
        if (!string.IsNullOrWhiteSpace(config.Username)) builder.WithCredentials(config.Username, config.Password);
        attempt = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!client.IsConnected)
                    {
                        await client.ConnectAsync(builder.Build(), stoppingToken);
                        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                                .WithTopicFilter(f =>
                                    f.WithTopic($"{config.TopicPrefix}/+/env")
                                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                                .WithTopicFilter(f =>
                                    f.WithTopic($"{config.TopicPrefix}/+/avail")
                                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build(),
                            stoppingToken);
                        logger.LogInformation("MQTT connected {Host}:{Port} {ClientId}", config.Host, config.Port,
                            config.ClientId);
                        attempt = 0;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("MQTT connection unavailable: {Failure}", e.GetType().Name);
                    await Task.Delay(Backoff(attempt++), stoppingToken);
                }
            }
        }
        finally
        {
            if (client.IsConnected)
                try
                {
                    await client.DisconnectAsync(new MqttClientDisconnectOptions());
                }
                catch (Exception e)
                {
                    logger.LogDebug("MQTT shutdown: {Failure}", e.GetType().Name);
                }
        }
    }
}
