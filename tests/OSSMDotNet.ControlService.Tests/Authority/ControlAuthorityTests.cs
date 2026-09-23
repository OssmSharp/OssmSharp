using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Tests.Authority;

/// <summary>
/// Verifies guest admission, operator state, and stop behavior at the authority boundary.
/// </summary>
public sealed class ControlAuthorityTests
{
    private static readonly Uri GuestOrigin = new("http://127.0.0.1:5182");

    [Fact]
    public async Task AcceptedStopAlwaysAdvancesAuthorityRevisionIncludingWhenAlreadyPaused()
    {
        await using var bridge = new FakeBridge();
        await using var engine = CreateEngine(bridge);
        await using var authority = new ControlAuthority(engine, TimeProvider.System);
        var first = authority.Read();

        var stopped = authority.Stop();
        var stoppedAgain = authority.Stop();

        Assert.Equal(first.Precondition.AuthorityRevision + 1, stopped.Precondition.AuthorityRevision);
        Assert.Equal(stopped.Precondition.AuthorityRevision + 1, stoppedAgain.Precondition.AuthorityRevision);
    }

    [Fact]
    public async Task AuthorityOperationRejectsStaleGuestAdmissionBeforeCallingTheEngine()
    {
        await using var bridge = new FakeBridge();
        await using var engine = CreateEngine(bridge);
        await using var authority = new ControlAuthority(engine, TimeProvider.System);
        authority.Recover(authority.Read().Precondition);
        var invitation = authority.ReadInvitation(GuestOrigin);
        var guest = authority.JoinGuest(
            invitation.SessionId,
            new Uri(invitation.Url).Fragment["#invite=".Length..],
            "guest-connection");
        var admitted = authority.Read();
        authority.GuestTransportLost(guest.ConnectionId);

        var error = Assert.Throws<ControlRejectedException>(() =>
            authority.Approve(admitted.Precondition, guest.ConnectionId));

        Assert.Equal("guest-admission-mismatch", error.Code);
        Assert.Equal("Ended", authority.Read().Snapshot.SessionMode);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task OperatorStatusCapturesTheActiveAdmissionAndInvitationTogether()
    {
        await using var bridge = new FakeBridge();
        await using var engine = CreateEngine(bridge);
        await using var authority = new ControlAuthority(engine, TimeProvider.System);
        var invitation = authority.ReadInvitation(new Uri("http://127.0.0.1:5182"));
        authority.JoinGuest(invitation.SessionId, new Uri(invitation.Url).Fragment["#invite=".Length..], "guest-connection");

        var status = authority.ReadOperatorStatus(new Uri("http://127.0.0.1:5182"));

        Assert.Equal(status.Invitation.SessionId, status.Authority.Snapshot.SessionId);
        Assert.Equal(status.Invitation.SessionId, status.Authority.Precondition.GuestAdmission.SessionId);
        Assert.Equal("PendingApproval", status.Authority.Snapshot.SessionMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredApprovedAdmissionCannotArmExecution(bool allowControl)
    {
        await using var bridge = new FakeBridge();
        var time = new ManualTimeProvider();
        await using var engine = new PrototypeEngine(bridge, time);
        bridge.Connected = true;
        bridge.Publish();
        await using var authority = new ControlAuthority(engine, time);
        authority.Recover(authority.Read().Precondition);
        var invitation = authority.ReadInvitation(GuestOrigin);
        var joined = authority.JoinGuest(
            invitation.SessionId,
            new Uri(invitation.Url).Fragment["#invite=".Length..],
            "guest-connection");
        authority.Approve(authority.Read().Precondition, joined.ConnectionId);
        var approved = authority.Read();
        time.Advance(TimeSpan.FromMinutes(30));

        var error = Assert.Throws<ControlRejectedException>(() =>
            _ = allowControl
                ? authority.AllowControl(approved.Precondition)
                : authority.Arm(approved.Precondition));

        Assert.Equal("session-not-approved", error.Code);
        Assert.Equal("Paused", authority.Read().Snapshot.Mode);
        Assert.Equal("Ended", authority.Read().Snapshot.SessionMode);
        Assert.Null(authority.Read().Snapshot.Grant);
    }

    private static PrototypeEngine CreateEngine(FakeBridge bridge)
    {
        var engine = new PrototypeEngine(bridge, TimeProvider.System);
        bridge.Connected = true;
        bridge.Publish();
        return engine;
    }
}
