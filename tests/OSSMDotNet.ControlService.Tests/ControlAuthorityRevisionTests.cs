using OSSMDotNet.ControlCore;
using OSSMDotNet.ControlCore.Tests;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Tests;

public sealed class ControlAuthorityRevisionTests
{
    private static readonly Uri GuestOrigin = new("http://127.0.0.1:5182");
    private static readonly string[] InvalidPreconditionKinds = ["missing", "service-instance", "authority-revision", "guest-admission"];

    public static IEnumerable<object?[]> RejectedOperatorActions =>
        from operation in sourceArray
        from invalidPrecondition in InvalidPreconditionKinds
        select new object?[]
        {
            operation,
            invalidPrecondition,
            invalidPrecondition switch
            {
                "missing" => "authority-precondition-required",
                "service-instance" => "service-instance-mismatch",
                "authority-revision" => "authority-revision-mismatch",
                _ => "guest-admission-mismatch"
            }
        };

    private static readonly string[] sourceArray = new[] { "approve", "limits", "recover", "arm", "allow", "end", "cycle", "unlock" };

    [Theory]
    [MemberData(nameof(RejectedOperatorActions))]
    public async Task EveryPreconditionedOperatorActionRejectsBeforeMutatingAuthorityOrBridge(
        string operation, string invalidPrecondition, string rejectionCode)
    {
        await using var fixture = await AuthorityFixture.CreateAsync();
        var before = fixture.Authority.Read();
        AuthorityPrecondition? invalid = invalidPrecondition switch
        {
            "missing" => null,
            "service-instance" => before.Precondition with { ServiceInstanceId = "other-service" },
            "authority-revision" => before.Precondition with
            {
                AuthorityRevision = before.Precondition.AuthorityRevision + 1
            },
            _ => before.Precondition with
            {
                GuestAdmission = before.Precondition.GuestAdmission with { SessionMode = "other-admission" }
            }
        };

        var error = Assert.Throws<ControlRejectedException>(() => Invoke(
            fixture.Authority, operation, invalid!, fixture.CurrentGuestConnectionId));

        Assert.Equal(rejectionCode, error.Code);
        Assert.Equal(before, fixture.Authority.Read());
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("limits")]
    [InlineData("recover")]
    [InlineData("arm")]
    [InlineData("allow")]
    [InlineData("end")]
    [InlineData("cycle")]
    [InlineData("unlock")]
    public async Task EveryAcceptedPreconditionedOperatorActionAdvancesAuthorityAndPublicationExactlyOnce(
        string operation)
    {
        await using var fixture = await AuthorityFixture.CreateAsync();
        var guestConnectionId = PrepareForAcceptedOperation(fixture, operation);
        var before = fixture.Authority.Read();
        fixture.Bridge.ClearCalls();

        var result = Invoke(fixture.Authority, operation, before.Precondition, guestConnectionId);
        var after = fixture.Authority.Read();

        Assert.Equal(before.Precondition.AuthorityRevision + 1, result.Precondition.AuthorityRevision);
        Assert.Equal(before.SnapshotRevision + 1, result.SnapshotRevision);
        Assert.Equal(result, after);
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task GuestLifecycleAdvancesPublicationButUnchangedTicksDoNot()
    {
        await using var fixture = await AuthorityFixture.CreateAsync();
        var initial = fixture.Authority.Read();
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);

        var joined = fixture.Authority.JoinGuest(
            invitation.SessionId,
            InvitationProof(invitation),
            "guest-connection");
        var afterJoin = fixture.Authority.Read();
        fixture.Authority.Tick();
        var afterTick = fixture.Authority.Read();
        fixture.Authority.GuestTransportLost(joined.ConnectionId);
        var afterLoss = fixture.Authority.Read();

        Assert.Equal(initial.Precondition.AuthorityRevision, afterJoin.Precondition.AuthorityRevision);
        Assert.Equal(initial.SnapshotRevision + 1, afterJoin.SnapshotRevision);
        Assert.Equal("PendingApproval", afterJoin.Snapshot.SessionMode);
        Assert.Equal(afterJoin.Precondition.AuthorityRevision, afterTick.Precondition.AuthorityRevision);
        Assert.Equal(afterJoin, afterTick);
        Assert.Equal(afterTick.Precondition.AuthorityRevision, afterLoss.Precondition.AuthorityRevision);
        Assert.Equal(afterJoin.SnapshotRevision + 1, afterLoss.SnapshotRevision);
        Assert.Equal("Ended", afterLoss.Snapshot.SessionMode);
        Assert.Empty(fixture.Bridge.Calls);
    }

