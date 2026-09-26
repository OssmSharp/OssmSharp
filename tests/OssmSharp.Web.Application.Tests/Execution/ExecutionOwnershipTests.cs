using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Application.Tests.Execution;

/// <summary>
/// Checks device I/O ownership, confirmation, and stop priority during control execution.
/// </summary>
public sealed class ExecutionOwnershipTests
{
    private static readonly string[] EnvelopeCalls = ["Stop", "Stop", "Write"];

    [Fact]
    public async Task AcceptedInputIsNotConfirmedUntilTheDeviceReportsItsSettings()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var desired = TestMotion.Settings(speed: 10);

        rig.Submit(desired);

        Assert.False(rig.Engine.Snapshot.DesiredConfirmed);
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.True(rig.Engine.Snapshot.DesiredConfirmed);
        Assert.Equal(desired, rig.Engine.Snapshot.Current);
    }

    [Fact]
    public async Task EffectiveStockSpeedAndLivePositionConfirmRequestedControlIntent()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.ReportedSpeed = 10;
        rig.Bridge.Position = 12.5;

        rig.Submit(TestMotion.Settings(speed: 20));
        rig.Pump();

        Assert.Equal(10, rig.Engine.Snapshot.Current!.Speed);
        Assert.Equal(12.5, rig.Engine.Snapshot.Position);
        Assert.True(rig.Engine.Snapshot.DesiredConfirmed);
    }

    [Fact]
    public async Task SnapshotReportsQueuedAndIssuedIoUntilTheStopIsObserved()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();
        rig.Bridge.AutoComplete = false;

        rig.Engine.Pause();
        Assert.True(rig.Engine.Snapshot.OperationPending);
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        Assert.True(rig.Engine.Snapshot.OperationPending);
        rig.Engine.Tick();
        rig.Bridge.Publish();
        rig.Engine.Tick();

        Assert.False(rig.Engine.Snapshot.OperationPending);
    }

    [Fact]
    public async Task ArmRequiresFreshStoppedDeviceAndPositiveLimits()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();
        rig.Engine.SetLimits(new(0, 100));

        Assert.Equal("limits-not-configured", Assert.Throws<ControlRejectedException>(rig.Engine.Arm).Code);

        rig.Engine.SetLimits(new(50, 100));
        rig.Engine.Arm();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task ArmRejectsLogicallyTimedOutButUnsettledDeviceIo()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();
        rig.Bridge.AutoComplete = false;
        rig.Engine.Pause();
        rig.Engine.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Engine.Tick();

        Assert.Equal("operation-pending", Assert.Throws<ControlRejectedException>(rig.Engine.Arm).Code);
        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.True(rig.Engine.Snapshot.OperationPending);
        Assert.Single(rig.Bridge.Calls);
    }

    [Fact]
    public async Task ConcurrentStopAndArmAlwaysLeaveExecutionPaused()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();

        await Task.WhenAll(
            Task.Run(() =>
            {
                try { rig.Engine.Arm(); }
                catch (ControlRejectedException) { }
            }, TestContext.Current.CancellationToken),
            Task.Run(() => rig.Engine.Pause(), TestContext.Current.CancellationToken));

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
    }

    [Fact]
    public async Task SynchronousDeviceCallbackDoesNotHoldCoreLockAgainstStop()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        rig.Bridge.BeforeStatePublished = () =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TestContext.Current.CancellationToken);
        };
        rig.Submit(TestMotion.Settings(speed: 10));
        var dispatch = Task.Run(rig.Engine.Tick, TestContext.Current.CancellationToken);
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var stop = Task.Run(() => rig.Engine.Pause(), TestContext.Current.CancellationToken);
        await stop.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        releaseCallback.Set();
        await dispatch;

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task PauseBetweenDelayedWritesClearsReplacementAndDispatchesOnlyExplicitStop()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 10));
        rig.Engine.Tick();
        rig.Submit(TestMotion.Settings(speed: 20));

        rig.Engine.Pause();
        rig.Engine.Pause();
        rig.Engine.Tick();
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();

        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task WriteTimeoutRetainsActualWriterUntilItSettlesThenAllowsOnlyStop()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 10));
        rig.Engine.Tick();

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Engine.Tick();
        Assert.Equal("bridge-operation-timeout", rig.Engine.Snapshot.Reason);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Engine.Tick();
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();

        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task ExternalOverrideDuringInFlightWriteLocksAndUnlockOnlyReturnsPaused()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 10));
        rig.Engine.Tick();
        rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 30));

        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
        var revision = rig.Engine.Snapshot.LockRevision;
        Assert.Equal("operation-pending", Assert.Throws<ControlRejectedException>(() => rig.Engine.Unlock(revision)).Code);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Pump();
        rig.Engine.Unlock(rig.Engine.Snapshot.LockRevision);

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
        Assert.Equal(0, rig.Engine.Snapshot.Desired.Speed);
    }

    [Fact]
    public async Task NewExternalRevisionMakesConcurrentStaleUnlockFailClosed()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 30));
        rig.Pump();
        var oldRevision = rig.Engine.Snapshot.LockRevision;

        rig.Bridge.External(TestMotion.Settings(speed: 2, depth: 40));

        Assert.Equal("lock-revision-mismatch", Assert.Throws<ControlRejectedException>(() => rig.Engine.Unlock(oldRevision)).Code);
        Assert.True(rig.Engine.Snapshot.LockRevision > oldRevision);
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task RecoveryStopReconnectAndPolicyEditsNeverClearExternalLock()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 30));
        rig.Pump();

        rig.Engine.SetLimits(new(30, 100));
        rig.Engine.Pause();
        rig.Bridge.Connected = false;
        rig.Bridge.Connected = true;
        rig.Bridge.Publish();
        rig.Pump();

        Assert.Equal("external-lock", Assert.Throws<ControlRejectedException>(rig.Engine.Recover).Code);
        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task FreshStateEventsRenewReadinessButSilenceExpiresIt()
    {
        await using var rig = new CoreRig();
        rig.Engine.Recover();
        rig.Bridge.AutoComplete = false;
        rig.Time.Advance(TimeSpan.FromMilliseconds(1499));
        rig.Bridge.Publish();
        rig.Engine.Tick();
        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);

        rig.Time.Advance(TimeSpan.FromMilliseconds(1500));
        rig.Engine.Tick();

        Assert.Equal("Faulted", rig.Engine.Snapshot.Mode);
        Assert.False(rig.Engine.Snapshot.BridgeReady);
    }

    [Fact]
    public async Task StockProfileUsesFreshFeedbackWithoutClaimingSimulatorIdentity()
    {
        await using var rig = new CoreRig();
        rig.Bridge.Connected = false;
        rig.Bridge.Profile = "ossm-stock-1.0.34";
        rig.Bridge.Connected = true;
        rig.Bridge.Publish();

        Assert.True(rig.Engine.Snapshot.BridgeReady);
        Assert.False(rig.Engine.Snapshot.IsSimulator);
    }

    [Fact]
    public async Task ReconnectRequiresFreshStateAndDoesNotExposeThePriorObservation()
    {
        await using var rig = new CoreRig();
        Assert.NotNull(rig.Engine.Snapshot.Observed);

        rig.Bridge.Connected = false;
        rig.Bridge.Connected = true;

        Assert.False(rig.Engine.Snapshot.BridgeReady);
        Assert.Null(rig.Engine.Snapshot.Observed);
        Assert.Null(rig.Engine.Snapshot.Current);
        Assert.Null(rig.Engine.Snapshot.Position);
    }

    [Fact]
    public async Task PositionAndVelocityDoNotLockButUnownedDesiredChangeDoes()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.Position = 99;
        rig.Bridge.Velocity = 18;
        rig.Bridge.Publish();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);

        rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 21));

        Assert.Equal("Locked", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task EnvelopeChangeStopsThenWritesZeroBeforeSpeedAndRechecksLeaseBetweenSteps()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        var target = TestMotion.Settings(speed: 20, stroke: 15, depth: 40);

        rig.Submit(target);
        rig.Pump();

        Assert.Equal(EnvelopeCalls, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(target with { Speed = 0 }, rig.Bridge.Calls[1].Desired);
        Assert.Equal(target, rig.Bridge.Calls[2].Desired);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopFeedbackTimeoutUsesOneFixedBudget(bool delayedTick)
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.RetractOnStop = true;
        rig.Submit(TestMotion.Settings(speed: 20, stroke: 15, depth: 40));
        rig.Engine.Tick();
        if (!delayedTick)
            rig.Engine.Tick();

        rig.Time.Advance(TimeSpan.FromMilliseconds(1500));
        rig.Engine.Tick();

        Assert.Equal(("Faulted", "stop-feedback-timeout"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
        Assert.True(rig.Engine.Snapshot.DeviceMoving);
    }

    [Fact]
    public async Task SameDesiredCoalescesWithoutRenewingTheOriginalDeadline()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        var desired = TestMotion.Settings(speed: 10);
        var deadline = rig.Deadline(TimeSpan.FromMilliseconds(750));
        rig.Engine.Submit(desired, deadline);
        rig.Pump();
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        rig.Engine.Submit(desired, deadline);
        rig.Pump();
        Assert.Single(rig.Bridge.Calls);

        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        rig.Engine.Tick();

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task WallClockJumpsCannotChangeExecutionDeadlines()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Submit(TestMotion.Settings(speed: 10));
        rig.Pump();

        rig.Clock.Offset = TimeSpan.FromDays(2);
        rig.Engine.Tick();
        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        rig.Clock.Offset = TimeSpan.FromDays(-2);
        rig.Engine.Tick();

        Assert.Equal("Paused", rig.Engine.Snapshot.Mode);
    }

    [Fact]
    public async Task FailedBridgeTaskFaultsWithASafeReasonAndOnlyQueuesStop()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 10));
        rig.Engine.Tick();

        rig.Bridge.FailNext(new IOException("sensitive bridge detail"));
        rig.Engine.Tick();

        Assert.Equal(("Faulted", "bridge-operation-failed"),
            (rig.Engine.Snapshot.Mode, rig.Engine.Snapshot.Reason));
        Assert.Equal("Stop", rig.Bridge.Calls[1].Kind);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task LeaseExpiryWhileZeroWriteIsPendingNeverDispatchesSpeed()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 20, stroke: 15, depth: 40));
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(0, rig.Bridge.Calls[1].Desired.Speed);

        rig.Time.Advance(TimeSpan.FromMilliseconds(750));
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();

        Assert.DoesNotContain(rig.Bridge.Calls, call => call.Desired.Speed > 0);
    }

    [Fact]
    public async Task UnsupportedDeviceProfileNeverQualifiesAndDisposalUnsubscribes()
    {
        await using var bridge = new FakeBridge { Profile = "native-device" };
        var engine = new ControlEngine(bridge, new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        bridge.Connected = true;
        bridge.Publish();

        Assert.False(engine.Snapshot.IsSimulator);
        Assert.Equal(1, bridge.SubscriberCount);
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        Assert.Equal(0, bridge.SubscriberCount);
    }
}
