using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.ControlService;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task JoinedGuestReceivesScopedActualSettingsAndMotion()
    {
        await using var bridge = new FakeBridge { Position = 73.5, Velocity = -6.25 };
        await using var engine = new PrototypeEngine(bridge, TimeProvider.System);
        bridge.Publish();
        var authority = new ControlAuthority(engine);
        var registry = new ControlHubConnectionRegistry();
        var guests = new GuestControlOperations(authority, registry);
        const string hubConnectionId = "guest-hub-connection";
        registry.StageGuest(hubConnectionId);
        var invitation = engine.GetInvitation(new Uri("http://127.0.0.1:5182"));
        var secret = new Uri(invitation.Url).Fragment["#invite=".Length..];

        await Task.Yield();
        guests.Join(hubConnectionId, new("join", invitation.SessionId, secret));
        var status = guests.Status(hubConnectionId);

        Assert.Equal(73.5, status.Position);
        Assert.Equal(-6.25, status.Velocity);
        Assert.NotNull(status.Current);
        Assert.Equal(0, status.Current.Speed);
        Assert.Equal(10, status.Current.Stroke);
        Assert.Equal(20, status.Current.Depth);
        Assert.Equal(0, status.VerifiedSeq);
        Assert.False(status.DeviceEnabled);
        Assert.True(status.IsSimulator);
        Assert.Null(status.ConnectionId);
        bridge.External(MotionSettings.Stopped with { Speed = 10 });
        Assert.True(guests.Status(hubConnectionId).DeviceEnabled);
    }
}
