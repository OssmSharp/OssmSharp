using OssmSharp.Web.Application.Models;
using OssmSharp.Web.Application.Tests;

namespace OssmSharp.Web.Application.Tests.Helpers;

/// <summary>
/// Provides a connected device and authority with a controllable clock for policy tests.
/// </summary>
internal sealed class AuthorityRig : IAsyncDisposable
{
    internal AuthorityRig()
    {
        Core = new(Bridge, Time);
        Authority = new(Core, Time);
        Engine = new(Core, Authority);
        Bridge.Connected = true;
        Bridge.Publish();
    }

    internal ManualTimeProvider Time { get; } = new();
    internal FakeBridge Bridge { get; } = new();
    internal ControlEngine Core { get; }
    internal ControlAuthority Authority { get; }
    internal AuthorityTestEngine Engine { get; }
    internal JoinResult Guest { get; private set; } = null!;

    internal Invitation Invite() =>
        Engine.CreateInvitation(Guid.NewGuid(), new Uri("http://localhost:8080"), "operator");

    internal void ConnectGuest()
    {
        var invitation = Invite();
        Guest = Engine.Join(invitation.SessionId, InvitationProof(invitation), "connection");
    }

    /// <summary>
    /// Recovers the engine, applies limits, joins and approves a guest, and clears setup writes.
    /// </summary>
    internal void Arm()
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
    /// Creates input using the current grant and policy revision.
    /// </summary>
    /// <param name="sequence">The guest input sequence number.</param>
    /// <param name="desired">The requested settings, or the default test settings.</param>
    /// <returns>Input authorized by the current grant.</returns>
    internal MotionInput Input(long sequence, MotionSettings? desired = null)
    {
        var grant = Engine.Snapshot.Grant!;
        return new(2, grant.Epoch, grant.PolicyVersion, grant.Id, sequence, true,
            desired ?? TestMotion.Settings(speed: 10));
    }

    internal MotionInput InputWithoutGrant(long sequence) =>
        new(2, "old", 1, "old", sequence, true, TestMotion.Settings());

    internal void Submit(MotionInput input) =>
        Engine.Submit(Guest.SessionId, Guest.GuestToken, Guest.ConnectionId, input);

    internal void Pump()
    {
        for (var index = 0; index < 8; index++)
            Engine.Tick();
    }

    /// <summary>
    /// Advances the clock, publishes device state, and ticks the engine once.
    /// </summary>
    /// <param name="milliseconds">The clock advance in milliseconds.</param>
    internal void Advance(int milliseconds)
    {
        Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Bridge.Publish();
        Engine.Tick();
    }

    internal static string InvitationProof(Invitation invitation)
    {
        var fragment = new Uri(invitation.Url).Fragment;
        Assert.StartsWith("#invite=", fragment, StringComparison.Ordinal);
        return fragment["#invite=".Length..];
    }

    public async ValueTask DisposeAsync()
    {
        Bridge.BeforeStatePublished = null;
        Bridge.AutoComplete = true;
        Bridge.CompleteAll();
        Pump();
        try { await Authority.DisposeAsync(); }
        finally { await Bridge.DisposeAsync(); }
    }
}
