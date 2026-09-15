using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Web.ControlCore.Tests;

public sealed class LimitsPolicyTests
{
    [Theory]
    [InlineData(25, 80)]
    [InlineData(100, 100)]
    public async Task CompatiblePolicyChangePreservesAcceptedMotionAndPublishesFreshGrant(int speed, int depth)
    {
        await using var rig = new Rig();
        rig.Arm();
        var input = rig.Input(1);
        rig.Submit(input);
        rig.Pump();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(speed, depth));

        var after = rig.Engine.Snapshot;
        Assert.Equal("Ready", after.Mode);
        Assert.Equal(before.Reason, after.Reason);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.AckSeq, after.AckSeq);
        Assert.Equal(before.VerifiedSeq, after.VerifiedSeq);
        Assert.Equal(before.Desired, after.Desired);
        Assert.Equal(before.Current, after.Current);
        Assert.Equal("Approved", after.SessionMode);
        Assert.Equal(before.ConnectionId, after.ConnectionId);
        Assert.Equal(new ControlLimits(speed, depth), after.Limits);
        Assert.Equal(before.PolicyVersion + 1, after.PolicyVersion);
        Assert.Equal(input.Epoch, after.Grant!.Epoch);
        Assert.Equal(after.PolicyVersion, after.Grant.PolicyVersion);
        Assert.NotEqual(input.GrantId, after.Grant.Id);
        rig.Pump();
        Assert.Single(rig.Bridge.Calls);
        Assert.True(rig.Engine.Snapshot.DeviceEnabled);
    }

    [Fact]
    public async Task ReapplyingSameLimitsIsANoopIncludingGrantAndSequence()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(50, 100));

        Assert.Equal(before, rig.Engine.Snapshot);
        rig.Pump();
        Assert.Single(rig.Bridge.Calls);
    }

    [Fact]
    public async Task OldPolicyAndSupersededGrantRejectWithoutChangingAcceptedIntent()
    {
        await using var rig = new Rig();
        rig.Arm();
        var old = rig.Input(1);
        rig.Submit(old);
        rig.Engine.SetLimits(new(25, 80));
        var before = rig.Engine.Snapshot;

        Assert.Equal("policy-invalid", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(old with { Seq = 2 })).Code);
        Assert.Equal("grant-invalid", Assert.Throws<ControlRejectedException>(() =>
            rig.Submit(old with { Seq = 2, PolicyVersion = before.PolicyVersion })).Code);
        Assert.Equal(before, rig.Engine.Snapshot);

        rig.Submit(rig.Input(2));
        rig.Pump();
        Assert.Equal(2, rig.Engine.Snapshot.AckSeq);
        Assert.Equal(2, rig.Engine.Snapshot.VerifiedSeq);
        Assert.Equal(old.Desired, Assert.Single(rig.Bridge.Calls).Desired);
    }

    [Fact]
    public async Task ApplyAndStatusDoNotRenewTheOriginalAcceptedLease()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Advance(500);
        rig.Engine.SetLimits(new(25, 80));
        var fresh = rig.Engine.Snapshot.Grant!;
        Assert.Equal(750, fresh.ValidForMs);
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Advance(249);
        Assert.Equal("Ready", rig.Engine.GuestSnapshot(
            rig.Guest.SessionId, rig.Guest.GuestToken, rig.Guest.ConnectionId).Mode);

        rig.Advance(1);
        rig.Pump();

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal("grant-expired", rig.Engine.Snapshot.Reason);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.False(rig.Engine.Snapshot.DeviceEnabled);
        Assert.Equal(new[] { "Write", "Stop" }, rig.Bridge.Calls.Select(call => call.Kind));
    }

    [Fact]
    public async Task ExplicitGuestInputCanRenewWithFreshPolicyGrantButCannotOutliveItsDeadline()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Advance(500);
        rig.Engine.SetLimits(new(25, 80));
        rig.Submit(rig.Input(2));
        rig.Pump();

        rig.Advance(749);
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Single(rig.Bridge.Calls);
        rig.Advance(1);
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal("grant-expired", rig.Engine.Snapshot.Reason);
    }

    [Fact]
    public async Task ApplyAfterMissedLeaseDeadlineStopsInsteadOfRevivingAuthority()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        rig.Bridge.Publish();

        rig.Engine.SetLimits(new(25, 80));

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal("grant-expired", rig.Engine.Snapshot.Reason);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.True(rig.Engine.Snapshot.OperationPending);
        rig.Pump();
        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
    }

    [Theory]
    [InlineData(9, 100)]
    [InlineData(50, 19)]
    public async Task ConflictingPendingInputRejectsEntirePolicyWithoutClearingIt(int speed, int depth)
    {
        await using var rig = new Rig();
        rig.Arm();
        var input = rig.Input(1);
        rig.Submit(input);
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(speed, depth))).Code);

        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
        rig.Pump();
        Assert.Equal(input.Desired, Assert.Single(rig.Bridge.Calls).Desired);
    }

    [Fact]
    public async Task ConflictingObservedMotionRejectsEvenWhenReplacementInputFits()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Submit(rig.Input(1, DesiredState.Stopped with { Speed = 40 }));
        rig.Pump();
        rig.Submit(rig.Input(2));
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(20, 100))).Code);

        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Single(rig.Bridge.Calls);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("write")]
    [InlineData("stop")]
    public async Task CompletedMotionAwaitingFeedbackStillConflictsWithLowerLimits(string replacement)
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.PublishOnCompletion = false;
        var issued = rig.Input(1, DesiredState.Stopped with { Speed = 40 });
        rig.Submit(issued);
        rig.Pump();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(2));
        if (replacement == "stop") rig.Engine.Pause();
        if (replacement != "pending") rig.Pump();
        var before = rig.Engine.Snapshot;
        Assert.Equal(replacement != "pending", before.OperationPending);
        Assert.Equal(0, before.Current!.Speed);

        Assert.Equal("limits-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(20, 100))).Code);

        Assert.Equal(before, rig.Engine.Snapshot);
        if (replacement == "pending")
        {
            rig.Bridge.Publish();
            Assert.Equal(issued.Desired, rig.Engine.Snapshot.Current);
            Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedFeedbackClearsSupersededWriteBoundsWithoutArming(bool stop)
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.PublishOnCompletion = false;
        rig.Submit(rig.Input(1, DesiredState.Stopped with { Speed = 40 }));
        rig.Pump();
        if (stop) rig.Engine.Pause();
        else rig.Submit(rig.Input(2));
        rig.Pump();
        Assert.Equal("limits-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(20, 100))).Code);
        rig.Bridge.Publish();
        rig.Pump();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(20, 100));

        Assert.Equal(stop ? "Paused" : "Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(before.Generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(before.Current, rig.Engine.Snapshot.Current);
        Assert.Equal(new ControlLimits(20, 100), rig.Engine.Snapshot.Limits);
        if (stop) Assert.Null(rig.Engine.Snapshot.Grant);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingInFlightWriteRejectsEvenWhenPendingInputFitsOrStopIsLatched(bool stop)
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1, DesiredState.Stopped with { Speed = 40 }));
        rig.Pump();
        rig.Submit(rig.Input(2));
        if (stop) rig.Engine.Pause();
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(20, 100))).Code);

        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(stop ? "Stop" : "Write", rig.Bridge.Calls[1].Kind);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task ConflictingZeroEnvelopeWriteRejectsBeforeItCanCompleteUnderNewBounds()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1, new(0, 10, 15, 60, 50)));
        rig.Pump();
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(new DesiredState(0, 0, 15, 60, 50), rig.Bridge.Calls[1].Desired);
        rig.Submit(rig.Input(2, new(0, 10, 15, 40, 50)));
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict", Assert.Throws<ControlRejectedException>(() =>
            rig.Engine.SetLimits(new(25, 50))).Code);

        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task CompatibleChangePreservesSerializedInFlightAndPendingWrites()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var first = rig.Input(1);
        rig.Submit(first);
        rig.Pump();
        var second = rig.Input(2, DesiredState.Stopped with { Speed = 15 });
        rig.Submit(second);
        var generation = rig.Engine.Snapshot.Generation;

        rig.Engine.SetLimits(new(25, 80));
        rig.Pump();

        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(first.Epoch, rig.Engine.Snapshot.Grant!.Epoch);
        Assert.True(rig.Engine.Snapshot.OperationPending);
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(second.Desired, rig.Bridge.Calls[1].Desired);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(second.Desired, rig.Engine.Snapshot.Current);
        Assert.Equal(2, rig.Engine.Snapshot.VerifiedSeq);
        Assert.Equal(new[] { "Write", "Write" }, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task CompatibleChangeDuringZeroWriteKeepsTheStoppedEnvelopeTransition()
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var input = rig.Input(1, new(0, 20, 15, 40, 50));
        rig.Submit(input);
        rig.Pump();
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(0, rig.Bridge.Calls[1].Desired!.Speed);
        var generation = rig.Engine.Snapshot.Generation;

        rig.Engine.SetLimits(new(25, 50));
        rig.Pump();

        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal(generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(input.Epoch, rig.Engine.Snapshot.Grant!.Epoch);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(input.Desired, rig.Bridge.Calls[2].Desired);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(input.Desired, rig.Engine.Snapshot.Current);
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(new[] { "Stop", "Write", "Write" }, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Theory]
    [InlineData("Initializing")]
    [InlineData("Paused")]
    [InlineData("Locked")]
    [InlineData("Faulted")]
    public async Task PolicyEditsNeverArmOrClearExistingLifecycleState(string mode)
    {
        await using var rig = new Rig();
        if (mode == "Paused") rig.Engine.Recover();
        if (mode == "Locked") rig.Bridge.External(DesiredState.Stopped with { Depth = 30 });
        if (mode == "Faulted")
        {
            rig.Bridge.Connected = false;
            rig.Bridge.Publish();
        }
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(25, 80));

        var after = rig.Engine.Snapshot;
        Assert.Equal(mode, after.Mode);
        Assert.Equal(before.Reason, after.Reason);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.LockRevision, after.LockRevision);
        Assert.Equal(before.OperationPending, after.OperationPending);
        Assert.Null(after.Grant);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrLockStillDominatesSafeInFlightWorkAfterPolicyUpdate(bool externalLock)
    {
        await using var rig = new Rig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(rig.Input(1));
        rig.Pump();
        rig.Submit(rig.Input(2, DesiredState.Stopped with { Speed = 15 }));
        if (externalLock) rig.Bridge.External(DesiredState.Stopped with { Depth = 30 });
        else rig.Engine.Pause();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(25, 80));
        rig.Bridge.CompleteNext();
        rig.Pump();

        Assert.Equal(before.Mode, rig.Engine.Snapshot.Mode);
        Assert.Equal(before.Generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(before.LockRevision, rig.Engine.Snapshot.LockRevision);
        Assert.Null(rig.Engine.Snapshot.Grant);
        Assert.Equal(new[] { "Write", "Stop" }, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(1, rig.Bridge.MaxActive);
    }
}
