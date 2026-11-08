using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFarm.Ingestion;
using Xunit;

namespace SmartFarm.Tests;

public sealed class IngestionProcessorTests
{
    private readonly Mock<IDeviceStore> devices = new();
    private readonly Mock<IDeviceStateStore> states = new();
    private readonly Mock<ITelemetryWriter> readings = new();
    private readonly Mock<INotifier> notifier = new();
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-11-05T02:00:00Z");

    private IngestionProcessor Processor() => new(devices.Object, states.Object, readings.Object, notifier.Object, Options.Create(new FarmOptions()),
        Options.Create(new MqttOptions()), NullLogger<IngestionProcessor>.Instance);

    private void Registered()
    {
        devices.Setup(s => s.DeviceAsync("pi4-1", It.IsAny<CancellationToken>())).ReturnsAsync(new Device
            { DeviceId = "pi4-1", NodeId = 6, FarmId = "farm-001", Label = "Pi" });
        states.Setup(s => s.UpdateTelemetryStateAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState { DeviceId = "pi4-1", LastReceivedAt = now.UtcDateTime });
    }

    [Fact]
    public async Task NewReading_StoresBeforePublishing()
    {
        Registered();
        var order = new List<string>();
        readings.Setup(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("store")).Returns(Task.CompletedTask);
        notifier.Setup(n => n.PublishTelemetryAsync(It.IsAny<TelemetryDto>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("publish")).Returns(Task.CompletedTask);
        Assert.Equal("inserted",
            await Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        Assert.Equal(new[] { "store", "publish" }, order);
    }

    [Fact]
    public async Task Duplicate_RepairsStateWithoutReinsertingOrChangingOriginalReceipt()
    {
        Registered();
        var original = TelemetryParser.Parse("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), "mugvn/farm",
            "farm-001", now.AddHours(-1));
        readings.Setup(s => s.FindReadingAsync("pi4-1", TelemetryParserTests.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(original);
        Assert.Equal("duplicate",
            await Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        readings.Verify(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()), Times.Never);
        states.Verify(s => s.UpdateTelemetryStateAsync(original, It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(
            n => n.PublishTelemetryAsync(It.Is<TelemetryDto>(r => r.IngestedAt == now.AddHours(-1)),
                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false, 6)]
    [InlineData(true, true, 6)]
    [InlineData(true, false, 18)]
    public async Task DisabledDecommissionedOrMismatchedDevice_CannotWrite(bool enabled, bool decommissioned, int node)
    {
        devices.Setup(s => s.DeviceAsync("pi4-1", It.IsAny<CancellationToken>())).ReturnsAsync(new Device
        {
            DeviceId = "pi4-1", FarmId = "farm-001", NodeId = node, Label = "Pi", Enabled = enabled,
            Decommissioned = decommissioned
        });
        await Assert.ThrowsAsync<InvalidTelemetryException>(() =>
            Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        readings.Verify(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownDevice_CannotWrite()
    {
        await Assert.ThrowsAsync<InvalidTelemetryException>(() =>
            Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        notifier.Verify(n => n.PublishTelemetryAsync(It.IsAny<TelemetryDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedisFailure_DoesNotFailSuccessfulStorage()
    {
        Registered();
        notifier.Setup(n => n.PublishTelemetryAsync(It.IsAny<TelemetryDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("offline"));
        notifier.Setup(n => n.PublishStateAsync(It.IsAny<DeviceStateEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("offline"));
        Assert.Equal("inserted",
            await Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        readings.Verify(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AmbiguousInsert_RetryReadsStoredMeasurementAndRepairsState()
    {
        Registered();
        Reading? saved = null;
        readings.Setup(s => s.FindReadingAsync("pi4-1", TelemetryParserTests.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => saved);
        readings.Setup(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()))
            .Callback<Reading, CancellationToken>((r, _) => saved = r).ThrowsAsync(new IOException("reply lost"));
        await Assert.ThrowsAsync<IOException>(() =>
            Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        notifier.Verify(n => n.PublishTelemetryAsync(It.IsAny<TelemetryDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("duplicate",
            await Processor().ProcessAsync("mugvn/farm/pi4-1/env", TelemetryParserTests.Payload(), now, default));
        readings.Verify(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()), Times.Once);
        states.Verify(s => s.UpdateTelemetryStateAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Availability_UpdatesStateWithoutTelemetryInsertion()
    {
        Registered();
        states.Setup(s => s.UpdateAvailabilityAsync("pi4-1", "offline", now.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceState
                { DeviceId = "pi4-1", Availability = "offline", AvailabilityObservedAt = now.UtcDateTime });
        Assert.Equal("availability",
            await Processor().ProcessAsync("mugvn/farm/pi4-1/avail",
                System.Text.Encoding.UTF8.GetBytes("""{"device_id":"pi4-1","status":"offline"}"""), now, default));
        notifier.Verify(
            n => n.PublishStateAsync(
                It.Is<DeviceStateEvent>(e => e.Availability == "offline" && e.AvailabilityObservedAt == now),
                It.IsAny<CancellationToken>()), Times.Once);
        readings.Verify(s => s.InsertAsync(It.IsAny<Reading>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}