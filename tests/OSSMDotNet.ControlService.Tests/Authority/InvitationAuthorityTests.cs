using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Tests.Authority;

/// <summary>
/// Checks reusable invitations and the separate lifetime of guest control leases.
/// </summary>
public sealed class InvitationAuthorityTests
{
    [Fact]
    public async Task InvitationDoesNotExpireAndReadingItDoesNotAlterControl()
    {
        await using var rig = new AuthorityRig();
        var invitation = rig.Invite();
        rig.Time.Advance(TimeSpan.FromDays(365));
        rig.Bridge.Publish();
        rig.Pump();
        var before = rig.Engine.Snapshot;

        Assert.Equal(invitation, rig.Invite());
        Assert.Equal(before, rig.Engine.Snapshot);
        var guest = rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "fresh");
        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        Assert.Equal(invitation.SessionId, guest.SessionId);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("end")]
    [InlineData("transport")]
    public async Task SameLinkRejoinsWithNewProofAndExplicitApprovalAfterSessionEnds(string ending)
    {
        await using var rig = new AuthorityRig();
        var invitation = rig.Invite();
        var previous = rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "first");
        rig.Engine.Approve(previous.ConnectionId);
        if (ending == "leave") rig.Engine.Leave(previous.SessionId, previous.GuestToken, previous.ConnectionId);
        else if (ending == "transport") rig.Engine.TransportLost(previous.ConnectionId);
        else rig.Engine.EndSession();

        var next = rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "next");

        Assert.NotEqual(previous.GuestToken, next.GuestToken);
        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.GuestSnapshot(previous.SessionId, previous.GuestToken, previous.ConnectionId)).Code);
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 1800)]
    public async Task GuestLeaseStillExpiresExactlyAndSameLinkAllowsFreshAdmission(bool approved, int seconds)
    {
        await using var rig = new AuthorityRig();
        var invitation = rig.Invite();
        var first = rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "first");
        if (approved) rig.Engine.Approve(first.ConnectionId);
        rig.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(approved ? "Approved" : "PendingApproval",
            rig.Engine.GuestSnapshot(first.SessionId, first.GuestToken, first.ConnectionId).SessionMode);
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "second"));
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));

        var next = rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "second");

        Assert.Equal("PendingApproval", rig.Engine.Snapshot.SessionMode);
        Assert.NotEqual(first.GuestToken, next.GuestToken);
    }

    [Fact]
    public async Task InvitationRetrievalAndRejectedSecondJoinPreserveArmedAuthority()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var before = rig.Engine.Snapshot;
        var invitation = rig.Invite();

        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "second"));
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task RejoiningDoesNotClearExternalLock()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var invitation = rig.Invite();
        rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 99));
        rig.Engine.EndSession();
        rig.Pump();
        var revision = rig.Engine.Snapshot.LockRevision;

        rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "next");
        rig.Engine.Approve("next");

        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.Equal(revision, rig.Engine.Snapshot.LockRevision);
        Assert.Null(rig.Engine.Snapshot.Grant);
    }

    [Fact]
    public async Task CycleClearsPendingInputAndRevokesEveryOldGuestOperation()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var invitation = rig.Invite();
        var previous = rig.Guest;
        var oldInput = rig.Input(1);
        rig.Submit(oldInput);

        var next = rig.Engine.CycleInvitation(new Uri("http://localhost:8080"));

        Assert.NotEqual(invitation.Url, next.Url);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Null(rig.Engine.Snapshot.ConnectionId);
        Assert.True(rig.Engine.Snapshot.OperationPending);
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "old-link"));
        foreach (var operation in new Action[]
        {
            () => rig.Engine.GuestSnapshot(previous.SessionId, previous.GuestToken, previous.ConnectionId),
            () => rig.Engine.GuestStop(previous.SessionId, previous.GuestToken, previous.ConnectionId),
            () => rig.Engine.Leave(previous.SessionId, previous.GuestToken, previous.ConnectionId),
            () => rig.Engine.Submit(previous.SessionId, previous.GuestToken, previous.ConnectionId, oldInput),
        })
            Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(operation).Code);
    }

    [Fact]
    public async Task CycleWaitsForOutstandingWriteThenSerializesStopBeforeNewAuthority()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Pump();
        Assert.Equal("Write", Assert.Single(rig.Bridge.Calls).Kind);
        rig.Submit(rig.Input(2, TestMotion.Settings(speed: 15)));

        var invitation = rig.Engine.CycleInvitation(new Uri("http://localhost:8080"));
        rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "next");
        rig.Engine.Approve("next");
        rig.Pump();

        Assert.Single(rig.Bridge.Calls);
        Assert.Throws<ControlRejectedException>(rig.Engine.Arm);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(["Write", "Stop"], rig.Bridge.Calls.Select(call => call.Kind));
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
        await using var rig = new AuthorityRig();
        rig.Arm();
        rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 99));
        rig.Pump();
        var revision = rig.Engine.Snapshot.LockRevision;
        var invitation = rig.Engine.CycleInvitation(new Uri("http://localhost:8080"));
    var before = rig.Engine.Snapshot;

        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.Equal(revision, rig.Engine.Snapshot.LockRevision);
        Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CycleInvitation(new Uri("http://example.com")));
    Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Equal(invitation, rig.Engine.GetInvitation(new Uri("http://localhost:8080")));
    }

    [Fact]
    public async Task InvitationCompatibilityRetriesValidateOriginAndBindAttemptScopeUntilCycle()
    {
        await using var rig = new AuthorityRig();
        foreach (var origin in new[]
        {
            "http://example.com", "https://example.com/path", "https://user@example.com",
            "https://example.com/?query=1", "https://example.com/#fragment", "ftp://localhost",
            "http://127.0.0.2", "http://localhost.evil.test"
        })
            Assert.Equal("origin-invalid", Assert.Throws<ControlRejectedException>(() =>
                rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri(origin), "operator")).Code);

        var attempt = Guid.NewGuid();
        var invitation = rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator");
        Assert.Equal(invitation, rig.Engine.CreateInvitation(attempt, new Uri("https://different.example"), "operator"));
        Assert.Equal(43, AuthorityRig.InvitationProof(invitation).Length);
        Assert.False(invitation.LocalOnly);
        Assert.Null(invitation.ExpiresAt);
        Assert.Equal("attempt-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "other-operator")).Code);
        rig.Engine.CycleInvitation(new Uri("https://example.com:8443"));
        Assert.Equal("attempt-consumed", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator")).Code);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://[::1]:8080")]
    public async Task ExactLoopbackOriginsAllowHttp(string origin)
    {
        await using var rig = new AuthorityRig();
        Assert.True(rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri(origin), "operator").LocalOnly);
    }

    [Fact]
    public async Task WrongProofOrConnectionCannotReadStopLeaveOrRenewController()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var before = rig.Engine.Snapshot;
        var motion = rig.Input(1);

        foreach (var operation in new Action[]
        {
            () => rig.Engine.Submit(rig.Guest.SessionId, "wrong-secret", rig.Guest.ConnectionId, motion),
            () => rig.Engine.GuestSnapshot(rig.Guest.SessionId, rig.Guest.GuestToken, "other"),
            () => rig.Engine.GuestStop(rig.Guest.SessionId, "wrong-secret", rig.Guest.ConnectionId),
            () => rig.Engine.Leave("other", rig.Guest.GuestToken, rig.Guest.ConnectionId),
        })
            Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(operation).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task MalformedProofsRejectWithoutChangingAuthority()
    {
        await using var rig = new AuthorityRig();
        rig.Arm();
        var before = rig.Engine.Snapshot;
        foreach (var proof in new[]
        {
            string.Empty, new string('a', 42), new string('a', 44), new string('é', 43),
            new string('=', 43), new string('a', 42) + "+", new string('a', 42) + "/"
        })
            Assert.Equal("guest-unauthorized", Assert.Throws<ControlRejectedException>(() =>
                rig.Engine.GuestSnapshot(rig.Guest.SessionId, proof, rig.Guest.ConnectionId)).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
    }

    [Fact]
    public async Task ConcurrentJoinsReserveExactlyOneConnection()
    {
        await using var rig = new AuthorityRig();
        var invitation = rig.Invite();
        var results = await Task.WhenAll(Enumerable.Range(1, 32).Select(index => Task.Run(() =>
        {
            try
            {
                return rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), $"connection-{index}");
            }
            catch (ControlRejectedException)
            {
                return null;
            }
        }, TestContext.Current.CancellationToken)));

        var winner = Assert.Single(results, result => result is not null)!;
        Assert.Equal(winner.ConnectionId, rig.Engine.Snapshot.ConnectionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task InvalidCreationScopeDoesNotCreateAnInvitationOrReachTheBridge(string? scope)
    {
        await using var rig = new AuthorityRig();
        var before = rig.Engine.Snapshot;

        var error = Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri("http://localhost:8080"), scope!));

        Assert.Equal("invalid-attempt", error.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task ExactMaximumCreationScopeCreatesALocalInvitationWithoutChangingControlState()
    {
        await using var rig = new AuthorityRig();
        var before = rig.Engine.Snapshot;

        var invitation = rig.Engine.CreateInvitation(
            Guid.NewGuid(), new Uri("http://localhost:8080"), new string('s', 256));

        Assert.True(invitation.LocalOnly);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task AttemptCapacityRejectsTheNextNewAttemptWithoutChangingControlState()
    {
        await using var rig = new AuthorityRig();
        var origin = new Uri("http://localhost:8080");
        for (var count = 0; count < 4096; count++)
            rig.Engine.CreateInvitation(Guid.NewGuid(), origin, "operator");
        var before = rig.Engine.Snapshot;

        var error = Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(Guid.NewGuid(), origin, "operator"));

        Assert.Equal("attempt-capacity", error.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
    }

    [Fact]
    public async Task GuestReceivesLivePositionWithoutRawDeviceDetailsAndStaleFeedbackClearsIt()
    {
        await using var rig = new AuthorityRig();
        rig.ConnectGuest();
        rig.Bridge.Position = 63.5;
        rig.Bridge.Velocity = -12.25;
        rig.Bridge.Publish();

        var snapshot = rig.Engine.GuestSnapshot(rig.Guest.SessionId, rig.Guest.GuestToken, rig.Guest.ConnectionId);

        Assert.Equal(63.5, snapshot.Position);
        Assert.Equal(-12.25, snapshot.Velocity);
        Assert.Null(snapshot.Observed);
        Assert.Null(snapshot.ConnectionId);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1500));
        var stale = rig.Engine.GuestSnapshot(rig.Guest.SessionId, rig.Guest.GuestToken, rig.Guest.ConnectionId);
        Assert.False(stale.BridgeReady);
        Assert.Null(stale.Position);
    }

    [Fact]
    public async Task DisposalEndsTheInvitationAndDoesNotExposeItsCreationSecret()
    {
        await using var rig = new AuthorityRig();
        var invitation = rig.Invite();
        await rig.Engine.DisposeAsync();

        Assert.Equal("None", rig.Engine.Snapshot.SessionMode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal("engine-disposed", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.Join(invitation.SessionId, AuthorityRig.InvitationProof(invitation), "after-dispose")).Code);
        Assert.Equal("engine-disposed", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri("http://localhost:8080"), "operator")).Code);
        Assert.Equal("engine-disposed", Assert.Throws<ControlRejectedException>(() =>
            rig.Authority.ReadOperatorStatus(new Uri("http://localhost:8080"))).Code);
    }
}
