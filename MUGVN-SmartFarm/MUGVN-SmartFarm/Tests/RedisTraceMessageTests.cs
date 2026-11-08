using System.Text.Json;
using SmartFarm.Infrastructure.Redis;
using Xunit;

namespace SmartFarm.Tests;

public sealed class RedisTraceMessageTests
{
    private const string Parent = "00-1234567890abcdef1234567890abcdef-0123456789abcdef-01";
    private static readonly DeviceStateEvent State = new("farm-001", "pi4-1", "online", null, null);

    [Fact]
    public void PropagatedHeaders_RoundTripWithoutChangingEventFields()
    {
        var json = RedisTraceMessage.Serialize(State, new Dictionary<string, string>
        {
            ["traceparent"] = Parent,
            ["tracestate"] = "dd=s:1",
            ["Authorization"] = "must-not-export"
        });
        var received = RedisTraceMessage.Deserialize<DeviceStateEvent>(json);
        Assert.Equal(State, received.Value);
        Assert.Equal(Parent, received.Headers["traceparent"]);
        Assert.Equal("dd=s:1", received.Headers["tracestate"]);
        Assert.Equal(2, received.Headers.Count);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("pi4-1", document.RootElement.GetProperty("device_id").GetString());
        Assert.DoesNotContain("must-not-export", json);
    }

    [Fact]
    public void LegacyEvent_IsReadableAndHasNoParentContext()
    {
        const string json = """{"farm_id":"farm-001","device_id":"pi4-1","availability":"online"}""";
        var received = RedisTraceMessage.Deserialize<DeviceStateEvent>(json);
        Assert.Equal(State, received.Value);
        Assert.Empty(received.Headers);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"traceparent\":42}")]
    [InlineData("{\"traceparent\":\"invalid\"}")]
    [InlineData("{\"traceparent\":\"00-00000000000000000000000000000000-0123456789abcdef-01\"}")]
    [InlineData("{\"traceparent\":\"00-1234567890abcdef1234567890abcdef-0000000000000000-01\"}")]
    public void MalformedMetadata_DoesNotRejectTheEvent(string metadata)
    {
        var json = "{\"farm_id\":\"farm-001\",\"device_id\":\"pi4-1\",\"availability\":\"online\",\"_trace\":" + metadata + "}";
        var received = RedisTraceMessage.Deserialize<DeviceStateEvent>(json);
        Assert.Equal(State, received.Value);
        Assert.Empty(received.Headers);
    }

    [Fact]
    public void TypedSignalREvent_DoesNotExposeTransportMetadata()
    {
        var received = RedisTraceMessage.Deserialize<DeviceStateEvent>(RedisTraceMessage.Serialize(State,
            new Dictionary<string, string> { ["traceparent"] = Parent }));
        var browserJson = JsonSerializer.Serialize(received.Value, JsonDefaults.Options);
        Assert.Equal(JsonSerializer.Serialize(State, JsonDefaults.Options), browserJson);
        Assert.DoesNotContain("_trace", browserJson);
        // An older consumer can also read the transport JSON directly.
        Assert.Equal(State, JsonSerializer.Deserialize<DeviceStateEvent>(RedisTraceMessage.Serialize(State,
            new Dictionary<string, string> { ["traceparent"] = Parent }), JsonDefaults.Options));
    }

    [Fact]
    public async Task ConcurrentEvents_KeepTheirOwnContextAndDevice()
    {
        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(async index =>
        {
            var parent = $"00-{index:x32}-{index:x16}-01";
            var value = State with { DeviceId = $"pi-{index}" };
            var json = RedisTraceMessage.Serialize(value, new Dictionary<string, string> { ["traceparent"] = parent });
            await Task.Yield();
            var received = RedisTraceMessage.Deserialize<DeviceStateEvent>(json);
            Assert.Equal(value, received.Value);
            Assert.Equal(parent, received.Headers["traceparent"]);
            return received.Headers["traceparent"];
        }));
        Assert.Equal(20, results.Distinct().Count());
    }

    [Fact]
    public void InvalidJson_IsRejectedRatherThanFabricatingAnEvent()
    {
        Assert.ThrowsAny<JsonException>(() => RedisTraceMessage.Deserialize<DeviceStateEvent>("{invalid"));
    }
}
