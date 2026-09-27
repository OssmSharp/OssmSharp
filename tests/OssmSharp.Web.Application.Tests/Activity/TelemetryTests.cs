using OssmSharp.Web.Application;

namespace OssmSharp.Web.Application.Tests.Activity;

/// <summary>
/// Checks safe operational telemetry emitted by the control service during device activity.
/// </summary>
public sealed class TelemetryTests
{
    [Fact]
    public async Task BridgeExternalSpeedAndDepthChangePublishesOneFullMotionError()
    {
        await using var bridge = new FakeBridge { Connected = true };
        var changes = new List<OneOf.OneOf<DeviceState, Exception>>();
        bridge.DeviceStateChanged += changes.Add;
        var previous = TestMotion.Settings();
        var current = previous with { Speed = 25, Depth = 40 };

        bridge.External(current);

        Assert.Collection(
            changes,
            change => Assert.Equal(current, change.AsT0.MotionSettings),
            change =>
            {
                var error = Assert.IsType<UnexpectedMotionChangeException>(change.AsT1);
                Assert.Equal(previous, error.PreviousSettings);
                Assert.Same(current, error.CurrentSettings);
                Assert.Null(error.PendingRequestedSettings);
            });
    }

    [Fact]
    public async Task JoinedGuestReceivesScopedActualSettingsAndMotion()
    {
        await using var bridge = new FakeBridge { Position = 73.5, Velocity = -6.25 };
        await using var engine = new ControlEngine(bridge, TimeProvider.System);
        bridge.Connected = true;
        bridge.Publish();
        await using var authority = new ControlAuthority(engine, TimeProvider.System);
        var guests = new GuestControlOperations(authority);
        const string logicalConnectionId = "guest-logical-connection";
        var invitation = authority.ReadInvitation(new Uri("http://127.0.0.1:5182"));
        var secret = new Uri(invitation.Url).Fragment["#invite=".Length..];

        await Task.Yield();
        var joined = guests.Join(logicalConnectionId, new("join", invitation.SessionId, secret));
        var status = guests.Status(joined.SessionId, joined.GuestToken, joined.ConnectionId);

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
        bridge.External(TestMotion.Settings(speed: 10));
        Assert.True(guests.Status(joined.SessionId, joined.GuestToken, joined.ConnectionId).DeviceEnabled);
    }
}
