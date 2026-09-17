using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Tests;

public sealed class ControlAuthorityTests
{
    [Fact]
    public async Task AcceptedStopAlwaysAdvancesAuthorityRevisionIncludingWhenAlreadyPaused()
    {
        await using var bridge = new FakeBridge();
        await using var engine = CreateEngine(bridge);
        var authority = new ControlAuthority(engine);
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
        var authority = new ControlAuthority(engine);
        authority.Recover(authority.Read().Precondition);
        var invitation = engine.GetInvitation(new Uri("http://127.0.0.1:5182"));
        var guest = engine.Join(invitation.SessionId, new Uri(invitation.Url).Fragment["#invite=".Length..], "guest-connection");
        var admitted = authority.Read();
        engine.TransportLost(guest.ConnectionId);

        var error = Assert.Throws<ControlRejectedException>(() =>
            authority.Approve(admitted.Precondition, guest.ConnectionId));

        Assert.Equal("guest-admission-mismatch", error.Code);
        Assert.Equal("Ended", engine.Snapshot.SessionMode);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task OperatorStatusCapturesTheActiveAdmissionAndInvitationTogether()
    {
        await using var bridge = new FakeBridge();
        await using var engine = CreateEngine(bridge);
        var authority = new ControlAuthority(engine);
        var invitation = authority.ReadInvitation(new Uri("http://127.0.0.1:5182"));
        authority.JoinGuest(invitation.SessionId, new Uri(invitation.Url).Fragment["#invite=".Length..], "guest-connection");

        var status = authority.ReadOperatorStatus(new Uri("http://127.0.0.1:5182"));

        Assert.Equal(status.Invitation.SessionId, status.Authority.Snapshot.SessionId);
        Assert.Equal(status.Invitation.SessionId, status.Authority.Precondition.GuestAdmission.SessionId);
        Assert.Equal("PendingApproval", status.Authority.Snapshot.SessionMode);
    }

    private static PrototypeEngine CreateEngine(FakeBridge bridge)
    {
        var engine = new PrototypeEngine(bridge, TimeProvider.System);
        bridge.Publish();
        return engine;
    }
}
