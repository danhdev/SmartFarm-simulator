using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace SmartFarm.Tests;

public sealed class ApiFactory : WebApplicationFactory<MUGVN_SmartFarm.Program>
{
    internal const string Issuer = "https://identity.test/realms/smartfarm";

    internal static readonly SymmetricSecurityKey Key =
        new(Encoding.UTF8.GetBytes("test-signing-key-with-at-least-32-bytes-123456"));

    public readonly Mock<IDeviceStore> Store = new();
    public readonly Mock<IDeviceStateStore> StateStore = new();
    public readonly Mock<ITelemetryQueries> TelemetryQueries = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IDeviceStore>();
            services.RemoveAll<IDeviceStateStore>();
            services.RemoveAll<ITelemetryQueries>();
            services.AddSingleton(StateStore.Object);
            services.AddSingleton(Store.Object);
            services.AddSingleton(TelemetryQueries.Object);
            Store.Setup(s => s.DevicesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            StateStore.Setup(s => s.StatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = null!;
                o.MetadataAddress = null!;
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(Key);
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                o.TokenValidationParameters.ValidIssuer = Issuer;
                o.TokenValidationParameters.ValidAudience = "smartfarm-api";
                o.TokenValidationParameters.IssuerSigningKey = Key;
            });
        });
    }

    public static string Token(string issuer = Issuer, string audience = "smartfarm-api", string role = "farm-admin",
        bool expired = false)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience,
            [new Claim("realm_access", $"{{\"roles\":[\"{role}\"]}}"), new Claim("sub", "test-admin")],
            now.AddMinutes(-10), expired ? now.AddMinutes(-5) : now.AddMinutes(5),
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
    }

    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
}

public sealed class ApiAuthenticationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ApiAuthenticationTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task AnonymousApiAndHub_AreUnauthorized()
    {
        using var c = factory.Client();
        var response = await c.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsync("/hubs/telemetry/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    public async Task InvalidJwt_IsRejected(string fault)
    {
        using var c = factory.Client(ApiFactory.Token(issuer: fault == "issuer" ? "https://wrong" : ApiFactory.Issuer,
            audience: fault == "audience" ? "wrong" : "smartfarm-api", expired: fault == "expired"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/devices")).StatusCode);
    }

    [Fact]
    public async Task TamperedSignature_IsRejected()
    {
        var jwt = ApiFactory.Token();
        var parts = jwt.Split('.');
        parts[2] = (parts[2][0] == 'a' ? "b" : "a") + parts[2][1..];
        using var c = factory.Client(string.Join('.', parts));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/devices")).StatusCode);
    }

    [Fact]
    public async Task ValidTokenWithoutFarmRole_IsForbidden()
    {
        using var c = factory.Client(ApiFactory.Token(role: "viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/devices")).StatusCode);
    }

    [Fact]
    public async Task Administrator_CanReadDevicesAndNegotiateHub()
    {
        using var c = factory.Client(ApiFactory.Token());
        var r = await c.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("[]", await r.Content.ReadAsStringAsync());
        var hub = await c.PostAsync("/hubs/telemetry/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        Assert.Contains("connectionToken", await hub.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QueryStringToken_IsOnlyAcceptedOnHubRoute()
    {
        using var c = factory.Client();
        var t = Uri.EscapeDataString(ApiFactory.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/devices?access_token=" + t)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c.PostAsync("/hubs/telemetry/negotiate?negotiateVersion=1&access_token=" + t, null)).StatusCode);
    }

    [Fact]
    public async Task DeviceCreation_ReturnsLocationAndSnakeCaseResponse()
    {
        Device? created = null;
        factory.Store.Setup(s => s.CreateDeviceAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .Callback<Device, CancellationToken>((d, _) => created = d).Returns(Task.CompletedTask);
        using var c = factory.Client(ApiFactory.Token());
        var r = await c.PostAsJsonAsync("/api/devices",
            new { device_id = "test-pi", node_id = 99, label = "Test Pi", reporting_interval_seconds = 300 });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("/api/devices/test-pi", r.Headers.Location?.ToString());
        Assert.Equal(99, created!.NodeId);
        Assert.Contains("\"device_id\":\"test-pi\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingDevice_ReturnsProblemDetails()
    {
        using var c = factory.Client(ApiFactory.Token());
        var r = await c.GetAsync("/api/devices/absent");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Device not found", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApi_ListsApplicationEndpoints()
    {
        using var c = factory.Client();
        var r = await c.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await r.Content.ReadAsStringAsync();
        Assert.Contains("/api/devices/{deviceId}/telemetry/aggregates", json);
        Assert.Contains("/api/telemetry/latest", json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("paths").GetProperty("/api/devices/{deviceId}/telemetry/history")
            .GetProperty("get");
        Assert.Contains(path.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "from" && p.GetProperty("required").GetBoolean());
        Assert.True(path.GetProperty("security")[0].TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task BrowserConfiguration_UsesKeycloakClientIdCasing()
    {
        using var client = factory.Client();
        var response = await client.GetAsync("/demo/config.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("smartfarm-demo", document.RootElement.GetProperty("clientId").GetString());
        Assert.False(document.RootElement.TryGetProperty("client_id", out _));
    }
}
