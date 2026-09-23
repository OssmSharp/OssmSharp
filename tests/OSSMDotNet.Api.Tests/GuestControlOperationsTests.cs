using System.Text.Json;
using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.Models;

namespace OSSMDotNet.Api.Tests;

/// <summary>
/// Verifies guest admission and input checks before operations can affect authority or the device.
/// </summary>
public sealed class GuestControlOperationsTests
{
    private static readonly Uri GuestOrigin = new("http://127.0.0.1:5182");

    public static IEnumerable<object?[]> InvalidJoinRequests =>
    [
        ["malformed-correlation", "invalid-correlation"],
        ["invalid-invitation", "invitation-invalid"],
        ["missing-stage", "guest-subscription-required"],
        ["duplicate-connection", "guest-already-joined"]
    ];

    public static IEnumerable<object?[]> InvalidInputs =>
    [
        ["correlation", "invalid-correlation"],
        ["version", "invalid-input"],
        ["grant", "grant-invalid"],
        ["policy", "policy-invalid"],
        ["desired-values", "desired-invalid"]
    ];

    [Fact]
    public async Task StageThenJoinReturnsOnlyReceiptAndGuestSafeStatus()
    {
        await using var fixture = await GuestFixture.CreateAsync();
        fixture.Registry.StageGuest("guest");
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);

        var receipt = fixture.Guests.Join(
            "guest",
            new("join-correlation", invitation.SessionId, InvitationProof(invitation)));
        var status = fixture.Guests.Status("guest");
        var receiptJson = JsonSerializer.Serialize(receipt);
        var statusJson = JsonSerializer.Serialize(status);

