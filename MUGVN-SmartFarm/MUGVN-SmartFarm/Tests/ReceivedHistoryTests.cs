using System.Net;
using System.Net.Http.Json;
using Moq;
using SmartFarm.Contracts;
using SmartFarm.Contracts.Devices;
using SmartFarm.Contracts.History;
using SmartFarm.Contracts.Telemetry;
using SmartFarm.Infrastructure.Mongo;
using Xunit;

namespace SmartFarm.Tests;

public sealed class ReceivedHistoryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ReceivedHistoryTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public void Cursor_BindsDeviceAndTimeRangeAndRejectsMalformedValues()
    {
        var reading = TelemetryParser.Parse("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(),
            "mugvn/farm", "farm-001", DateTimeOffset.UtcNow);
        var from = new DateTimeOffset(reading.IngestedAt.AddMinutes(-1), TimeSpan.Zero);
        var query = new ReceivedHistoryQuery("pi4-1", from, from.AddHours(1));
        var cursor = ReceivedHistoryCursorCodec.Encode(query, reading);
        Assert.Equal(reading.MessageId, ReceivedHistoryCursorCodec.Decode(query with { Cursor = cursor })!.MessageId);
        Assert.Throws<FarmException>(() => ReceivedHistoryCursorCodec.Decode(query with
            { DeviceId = "pi4-2", Cursor = cursor }));
        Assert.Throws<FarmException>(() => ReceivedHistoryCursorCodec.Decode(query with
            { From = from.AddSeconds(1), Cursor = cursor }));
        Assert.Throws<FarmException>(() => ReceivedHistoryCursorCodec.Decode(query with
            { Cursor = "not-base64" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void InvalidPageSize_IsRejected(int limit) => Assert.Throws<FarmException>(() =>
        new ReceivedHistoryQuery("pi4-1", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            limit).Validate());

    [Fact]
    public async Task Endpoint_RequiresFarmAdministrator()
    {
        const string path = "/api/devices/pi4-1/telemetry/received-history?from=2026-11-07T00:00:00Z&to=2026-11-08T00:00:00Z";
        using var anonymous = factory.Client();
        using var viewer = factory.Client(ApiFactory.Token(role: "viewer"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Endpoint_UsesReceivedTimeQueryAndSnakeCasePage()
    {
        var from = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(1);
        factory.Store.Setup(s => s.DeviceAsync("pi4-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Device { DeviceId = "pi4-1", FarmId = "farm-001", NodeId = 6, Label = "Pi 1" });
        ReceivedHistoryQuery? captured = null;
        factory.TelemetryQueries.Setup(q => q.ReceivedHistoryAsync(It.IsAny<ReceivedHistoryQuery>(),
                It.IsAny<CancellationToken>()))
            .Callback<ReceivedHistoryQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(new HistoryPage([], null));
        using var admin = factory.Client(ApiFactory.Token());
        var response = await admin.GetAsync($"/api/devices/pi4-1/telemetry/received-history?from={from.UtcDateTime:O}&to={to.UtcDateTime:O}&limit=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pi4-1", captured?.DeviceId);
        Assert.Equal(from, captured?.From);
        Assert.Equal(to, captured?.To);
        Assert.Equal(7, captured?.Limit);
        var page = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(page.TryGetProperty("next_cursor", out _));
    }
}
