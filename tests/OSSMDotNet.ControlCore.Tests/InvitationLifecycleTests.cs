using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore.Tests;

public sealed class InvitationLifecycleTests
{
    [Fact]
    public async Task InvitationDoesNotExpireAndReadingItDoesNotAlterControl()
    {
        await using var rig = new Rig();
        var invitation = rig.Invite();
        rig.Time.Advance(TimeSpan.FromDays(365));
        rig.Bridge.Publish();
        rig.Pump();
        var before = rig.Engine.Snapshot;

        Assert.Equal(invitation, rig.Invite());
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
        var guest = rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "fresh");
        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        Assert.Equal(before.Mode, rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal(invitation.SessionId, guest.SessionId);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("end")]
    [InlineData("transport")]
    public async Task SameLinkRejoinsWithNewProofAndExplicitApprovalAfterSessionEnds(string ending)
    {
        await using var rig = new Rig();
        var invitation = rig.Invite();
        rig.Arm();
        var previous = rig.Guest;
        var oldInput = rig.Input(1);
        if (ending == "leave")
            rig.Engine.Leave(previous.SessionId, previous.GuestToken, previous.ConnectionId);
        else if (ending == "transport")
            rig.Engine.TransportLost(previous.ConnectionId);
        else
            rig.Engine.EndSession();
        rig.Pump();

        var next = rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "next");

