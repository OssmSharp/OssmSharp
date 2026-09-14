using FastMoq;
using Microsoft.Extensions.Time.Testing;
using OSSMDotNet.DeviceControl;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.ControlCore.Tests;

internal sealed class Rig : IAsyncDisposable
{
    private readonly Mocker _mocker = new();
    public FakeTimeProvider Time { get; } = new();
    public WallClockTimeProvider Clock { get; }
    public FakeBridge Bridge { get; } = new();
    public PrototypeEngine Engine { get; }
    public JoinResult Guest { get; private set; } = null!;

    public Rig()
    {
        Clock = new(Time);
        _mocker.AddType<IDeviceBridge>(Bridge);
        _mocker.AddType<TimeProvider>(Clock);
        Engine = Assert.IsType<PrototypeEngine>(_mocker.CreateInstance<PrototypeEngine>());
        Bridge.Publish();
    }

    public Invitation Invite() => Engine.CreateInvitation(Guid.NewGuid(), new Uri("http://localhost:8080"), "operator");

    public void ConnectGuest()
    {
        var invitation = Invite();
        Guest = Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "connection");
    }

    public void Arm()
    {
        Engine.Recover();
        Engine.SetLimits(new(50, 100));
        Pump();
        ConnectGuest();
        Engine.Approve(Guest.ConnectionId);
        Engine.Arm();
        Bridge.ClearCalls();
    }

    public MotionInput Input(long sequence, DesiredState? desired = null)
    {
        var grant = Engine.Snapshot.Grant!;
        return new(2, grant.Epoch, grant.PolicyVersion, grant.Id, sequence, true, desired ?? DesiredState.Stopped with { Speed = 10 });
    }

    public MotionInput InputWithoutGrant(long sequence) => new(2, "old", 1, "old", sequence, true, DesiredState.Stopped);
    public void Submit(MotionInput input) => Engine.Submit(Guest.SessionId, Guest.GuestToken, Guest.ConnectionId, input);
    public void Pump()
    {
        for (var i = 0; i < 8; i++)
            Engine.Tick();
    }

    public void Advance(int milliseconds)
    {
        Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Bridge.Publish();
        Engine.Tick();
    }

    public async ValueTask DisposeAsync()
    {
        Bridge.AutoComplete = true;
        Bridge.CompleteAll();
        try { await Engine.DisposeAsync(); }
        finally { await _mocker.DisposeAsync(); }
    }
}
