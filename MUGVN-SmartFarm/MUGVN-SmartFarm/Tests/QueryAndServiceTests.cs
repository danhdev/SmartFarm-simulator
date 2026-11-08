using Microsoft.Extensions.Options;
using Moq;
using MUGVN_SmartFarm.Api;
using MUGVN_SmartFarm.Application.Devices;
using Xunit;

namespace SmartFarm.Tests;

public sealed class QueryAndServiceTests
{
    [Fact]
    public void Cursor_RoundtripsAndCannotChangeRangeOrDevice()
    {
        var reading = TelemetryParser.Parse("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), "mugvn/farm",
            "farm-001", DateTimeOffset.UtcNow);
        var q = new HistoryQuery("pi4-1", new(2025, 7, 24, 0, 0, 0, TimeSpan.Zero),
            new(2025, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var cursor = CursorCodec.Encode(q, reading);
        Assert.Equal(TelemetryParserTests.Id, CursorCodec.Decode(q with { Cursor = cursor })!.MessageId);
        Assert.Throws<FarmException>(() => CursorCodec.Decode(q with { DeviceId = "pi4-2", Cursor = cursor }));
        Assert.Throws<FarmException>(() => CursorCodec.Decode(q with { To = q.To.AddDays(1), Cursor = cursor }));
        Assert.Throws<FarmException>(() => CursorCodec.Decode(q with { Cursor = "bad" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void PageSizeOutsideBounds_IsRejected(int limit) => Assert.Throws<FarmException>(() =>
        new HistoryQuery("pi4-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), limit).Validate());

    [Fact]
    public void EmptyOrReversedRange_IsRejected()
    {
        var t = DateTimeOffset.UnixEpoch;
        Assert.Throws<FarmException>(() => new HistoryQuery("x", t, t).Validate());
        Assert.Throws<FarmException>(() => new HistoryQuery("x", t.AddDays(1), t).Validate());
    }

    [Fact]
    public async Task DeviceCrud_PreservesSourceIdentityAndHistoryOnDecommission()
    {
        var store = new Mock<IDeviceStore>();
        Device? saved = null;
        store.Setup(s => s.CreateDeviceAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .Callback<Device, CancellationToken>((d, _) => saved = d).Returns(Task.CompletedTask);
        store.Setup(s => s.DeviceAsync("new-pi", It.IsAny<CancellationToken>())).ReturnsAsync(() => saved);
        var states = new Mock<IDeviceStateStore>();
        states.Setup(s => s.StatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        store.Setup(s => s.SaveDeviceAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .Callback<Device, CancellationToken>((d, _) => saved = d).Returns(Task.CompletedTask);
        var service = new DeviceService(store.Object, states.Object, Options.Create(new FarmOptions()), TimeProvider.System);
        var created = await service.CreateAsync(new("new-pi", 99, "New Pi"), default);
        Assert.Equal(99, created.NodeId);
        Assert.True(created.IsStale);
        var patched = await service.PatchAsync("new-pi", new("Updated", 600, false), default);
        Assert.Equal("Updated", patched.Label);
        Assert.Equal(99, patched.NodeId);
        Assert.False(patched.Enabled);
        await service.DeleteAsync("new-pi", default);
        Assert.True(saved!.Decommissioned);
        Assert.False(saved.Enabled);
        await Assert.ThrowsAsync<FarmException>(() => service.PatchAsync("new-pi", new("Revive"), default));
    }

    [Theory]
    [InlineData("bad/topic", 1, "Pi", 300)]
    [InlineData("pi", 0, "Pi", 300)]
    [InlineData("pi", 1, "", 300)]
    [InlineData("pi", 1, "Pi", 0)]
    public async Task InvalidRegistration_IsRejected(string id, int node, string label, int interval)
    {
        var store = new Mock<IDeviceStore>();
        var service = new DeviceService(store.Object, Mock.Of<IDeviceStateStore>(), Options.Create(new FarmOptions()), TimeProvider.System);
        var e = await Assert.ThrowsAsync<FarmException>(() =>
            service.CreateAsync(new(id, node, label, interval), default));
        Assert.Equal(FarmErrorCode.InvalidInput, e.Code);
        store.Verify(s => s.CreateDeviceAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}