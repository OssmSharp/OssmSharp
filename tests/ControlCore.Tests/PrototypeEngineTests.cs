using FastMoq;
using Microsoft.Extensions.Time.Testing;
using OSSMDotNet.DeviceControl;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.ControlCore.Tests;

public sealed class PrototypeEngineTests
{
    [Fact]
    public async Task DefaultLimitsAllowExplicitControlWithoutASeparateLimitsStep()
    {
        await using var rig = new Rig();
        Assert.Equal(new ControlLimits(100, 100), rig.Engine.Snapshot.Limits);
        Assert.Equal("Initializing", rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
        rig.Engine.Recover();
        rig.ConnectGuest();
        rig.Engine.Approve(rig.Guest.ConnectionId);
        rig.Engine.Arm();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.NotNull(rig.Engine.Snapshot.Grant);
    }

    [Fact]
    public async Task AcceptedInputIsNotVerifiedUntilTheDeviceConfirmsItsSettings()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var input = rig.Input(1);
        rig.Submit(input);
        var pending = rig.Engine.Snapshot;
        Assert.Equal(1, pending.AckSeq);
        Assert.Equal(0, pending.VerifiedSeq);
        Assert.NotNull(pending.Current);
        Assert.Equal(0, pending.Current!.Speed);
        rig.Pump();
        rig.Bridge.AutoComplete = true;
        rig.Bridge.CompleteAll();
        rig.Pump();
        var confirmed = rig.Engine.Snapshot;
        Assert.Equal(input.Desired, confirmed.Current);
        Assert.Equal(1, confirmed.VerifiedSeq);
    }

    [Fact]
    public async Task GuestReceivesLivePositionWithoutRawDeviceDetailsAndStaleFeedbackClearsIt()
    {
        await using var rig = new Rig();
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
        Assert.Null(stale.Velocity);
    }

    [Fact]
    public async Task SnapshotReportsQueuedAndIssuedIoUntilTheStopIsObserved()
    {
        await using var rig = new Rig();
        rig.Engine.Recover();
        rig.Bridge.AutoComplete = false;
        rig.Engine.Pause();
        Assert.True(rig.Engine.Snapshot.OperationPending);
        rig.Pump();
        Assert.True(rig.Engine.Snapshot.OperationPending);
        rig.Bridge.CompleteNext();
        Assert.True(rig.Engine.Snapshot.OperationPending);
        rig.Pump();
        Assert.False(rig.Engine.Snapshot.OperationPending);
    }

    [Fact]
    public async Task InvitationCompatibilityRetriesValidateOriginAndBindAttemptScopeUntilCycle()
    {
        await using var rig = new Rig();
        foreach (var origin in new[]
        {
            "http://example.com", "https://example.com/path", "https://user@example.com",
            "https://example.com/?query=1", "https://example.com/#fragment", "ftp://localhost",
            "http://127.0.0.2", "http://localhost.evil.test", "https://example.com/../"
        })
        {
            Rejected(() => rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri(origin), "operator"));
        }

