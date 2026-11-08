using SmartFarm.Ingestion;
using SmartFarm.Infrastructure;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddFarmInfrastructure(builder.Configuration);
builder.Services.AddOptions<MqttOptions>().Bind(builder.Configuration.GetSection("Mqtt")).Validate(o => !string.IsNullOrWhiteSpace(o.Host) && o.Port is >= 1 and <= 65535 &&
                   !string.IsNullOrWhiteSpace(o.TopicPrefix) && !string.IsNullOrWhiteSpace(o.ClientId) &&
                   o.SessionExpirySeconds is >= 1 and <= 604800, "Valid MQTT host, port, topic, client ID, and session expiry are required")
    .Validate(o => !o.TopicPrefix.Contains('+') && !o.TopicPrefix.Contains('#') && !o.TopicPrefix.EndsWith('/'),
        "Invalid MQTT topic prefix").ValidateOnStart();
builder.Services.AddSingleton<IngestionProcessor>();
builder.Services.AddHostedService<MqttWorker>();
await builder.Build().RunAsync();