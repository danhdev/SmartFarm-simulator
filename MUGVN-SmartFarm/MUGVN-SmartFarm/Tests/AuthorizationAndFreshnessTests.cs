using System.Security.Claims;
using Microsoft.Extensions.Options;
using Moq;
using MUGVN_SmartFarm.Api;
using MUGVN_SmartFarm.Application.Devices;
using Xunit;

namespace SmartFarm.Tests;

public sealed class AuthorizationAndFreshnessTests
{
    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"roles\":\"farm-admin\"}")]
    [InlineData("{\"roles\":[\"viewer\"]}")]
    public void MalformedOrUnprivilegedRoleClaim_IsDenied(string value) => Assert.False(
        FarmAuthorization.HasRole(new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access", value)], "jwt")),
            "farm-admin"));

    [Fact]
    public void UnauthenticatedIdentity_CannotUseAdminClaim() => Assert.False(
        FarmAuthorization.HasRole(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("realm_access", "{\"roles\":[\"farm-admin\"]}")])),
            "farm-admin"));

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, false)]
    [InlineData(601, true)]
    public async Task Freshness_UsesReceiptTimeWithStrictTwiceIntervalBoundary(int age, bool stale)
    {
        var now = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
        var store = new Mock<IDeviceStore>();
        store.Setup(s => s.DeviceAsync("pi", It.IsAny<CancellationToken>())).ReturnsAsync(new Device
            { DeviceId = "pi", FarmId = "farm-001", NodeId = 6, Label = "Pi", ReportingIntervalSeconds = 300 });
        var states = new Mock<IDeviceStateStore>();
        states.Setup(s => s.StatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            new DeviceState
                { DeviceId = "pi", LastReceivedAt = now.AddSeconds(-age).UtcDateTime, Availability = "offline" }
        ]);
        var result =
            await new DeviceService(store.Object, states.Object, Options.Create(new FarmOptions()), new FixedClock(now)).GetAsync("pi",
                default);
        Assert.Equal(stale, result.IsStale);
        Assert.Equal("offline", result.Availability);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}