        Rejected(() => rig.Engine.CreateInvitation(Guid.Empty, new Uri("https://example.com"), "operator"));
        var attempt = Guid.NewGuid();
        var invitation = rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator");
        Assert.Equal(invitation, rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator"));
        Assert.Equal(invitation, rig.Engine.CreateInvitation(attempt, new Uri("https://different.example"), "operator"));
        Assert.Equal("https://example.com:8443", new Uri(invitation.Url).GetLeftPart(UriPartial.Authority));
        Assert.Equal($"https://example.com:8443/join/{invitation.SessionId}#invite={Secret(invitation)}", invitation.Url);
        Assert.Equal(43, Secret(invitation).Length);
        Assert.False(invitation.LocalOnly);
        Rejected(() => rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "other-operator"));
        Assert.Equal(invitation, rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri("https://example.com"), "operator"));
        Assert.Null(invitation.ExpiresAt);

        rig.Engine.Join(invitation.SessionId, Secret(invitation), "connection");
        Assert.Equal(invitation, rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator"));
        rig.Engine.EndSession();
        Assert.Equal(invitation, rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator"));
        var replacement = rig.Engine.CycleInvitation(new Uri("https://example.com:8443"));
        Rejected(() => rig.Engine.CreateInvitation(attempt, new Uri("https://example.com:8443"), "operator"), "attempt-consumed");
        Assert.Equal(replacement, rig.Engine.GetInvitation(new Uri("https://example.com:8443")));
    }

    [Fact]
    public async Task ExactLoopbackOriginsAllowHttp()
    {
        foreach (var origin in new[] { "http://localhost:8080", "http://127.0.0.1:80", "http://[::1]:8080" })
        {
            await using var rig = new Rig();
            Assert.True(rig.Engine.CreateInvitation(Guid.NewGuid(), new Uri(origin), "operator").LocalOnly);
        }
    }

    [Fact]
    public async Task WrongProofOrConnectionCannotReadStopLeaveOrRenewController()
    {
        await using var rig = new Rig();
        rig.Arm();
        var before = rig.Engine.Snapshot;
        var motion = rig.Input(1);
        Rejected(() => rig.Engine.Submit(rig.Guest.SessionId, "wrong-secret", rig.Guest.ConnectionId, motion), "guest-unauthorized");
        Rejected(() => rig.Engine.GuestSnapshot(rig.Guest.SessionId, rig.Guest.GuestToken, "other"), "guest-unauthorized");
        Rejected(() => rig.Engine.GuestStop(rig.Guest.SessionId, "wrong-secret", rig.Guest.ConnectionId), "guest-unauthorized");
        Rejected(() => rig.Engine.Leave("other", rig.Guest.GuestToken, rig.Guest.ConnectionId), "guest-unauthorized");
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task MalformedProofsRejectWithoutChangingAuthority()
    {
        await using var rig = new Rig();
        rig.Arm();
        var before = rig.Engine.Snapshot;
        foreach (var proof in new[]
        {
            string.Empty, new string('a', 42), new string('a', 44),
            new string('é', 43), new string('=', 43), new string('a', 42) + "+",
            new string('a', 42) + "/", new string('a', 42) + "\0"
        })
        {
            Rejected(() => rig.Engine.GuestSnapshot(rig.Guest.SessionId, proof, rig.Guest.ConnectionId),
                "guest-unauthorized");
        }
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task ConcurrentJoinsReserveExactlyOneConnection()
    {
        await using var rig = new Rig();
        var invitation = rig.Invite();
        var results = await Task.WhenAll(Enumerable.Range(1, 32).Select(i => Task.Run(() =>
        {
            try
            {
                return rig.Engine.Join(invitation.SessionId, Secret(invitation), $"connection-{i}");
            }
            catch (ControlRejectedException)
            {
                return null;
            }
        })));
        var winner = Assert.Single(results, result => result is not null)!;
        Assert.Equal(winner.ConnectionId, rig.Engine.Snapshot.ConnectionId);
        Rejected(() => rig.Engine.Join(invitation.SessionId, Secret(invitation), winner.ConnectionId));
    }

    [Fact]
    public async Task InvitationDoesNotExpireButPendingApprovalStillExpiresAtItsExactDeadline()
    {
        await using var rig = new Rig();
        var invitation = rig.Invite();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        rig.Engine.Join(invitation.SessionId, Secret(invitation), "pending");
        rig.Time.Advance(TimeSpan.FromSeconds(60));
        Rejected(() => rig.Engine.Approve("pending"));
        Assert.NotEqual("Approved", rig.Engine.Snapshot.SessionMode);
    }

    [Fact]
    public async Task ArmRequiresApprovalFreshStoppedSimulatorAndPositiveLocalLimits()
    {
        await using var rig = new Rig();
        Assert.Equal("Initializing", rig.Engine.Snapshot.Mode);
        rig.Engine.Recover();
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal(100, rig.Engine.Snapshot.Limits.SpeedCap);
        Rejected(rig.Engine.Arm);
        rig.ConnectGuest();
        Rejected(rig.Engine.Arm);
        rig.Engine.SetLimits(new(0, 100));
        rig.Pump();
        rig.Engine.Approve(rig.Guest.ConnectionId);
        Rejected(rig.Engine.Arm, "limits-not-configured");
        rig.Engine.SetLimits(new(50, 100));
        rig.Pump();
        rig.Engine.Arm();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
        Assert.NotNull(rig.Engine.Snapshot.Grant);
    }

    [Fact]
    public async Task GrantPublishesImmediatelyThenEvery250MillisecondsWithoutExtendingOldGrant()
    {
        await using var rig = new Rig();
        rig.Arm();
        var first = rig.Engine.Snapshot.Grant!;
        var input = rig.Input(1);
        rig.Advance(249);
        Assert.Equal(first.Id, rig.Engine.Snapshot.Grant!.Id);
        rig.Advance(1);
        Assert.NotEqual(first.Id, rig.Engine.Snapshot.Grant!.Id);
        rig.Advance(499);
        rig.Submit(input);
        Assert.Equal(1L, rig.Engine.Snapshot.AckSeq);
        rig.Advance(1);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Rejected(() => rig.Submit(input with { Seq = 2 }));
    }

    [Fact]
    public async Task MotionRejectsVersionEpochPolicyGrantSequenceAndMissingRunWithoutRenewal()
    {
        await using var rig = new Rig();
        rig.Arm();
        var valid = rig.Input(1);
        foreach (var invalid in new[]
        {
            valid with { V = 1 }, valid with { Epoch = "old" }, valid with { PolicyVersion = -1 },
            valid with { GrantId = "unknown" }, valid with { Seq = 0 },
            valid with { Seq = 9007199254740992 }, valid with { Run = false }
        })
        {
            Rejected(() => rig.Submit(invalid));
        }
        Assert.Equal(0L, rig.Engine.Snapshot.AckSeq);
        Assert.Empty(rig.Bridge.Calls);
        rig.Submit(valid);
        Rejected(() => rig.Submit(valid));
        rig.Advance(750);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task RateBucketHasCapacity20Refills10PerSecondAndDropsNeverRenewLease()
    {
        await using var rig = new Rig();
        rig.Arm();
        var first = rig.Input(1);
        for (var seq = 1; seq <= 20; seq++)
            rig.Submit(first with { Seq = seq });
        Rejected(() => rig.Submit(first with { Seq = 21 }), "rate-limited");
        Assert.Equal(20L, rig.Engine.Snapshot.AckSeq);
        rig.Advance(100);
        rig.Submit(first with { Seq = 21 });
        Assert.Equal(21L, rig.Engine.Snapshot.AckSeq);
        rig.Advance(650);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task PolicyChangePreservesReadyAndEpochButRejectsTheOldPolicy()
    {
        await using var rig = new Rig();
        rig.Arm();
        var old = rig.Input(1);
        var generation = rig.Engine.Snapshot.Generation;
        rig.Engine.SetLimits(new(25, 80));
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(old.Epoch, rig.Engine.Snapshot.Grant!.Epoch);
        Assert.Equal(old.PolicyVersion + 1, rig.Engine.Snapshot.PolicyVersion);
        rig.Pump();
        Rejected(() => rig.Submit(old), "policy-invalid");
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task InvalidFullTuplesNeverWrite()
    {
        await using var rig = new Rig();
        rig.Arm();
        foreach (var invalid in new[]
        {
            new DesiredState(1, 10, 10, 20, 50), new DesiredState(0, 10, 10, 20, 49),
            new DesiredState(0, 51, 10, 20, 50), new DesiredState(0, 10, 0, 20, 50),
            new DesiredState(0, 10, 21, 20, 50), new DesiredState(0, -1, 10, 20, 50),
            new DesiredState(0, 10, 10, 101, 50)
        })
        {
            Rejected(() => rig.Submit(rig.Input(1, invalid)));
        }
        rig.Pump();
        Assert.Empty(rig.Bridge.Calls);
        Assert.Equal(0L, rig.Engine.Snapshot.AckSeq);
    }

    [Fact]
    public async Task PauseBetweenDelayedWritesClearsReplacementAndDispatchesOnlyExplicitStop()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Engine.Tick();
        Assert.Single(rig.Bridge.Calls);
        rig.Submit(rig.Input(2, DesiredState.Stopped with { Speed = 20 }));
        rig.Engine.Pause();
        rig.Engine.Pause();
        rig.Engine.Tick();
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        Assert.Equal(2, rig.Bridge.Calls.Count);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task WriteTimeoutRetainsActualWriterUntilItSettlesThenAllowsOnlyStop()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Engine.Tick();
        rig.Advance(1000);
        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.Equal("bridge-operation-timeout", rig.Engine.Snapshot.Reason);
        Assert.Single(rig.Bridge.Calls);
        rig.Advance(1000);
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task ExternalOverrideDuringInFlightWriteLocksAndUnlockOnlyReturnsPaused()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Engine.Tick();
        rig.Bridge.External(DesiredState.Stopped with { Depth = 30 });
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.Equal("external-parameter-change", rig.Engine.Snapshot.Reason);
        var revision = rig.Engine.Snapshot.LockRevision;
        Rejected(() => rig.Engine.Unlock(revision));
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        rig.Engine.Unlock(rig.Engine.Snapshot.LockRevision);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
    }

    [Fact]
    public async Task NewExternalRevisionMakesConcurrentStaleUnlockFailClosed()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.External(DesiredState.Stopped with { Depth = 30 });
        rig.Pump();
        var old = rig.Engine.Snapshot.LockRevision;
        using var released = new ManualResetEventSlim();
        var unlock = Task.Run(() =>
        {
            released.Wait();
            return Rejected(() => rig.Engine.Unlock(old));
        });
        rig.Bridge.External(DesiredState.Stopped with { Depth = 40 });
        released.Set();
        await unlock;
        Assert.True(rig.Engine.Snapshot.LockRevision > old);
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task ReconnectSessionReplacementRecoveryAndPolicyEditsNeverClearExternalLock()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.External(DesiredState.Stopped with { Depth = 30 });
        rig.Pump();
        rig.Engine.EndSession();
        rig.Invite();
        rig.Engine.SetLimits(new(30, 100));
        rig.Engine.Pause();
        rig.Bridge.Connected = false;
        rig.Bridge.Publish();
        rig.Bridge.Connected = true;
        rig.Bridge.Publish();
        rig.Pump();
        Rejected(rig.Engine.Recover);
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.True(rig.Engine.Snapshot.LockRevision > 0);
    }

    [Fact]
    public async Task RepeatedSamplesDoNotRenewFreshnessButAdvancingSamplesDo()
    {
        await using var rig = new Rig();
        rig.Engine.Recover();
        rig.Bridge.AutoComplete = false;
        rig.Time.Advance(TimeSpan.FromMilliseconds(1499));
        rig.Bridge.Publish(advanceSequence: false);
        rig.Engine.Tick();
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        rig.Bridge.Publish(advanceSequence: false);
        rig.Engine.Tick();
        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.False(rig.Engine.Snapshot.BridgeReady);
        rig.Bridge.CompleteNext();
        rig.Bridge.AutoComplete = true;
        rig.Bridge.Publish();
        rig.Pump();
        rig.Engine.Recover();
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task PositionAndVelocityDoNotLockButUnownedDesiredChangeDoes()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.Position = 99;
        rig.Bridge.Velocity = 18;
        rig.Bridge.Publish();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Bridge.External(DesiredState.Stopped with { Depth = 21 });
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
    }

    private static readonly string[] expected = new[] { "Stop", "Write", "Write" };

    [Fact]
    public async Task EnvelopeChangeStopsThenWritesZeroBeforeSpeedAndRechecksLeaseBetweenSteps()
    {
        await using var rig = new Rig();
        rig.Arm();
        var target = new DesiredState(0, 20, 15, 40, 50);
        rig.Submit(rig.Input(1, target));
        rig.Pump();
        Assert.Equal(expected, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(target with { Speed = 0 }, rig.Bridge.Calls[1].Desired);
        Assert.Equal(target, rig.Bridge.Calls[2].Desired);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task RetractionIsNotPhysicallyStoppedAndStopFeedbackHasItsOwnBudget()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.RetractOnStop = true;
        rig.Submit(rig.Input(1, new DesiredState(0, 20, 15, 40, 50)));
        rig.Engine.Tick();
        rig.Engine.Tick();
        Assert.True(rig.Engine.Snapshot.DeviceMoving);
        Assert.Single(rig.Bridge.Calls);
        rig.Advance(1499);
        Assert.Single(rig.Bridge.Calls);
        rig.Advance(1);
        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.Equal("stop-feedback-timeout", rig.Engine.Snapshot.Reason);
        Assert.True(rig.Engine.Snapshot.DeviceMoving);
    }

    [Fact]
    public async Task DelayedTickCannotRestartTheStopFeedbackBudget()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.RetractOnStop = true;
        rig.Submit(rig.Input(1, new(0, 20, 15, 40, 50)));
        rig.Engine.Tick();
        rig.Advance(1500);
        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.Equal("stop-feedback-timeout", rig.Engine.Snapshot.Reason);
        Assert.True(rig.Engine.Snapshot.DeviceMoving);
    }

    [Fact]
    public async Task SameDesiredCoalescesWhileNewSequenceRenewsOnlyOriginalGrantDeadline()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        Assert.Single(rig.Bridge.Calls);
        rig.Advance(250);
        rig.Submit(rig.Input(2));
        rig.Pump();
        Assert.Single(rig.Bridge.Calls);
        rig.Advance(749);
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Advance(1);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task MissedTickCannotLetANewerGrantResurrectAnExpiredHold()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Advance(250);
        var newer = rig.Input(2);
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        rig.Bridge.Publish();
        Rejected(() => rig.Submit(newer));
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal(1L, rig.Engine.Snapshot.AckSeq);
    }

    [Fact]
    public async Task WallClockJumpsCannotChangeGrantDeadlines()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Clock.Offset = TimeSpan.FromDays(2);
        rig.Engine.Tick();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Advance(749);
        rig.Clock.Offset = TimeSpan.FromDays(-2);
        rig.Advance(1);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task AnOwnCommandIdWithTheWrongTupleStillLocks()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Engine.Tick();
        rig.Bridge.External(DesiredState.Stopped with { Depth = 22 }, rig.Bridge.Calls[0].CommandId);
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        Assert.Equal(1L, rig.Engine.Snapshot.LockRevision);
    }

    [Fact]
    public async Task FailedBridgeTaskFaultsWithASafeReasonAndOnlyQueuesStop()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Engine.Tick();
        rig.Bridge.FailNext(new IOException("sensitive bridge detail"));
        rig.Engine.Tick();
        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.Equal("bridge-operation-failed", rig.Engine.Snapshot.Reason);
        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task LeaseExpiryWhileZeroWriteIsPendingNeverDispatchesSpeed()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1, new(0, 20, 15, 40, 50)));
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal("Write", rig.Bridge.Calls[1].Kind);
        Assert.Equal(0, rig.Bridge.Calls[1].Desired!.Speed);
        rig.Advance(750);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal("Stop", rig.Bridge.Calls[2].Kind);
        Assert.DoesNotContain(rig.Bridge.Calls, call => call.Desired?.Speed > 0);
    }

    [Fact]
    public async Task TransportLossAndFixedSessionExpiryEndAuthority()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Engine.TransportLost("unrelated");
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Engine.TransportLost(rig.Guest.ConnectionId);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Rejected(() => rig.Submit(rig.InputWithoutGrant(1)));

        await using var expires = new Rig();
        expires.Arm();
        expires.Time.Advance(TimeSpan.FromMinutes(30));
        expires.Bridge.Publish();
        expires.Engine.Tick();
        Assert.Equal("Ended", expires.Engine.Snapshot.SessionMode);
        Assert.Equal("Paused", expires.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task UnsupportedDeviceProfileNeverQualifiesAndDisposalUnsubscribes()
    {
        var bridge = new FakeBridge { Profile = "native-device" };
        var engine = new PrototypeEngine(bridge, new FakeTimeProvider());
        bridge.Publish();
        Rejected(engine.Recover);
        Assert.False(engine.Snapshot.BridgeReady);
        Assert.Equal(1, bridge.SubscriberCount);
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        Assert.Equal(0, bridge.SubscriberCount);
    }

    [Fact]
    public async Task DisposalEndsTheInvitationAndDoesNotExposeItsCreationSecret()
    {
        await using var rig = new Rig();
        var invitation = rig.Invite();
        await rig.Engine.DisposeAsync();
        Assert.Equal("None", rig.Engine.Snapshot.SessionMode);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Rejected(() => rig.Engine.Join(invitation.SessionId, Secret(invitation), "after-dispose"), "engine-disposed");
    }

    private static ControlRejectedException Rejected(Action action, string? code = null)
    {
        var error = Assert.Throws<ControlRejectedException>(action);
        if (code is not null)
            Assert.Equal(code, error.Code);
        Assert.DoesNotContain("wrong-secret", error.Message, StringComparison.Ordinal);
        return error;
    }

    internal static string Secret(Invitation invitation)
    {
        var fragment = new Uri(invitation.Url).Fragment;
        Assert.StartsWith("#invite=", fragment, StringComparison.Ordinal);
        return fragment["#invite=".Length..];
    }

}