    [Fact]
    public async Task StopAndShutdownRemainAvailableWithoutPreconditionsAndPublishTheirRevisions()
    {
        await using var fixture = await AuthorityFixture.CreateAsync();
        var initial = fixture.Authority.Read();

        var stopped = fixture.Authority.Stop();
        fixture.Authority.StopForShutdown();
        var shutdown = fixture.Authority.Read();

        Assert.Equal(initial.Precondition.AuthorityRevision + 1, stopped.Precondition.AuthorityRevision);
        Assert.Equal(initial.SnapshotRevision + 1, stopped.SnapshotRevision);
        Assert.Equal(stopped.Precondition.AuthorityRevision + 1, shutdown.Precondition.AuthorityRevision);
        Assert.Equal(stopped.SnapshotRevision + 1, shutdown.SnapshotRevision);
        Assert.Equal("Paused", shutdown.Snapshot.Mode);
        Assert.Equal("control-service-stopping", shutdown.Snapshot.Reason);
        Assert.Empty(fixture.Bridge.Calls);
    }

    private static ControlAuthoritySnapshot Invoke(
        ControlAuthority authority, string operation, AuthorityPrecondition expected, string guestConnectionId) =>
        operation switch
        {
            "approve" => authority.Approve(expected, guestConnectionId),
            "limits" => authority.ApplySessionLimits(expected, new(99, 99)),
            "recover" => authority.Recover(expected),
            "arm" => authority.Arm(expected),
            "allow" => authority.AllowControl(expected),
            "end" => authority.End(expected, GuestOrigin),
            "cycle" => authority.CycleInvitation(expected, GuestOrigin),
            "unlock" => authority.Unlock(expected, authority.Read().Snapshot.LockRevision),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static string PrepareForAcceptedOperation(AuthorityFixture fixture, string operation)
    {
        if (operation is "approve" or "allow")
            return JoinPending(fixture).ConnectionId;

        if (operation == "arm")
        {
            fixture.Authority.Recover(fixture.Authority.Read().Precondition);
            var joined = JoinPending(fixture);
            fixture.Authority.Approve(fixture.Authority.Read().Precondition, joined.ConnectionId);
            return joined.ConnectionId;
        }

        if (operation == "unlock")
        {
            fixture.Bridge.External(DesiredState.Stopped with { Depth = 21 });
            fixture.Engine.Tick();
            fixture.Engine.Tick();
            fixture.Bridge.Publish();
            fixture.Engine.Tick();
        }

        return "guest-connection";
    }

    private static JoinResult JoinPending(AuthorityFixture fixture)
    {
        var invitation = fixture.Authority.ReadInvitation(GuestOrigin);
        return fixture.Authority.JoinGuest(invitation.SessionId, InvitationProof(invitation), "guest-connection");
    }

    private static string InvitationProof(Invitation invitation) =>
        new Uri(invitation.Url).Fragment["#invite=".Length..];

    private sealed class AuthorityFixture : IAsyncDisposable
    {
        private AuthorityFixture()
        {
            Bridge = new FakeBridge();
            Engine = new PrototypeEngine(Bridge, TimeProvider.System);
            Bridge.Publish();
            Authority = new ControlAuthority(Engine);
        }

        internal FakeBridge Bridge { get; }
        internal PrototypeEngine Engine { get; }
        internal ControlAuthority Authority { get; }
        internal string CurrentGuestConnectionId => Authority.Read().Snapshot.ConnectionId ?? "guest-connection";

        internal static Task<AuthorityFixture> CreateAsync() => Task.FromResult(new AuthorityFixture());

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Bridge.DisposeAsync();
        }
    }
}
