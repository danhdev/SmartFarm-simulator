using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace SmartFarm.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddFarmInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FarmOptions>().Bind(configuration.GetSection("Farm"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Id), "Farm ID is required").ValidateOnStart();
        services.AddOptions<MongoOptions>().Bind(configuration.GetSection("Mongo"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString) &&
                           !string.IsNullOrWhiteSpace(o.Database) && !string.IsNullOrWhiteSpace(o.Collection),
                "Mongo connection string, database, and collection are required")
            .ValidateOnStart();
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection("Redis"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString) &&
                           !string.IsNullOrWhiteSpace(o.ChannelPrefix) && o.PublishTimeoutMs is >= 100 and <= 10000,
                "Redis connection string, channel prefix, and valid publish timeout are required")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(
                sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            settings.WriteConcern = WriteConcern.WMajority;
            return new MongoClient(settings);
        });
        services.AddSingleton<MongoFarmStore>();
        services.AddSingleton<IDeviceStore>(sp => sp.GetRequiredService<MongoFarmStore>());
        services.AddSingleton<IDeviceStateStore>(sp => sp.GetRequiredService<MongoFarmStore>());
        services.AddSingleton<ITelemetryWriter>(sp => sp.GetRequiredService<MongoFarmStore>());
        services.AddSingleton<ITelemetryQueries>(sp => sp.GetRequiredService<MongoFarmStore>());
        services.AddSingleton<IStorageLifecycle>(sp => sp.GetRequiredService<MongoFarmStore>());
        services.AddSingleton<RedisBus>();
        services.AddSingleton<INotifier>(sp => sp.GetRequiredService<RedisBus>());
        return services;
    }
}
