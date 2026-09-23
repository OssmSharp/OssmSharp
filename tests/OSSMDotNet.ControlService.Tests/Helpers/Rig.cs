using OSSMDotNet.ControlService.Tests.Authority;

namespace OSSMDotNet.ControlService.Tests.Helpers;

/// <summary>
/// Couples the control engine with test authority, a manual clock, and a controllable device bridge.
/// </summary>
internal sealed class Rig : IAsyncDisposable
{
    public ManualTimeProvider Time { get; } = new();
    public ManualTimeProvider Clock => Time;
    public FakeBridge Bridge { get; } = new();
    public ControlEngine Core { get; }
    public AuthorityTestEngine Engine { get; }
    public JoinResult Guest { get; private set; } = null!;

    public Rig()
    {
        Core = new ControlEngine(Bridge, Time);
        Engine = new(Core, new ControlAuthority(Core, Time));
        Bridge.Connected = true;
        Bridge.Publish();
    }

    public Invitation Invite() => Engine.CreateInvitation(Guid.NewGuid(), new Uri("http://localhost:8080"), "operator");

    public void ConnectGuest()
    {
        var invitation = Invite();
        Guest = Engine.Join(invitation.SessionId, ControlEngineTests.Secret(invitation), "connection");
    }

    /// <summary>
    /// Recovers the device, admits and approves a guest, then arms control without setup writes in the log.
    /// </summary>
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

    /// <summary>
    /// Creates guest input using the current authority grant and policy version.
    /// </summary>
    /// <param name="sequence">The guest input sequence number.</param>
    /// <param name="desired">The requested settings, or baseline motion settings when omitted.</param>
    /// <returns>Input carrying the current grant and requested motion settings.</returns>
    public MotionInput Input(long sequence, MotionSettings? desired = null)
    {
        var grant = Engine.Snapshot.Grant!;
        return new(2, grant.Epoch, grant.PolicyVersion, grant.Id, sequence, true, desired ?? TestMotion.Settings(speed: 10));
    }

    public MotionInput InputWithoutGrant(long sequence) => new(2, "old", 1, "old", sequence, true, TestMotion.Settings());
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
        Bridge.BeforeStatePublished = null;
        Bridge.AutoComplete = true;
        Bridge.CompleteAll();
        Pump();
        try { await Core.DisposeAsync(); }
        finally { await Bridge.DisposeAsync(); }
    }
}