        Assert.Equal(invitation, rig.Invite());
        Assert.NotEqual(previous.GuestToken, next.GuestToken);
        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
        Assert.Equal("session-not-approved", Assert.Throws<ControlRejectedException>(rig.Engine.Arm).Code);
        Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Submit(previous.SessionId, previous.GuestToken, previous.ConnectionId, oldInput)).Code);
        rig.Engine.TransportLost(previous.ConnectionId);
        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        rig.Engine.Approve(next.ConnectionId);
        Assert.Null(rig.Engine.Snapshot.Grant);
        rig.Engine.Arm();
        Assert.NotEqual(oldInput.Epoch, rig.Engine.Snapshot.Grant!.Epoch);
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 1800)]
    public async Task GuestLeaseStillExpiresExactlyAndSameLinkAllowsFreshAdmission(bool approved, int seconds)
    {
        await using var rig = new Rig();
        var invitation = rig.Invite();
        var first = rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "first");
        if (approved) rig.Engine.Approve(first.ConnectionId);
        rig.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(approved ? "Approved" : "PendingApproval",
            rig.Engine.GuestSnapshot(first.SessionId, first.GuestToken, first.ConnectionId).SessionMode);
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "second"));
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));

        var next = rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "second");

        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.NotEqual(first.GuestToken, next.GuestToken);
        Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.GuestSnapshot(first.SessionId, first.GuestToken, first.ConnectionId)).Code);
    }

    [Fact]
    public async Task InvitationRetrievalAndRejectedSecondJoinPreserveArmedAuthority()
    {
        await using var rig = new Rig();
        rig.Arm();
        var before = rig.Engine.Snapshot;
        var invitation = rig.Invite();
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "second"));
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task RejoiningDoesNotClearExternalLock()
    {
        await using var rig = new Rig();
        rig.Arm();
        var invitation = rig.Invite();
        rig.Bridge.External(MotionSettings.Stopped with { Depth = 99 });
        rig.Engine.EndSession();
        rig.Pump();
        var locked = rig.Engine.Snapshot;

        rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "next");
        rig.Engine.Approve("next");

        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.Equal(locked.LockRevision, rig.Engine.Snapshot.LockRevision);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal("external-lock", Assert.Throws<ControlRejectedException>(rig.Engine.Arm).Code);
    }

    [Fact]
    public async Task CycleClearsPendingInputAndRevokesEveryOldGuestOperation()
    {
        await using var rig = new Rig();
        rig.Arm();
        var invitation = rig.Invite();
        var previous = rig.Guest;
        var oldInput = rig.Input(1);
        rig.Submit(oldInput);

        var next = rig.Engine.CycleInvitation(new Uri("http://localhost:8080"));

        Assert.NotEqual(invitation.Url, next.Url);
        Assert.Equal(next, rig.Engine.GetInvitation(new Uri("http://localhost:8080")));
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Null(rig.Engine.Snapshot.ConnectionId);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
        Assert.True(rig.Engine.Snapshot.OperationPending);
        Assert.Empty(rig.Bridge.Calls);
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "old-link"));
        foreach (var operation in new Action[]
        {
            () => rig.Engine.GuestSnapshot(previous.SessionId, previous.GuestToken, previous.ConnectionId),
            () => rig.Engine.GuestStop(previous.SessionId, previous.GuestToken, previous.ConnectionId),
            () => rig.Engine.Leave(previous.SessionId, previous.GuestToken, previous.ConnectionId),
            () => rig.Engine.Submit(previous.SessionId, previous.GuestToken, previous.ConnectionId, oldInput),
        })
            Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(operation).Code);
        var guest = rig.Engine.Join(next.SessionId, PrototypeEngineTests.Secret(next), "next");
        rig.Engine.TransportLost(previous.ConnectionId);
        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        rig.Engine.Approve(guest.ConnectionId);
        Assert.Throws<ControlRejectedException>(rig.Engine.Arm);
        rig.Pump();
        Assert.Equal("Stop", Assert.Single(rig.Bridge.Calls).Kind);
        Assert.Null(rig.Engine.Snapshot.Grant);
        rig.Engine.Arm();
        Assert.Equal("epoch-invalid", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Submit(guest.SessionId, guest.GuestToken, guest.ConnectionId, oldInput)).Code);
    }

    private static readonly string[] expected = new[] { "Write", "Stop" };

    [Fact]
    public async Task CycleWaitsForOutstandingWriteThenSerializesStopBeforeAnyNewAuthority()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Pump();
        Assert.Equal("Write", Assert.Single(rig.Bridge.Calls).Kind);
        rig.Submit(rig.Input(2, MotionSettings.Stopped with { Speed = 15 }));

        var invitation = rig.Engine.CycleInvitation(new Uri("http://localhost:8080"));
        rig.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "next");
        rig.Engine.Approve("next");
        rig.Pump();
        Assert.Single(rig.Bridge.Calls);
        Assert.Throws<ControlRejectedException>(rig.Engine.Arm);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(expected, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(1, rig.Bridge.MaxActive);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.False(rig.Engine.Snapshot.DeviceEnabled);
        Assert.False(rig.Engine.Snapshot.OperationPending);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
    }

    [Fact]
    public async Task CyclePreservesExternalLockAndInvalidOriginDoesNotRevokeCurrentLink()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.External(MotionSettings.Stopped with { Depth = 99 });
        rig.Pump();
        var locked = rig.Engine.Snapshot;
        var invitation = rig.Engine.CycleInvitation(new Uri("http://localhost:8080"));
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.Equal(locked.LockRevision, rig.Engine.Snapshot.LockRevision);
        var before = rig.Engine.Snapshot;
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CycleInvitation(new Uri("http://example.com")));
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Equal(invitation, rig.Engine.GetInvitation(new Uri("http://localhost:8080")));
    }

    [Fact]
    public async Task RestartChangesTheLinkAndNeverArms()
    {
        Invitation invitation;
        await using (var first = new Rig())
            invitation = first.Invite();
        await using var restarted = new Rig();
        var replacement = restarted.Engine.GetInvitation(new Uri("http://localhost:8080"));
        Assert.NotEqual(invitation.Url, replacement.Url);
        Assert.Equal("Initializing", restarted.Engine.Snapshot.Mode);
        Assert.Null(restarted.Engine.Snapshot.Grant);
        Assert.Empty(restarted.Bridge.Calls);
        Assert.Throws<ControlRejectedException>(() =>
            restarted.Engine.Join(invitation.SessionId, PrototypeEngineTests.Secret(invitation), "stale"));
    }
}