        Assert.Equal(("joined", "join-correlation"), (receipt.Code, receipt.Correlation));
        Assert.Equal("PendingApproval", status.SessionMode);
        Assert.Null(status.ConnectionId);
        Assert.True(status.SnapshotRevision > 0);
        Assert.DoesNotContain("guestToken", receiptJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invitationProof", receiptJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"connectionId\":null", statusJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidJoinRequests))]
    public async Task InvalidJoinRequestReturnsCurrentCodeWithoutCreatingOrChangingGuestAuthority(
        string caseName, string rejectionCode)
    {
        await using var fixture = await GuestFixture.CreateAsync();
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        if (caseName != "missing-stage")
            fixture.Registry.StageGuest("guest");
        if (caseName == "duplicate-connection")
            fixture.Guests.Join("guest", new("first", invitation.SessionId, InvitationProof(invitation)));

        var before = fixture.Authority.Read();
        GuestJoinRequest request = caseName switch
        {
            "malformed-correlation" => new(" ", invitation.SessionId, InvitationProof(invitation)),
            "invalid-invitation" => new("join", invitation.SessionId, "not-a-valid-invitation-proof"),
            _ => new("join", invitation.SessionId, InvitationProof(invitation))
        };

        var error = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Join("guest", request));

        Assert.Equal(rejectionCode, error.Code);
        var after = fixture.Authority.Read();
        Assert.Equal(before.Precondition, after.Precondition);
        Assert.Equal(before.SnapshotRevision, after.SnapshotRevision);
        Assert.Equal(before.Snapshot.AckSeq, after.Snapshot.AckSeq);
        Assert.Equal(before.Snapshot.Desired, after.Snapshot.Desired);
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task ASecondStagedConnectionCannotReuseAnAlreadyBoundAdmission()
    {
        await using var fixture = await GuestFixture.CreateAsync();
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        fixture.Registry.StageGuest("first");
        fixture.Registry.StageGuest("second");
        fixture.Guests.Join("first", new("first", invitation.SessionId, InvitationProof(invitation)));
        var before = fixture.Authority.Read();

        var error = Assert.Throws<ControlRejectedException>(() =>
            fixture.Guests.Join("second", new("second", invitation.SessionId, InvitationProof(invitation))));

        Assert.Equal("session-conflict", error.Code);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.True(fixture.Registry.IsGuestStaged("second"));
        Assert.False(fixture.Registry.TryGetGuest("second", out _));
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task PreJoinInputAndSecondConnectionCannotReachTheJoinedGuestAuthority()
    {
        await using var fixture = await GuestFixture.CreateAsync();
        fixture.Registry.StageGuest("joined");
        fixture.Registry.StageGuest("other");
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        fixture.Guests.Join("joined", new("join", invitation.SessionId, InvitationProof(invitation)));
        var before = fixture.Authority.Read();
        var request = new GuestInputRequest("input", new(2, "epoch", 1, "grant", 1, true,
            new(0, 10, 10, 20, 50)));

        var preJoin = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Submit("unbound", request));
        var otherConnection = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Submit("other", request));

        Assert.Equal("guest-not-joined", preJoin.Code);
        Assert.Equal("guest-not-joined", otherConnection.Code);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task InvalidGuestInputReturnsExactCodeBeforePublicationOrBridgeWork(
        string caseName, string rejectionCode)
    {
        await using var fixture = await GuestFixture.CreateAsync();
        var status = PrepareArmedGuest(fixture);
        var request = new GuestInputRequest(
            caseName == "correlation" ? " " : "input",
            BuildInput(status, caseName));
        var before = fixture.Authority.Read();

        var error = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Submit("guest", request));

        Assert.Equal(rejectionCode, error.Code);
        var after = fixture.Authority.Read();
        Assert.Equal(before.Precondition, after.Precondition);
        Assert.Equal(before.SnapshotRevision, after.SnapshotRevision);
        Assert.Equal(before.Snapshot.AckSeq, after.Snapshot.AckSeq);
        Assert.Equal(before.Snapshot.Desired, after.Snapshot.Desired);
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task ValidGuestInputAcknowledgesAndAdvancesOnlyThePublicationRevision()
    {
        await using var fixture = await GuestFixture.CreateAsync();
        var status = PrepareArmedGuest(fixture);
        var before = fixture.Authority.Read();

        var acknowledgement = fixture.Guests.Submit("guest", new("input",
            BuildInput(status, "valid")));
        var after = fixture.Authority.Read();

        Assert.Equal(("accepted", "input"), (acknowledgement.Code, acknowledgement.Correlation));
        Assert.Equal(before.Precondition.AuthorityRevision, after.Precondition.AuthorityRevision);
        Assert.Equal(before.SnapshotRevision + 1, after.SnapshotRevision);
        Assert.Equal(1, after.Snapshot.AckSeq);
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Theory]
    [InlineData("correlation", "invalid-correlation")]
    [InlineData("version", "invalid-input")]
    public async Task BoundTransportInvalidInputsConsumeRollingCapacity(string caseName, string rejectionCode)
    {
        await using var fixture = await GuestFixture.CreateAsync();
        var status = PrepareArmedGuest(fixture);
        var request = new GuestInputRequest(
            caseName == "correlation" ? " " : "input",
            BuildInput(status, caseName));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var error = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Submit("guest", request));
            Assert.Equal(rejectionCode, error.Code);
        }

        var limited = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Submit("guest", request));

        Assert.Equal("rate-limited", limited.Code);
        Assert.Equal(0, fixture.Authority.Read().Snapshot.AckSeq);
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task GuestStopLeaveAndTransportLossRevokeOnlyTheBoundConnectionExactlyOnce()
    {
        await using var fixture = await GuestFixture.CreateAsync();
        fixture.Registry.StageGuest("guest");
        fixture.Registry.StageGuest("other");
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        fixture.Guests.Join("guest", new("join", invitation.SessionId, InvitationProof(invitation)));
        var joined = fixture.Authority.Read();

        var stopped = fixture.Guests.Stop("guest", "stop");
        var afterStop = fixture.Authority.Read();
        fixture.Guests.TransportLost("guest");
        var afterGuestLoss = fixture.Authority.Read();
        fixture.Guests.TransportLost("guest");

        Assert.Equal(("stopped", "stop"), (stopped.Code, stopped.Correlation));
        Assert.Equal("guest-stop", afterStop.Snapshot.Reason);
        Assert.Equal(joined.Precondition.AuthorityRevision, afterStop.Precondition.AuthorityRevision);
        Assert.Equal(joined.SnapshotRevision + 1, afterStop.SnapshotRevision);
        Assert.Equal("Ended", afterGuestLoss.Snapshot.SessionMode);
        Assert.Equal(afterStop.SnapshotRevision + 1, afterGuestLoss.SnapshotRevision);
        Assert.Equal(afterGuestLoss, fixture.Authority.Read());
        Assert.True(fixture.Registry.IsGuestStaged("other"));
        Assert.False(fixture.Registry.TryGetGuest("guest", out _));
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task LeaveRemovesTheGuestBindingAndFurtherCommandsAreRejectedWithoutASecondRevision()
    {
        await using var fixture = await GuestFixture.CreateAsync();
        fixture.Registry.StageGuest("guest");
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        fixture.Guests.Join("guest", new("join", invitation.SessionId, InvitationProof(invitation)));

        var acknowledgement = fixture.Guests.Leave("guest", "leave");
        var afterLeave = fixture.Authority.Read();
        var error = Assert.Throws<ControlRejectedException>(() => fixture.Guests.Stop("guest", "after-leave"));

        Assert.Equal(("ended", "leave"), (acknowledgement.Code, acknowledgement.Correlation));
        Assert.Equal("Ended", afterLeave.Snapshot.SessionMode);
        Assert.Equal("guest-not-joined", error.Code);
        Assert.Equal(afterLeave, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    private static GuestStatus PrepareArmedGuest(GuestFixture fixture)
    {
        fixture.Authority.Recover(fixture.Authority.Read().Precondition);
        fixture.Registry.StageGuest("guest");
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        fixture.Guests.Join("guest", new("join", invitation.SessionId, InvitationProof(invitation)));
        var pending = fixture.Authority.Read();
        fixture.Authority.Approve(pending.Precondition, pending.Snapshot.ConnectionId!);
        fixture.Authority.Arm(fixture.Authority.Read().Precondition);
        fixture.Bridge.ClearCalls();
        return fixture.Guests.Status("guest");
    }

    private static GuestInput BuildInput(GuestStatus status, string caseName)
    {
        var grant = Assert.IsType<GuestGrant>(status.Grant);
        return caseName switch
        {
            "version" => new(1, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 10, 10, 20, 50)),
            "grant" => new(2, grant.Epoch, grant.PolicyVersion, "other-grant", 1, true, new(0, 10, 10, 20, 50)),
            "policy" => new(2, grant.Epoch, grant.PolicyVersion + 1, grant.Id, 1, true, new(0, 10, 10, 20, 50)),
            "desired-values" => new(2, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 10, 30, 20, 50)),
            _ => new(2, grant.Epoch, grant.PolicyVersion, grant.Id, 1, true, new(0, 10, 10, 20, 50))
        };
    }

    private static string InvitationProof(Invitation invitation) =>
        new Uri(invitation.Url).Fragment["#invite=".Length..];

    /// <summary>
    /// Owns a connected device, authority, and staged-connection registry for guest operation tests.
    /// </summary>
    private sealed class GuestFixture : IAsyncDisposable
    {
        private GuestFixture()
        {
            Bridge = new FakeBridge();
            Engine = new PrototypeEngine(Bridge, TimeProvider.System);
            Bridge.Connected = true;
            Bridge.Publish();
            Authority = new ControlAuthority(Engine, TimeProvider.System);
            Registry = new ControlHubConnectionRegistry();
            Guests = new GuestOperationsHarness(new GuestControlOperations(Authority), Registry);
        }

        internal FakeBridge Bridge { get; }
        internal PrototypeEngine Engine { get; }
        internal ControlAuthority Authority { get; }
        internal ControlHubConnectionRegistry Registry { get; }
        internal GuestOperationsHarness Guests { get; }

        internal static Task<GuestFixture> CreateAsync() => Task.FromResult(new GuestFixture());

        public async ValueTask DisposeAsync()
        {
            await Authority.DisposeAsync();
            await Bridge.DisposeAsync();
        }
    }
}
