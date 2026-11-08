using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MUGVN_SmartFarm.Api;
using MUGVN_SmartFarm.Application.Devices;
using MUGVN_SmartFarm.Application.Telemetry;
using SmartFarm.Infrastructure;
using SmartFarm.Infrastructure.Redis;
using MUGVN_SmartFarm.Middleware;
using MUGVN_SmartFarm.Realtime;

namespace MUGVN_SmartFarm;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        builder.Services.AddFarmInfrastructure(builder.Configuration);
        builder.Services.AddOptions<KeycloakOptions>().Bind(builder.Configuration.GetSection("Keycloak"))
            .ValidateDataAnnotations().Validate(o => o.Authority.Contains("/realms/", StringComparison.Ordinal),
                "Authority must identify a Keycloak realm").ValidateOnStart();
        var keycloak = builder.Configuration.GetSection("Keycloak").Get<KeycloakOptions>() ?? new();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.Authority = keycloak.Authority;
            if (!string.IsNullOrWhiteSpace(keycloak.MetadataAddress))
                o.MetadataAddress = keycloak.MetadataAddress;
            o.Audience = keycloak.Audience;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "preferred_username"
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/hubs/telemetry") &&
                        context.Request.Query.TryGetValue("access_token", out var token)) context.Token = token;
                    return Task.CompletedTask;
                }
            };
        });
        builder.Services.AddAuthorization(o => o.AddPolicy("FarmAdmin",
            p => p.RequireAuthenticatedUser()
                .RequireAssertion(c => FarmAuthorization.HasRole(c.User, keycloak.AdminRole))));
        builder.Services.AddFastEndpoints();
        builder.Services.AddOpenApi(OpenApiConfiguration.Configure);
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddScoped<DeviceService>();
        builder.Services.AddScoped<TelemetryQueryService>();
        builder.Services.AddSingleton<StorageInitializer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StorageInitializer>());
        builder.Services.AddSingleton<RedisSubscriber>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RedisSubscriber>());
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages(async context =>
        {
            var response = context.HttpContext.Response;
            response.ContentType = "application/problem+json";
            await response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = response.StatusCode,
                    Title = response.StatusCode switch
                    {
                        401 => "Authentication required", 403 => "Access denied", 404 => "Not found",
                        _ => "Request failed"
                    }
                }, options: JsonDefaults.Options, contentType: "application/problem+json");
        });
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "MUGVN SmartFarm API v1"));
        }

        app.UseHttpsRedirection();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseFastEndpoints(c =>
        {
            c.Serializer.Options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            c.Errors.UseProblemDetails();
        });
        app.MapHub<TelemetryHub>("/hubs/telemetry", o => o.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization("FarmAdmin");
        app.MapGet("/demo/config.json", (IOptions<KeycloakOptions> options) =>
        {
            var k = options.Value;
            var index = k.Authority.LastIndexOf("/realms/", StringComparison.Ordinal);
            return Results.Json(new
                { url = k.Authority[..index], realm = k.Authority[(index + 8)..], clientId = k.BrowserClientId },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous()
            .ExcludeFromDescription();
        app.MapGet("/", () => Results.Redirect("/demo/")).AllowAnonymous().ExcludeFromDescription();
        await app.RunAsync();
    }
}
