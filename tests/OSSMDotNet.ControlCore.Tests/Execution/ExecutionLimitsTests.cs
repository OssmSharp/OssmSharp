using OSSMDotNet.Models;

namespace OSSMDotNet.ControlCore.Tests.Execution;

/// <summary>
/// Checks that invalid or conflicting execution limits cannot change accepted motion.
/// </summary>
public sealed class ExecutionLimitsTests
{
    private static readonly string[] SerializedWrites = ["Write", "Write"];
    private static readonly string[] EnvelopeWrites = ["Stop", "Stop", "Write"];

    [Theory]
    [InlineData("null")]
    [InlineData("speed-below-minimum")]
    [InlineData("speed-above-maximum")]
    [InlineData("depth-below-minimum")]
    [InlineData("depth-above-maximum")]
    public async Task InvalidLimitTuplesRejectWithoutChangingStateOrReachingTheBridge(string boundary)
    {
        await using var rig = new CoreRig();
        var before = rig.Engine.Snapshot;
        ControlLimits? limits = boundary switch
        {
            "null" => null,
            "speed-below-minimum" => new(-1, 100),
            "speed-above-maximum" => new(101, 100),
            "depth-below-minimum" => new(100, -1),
            "depth-above-maximum" => new(100, 101),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };

        var error = Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(limits!));

        Assert.Equal("invalid-limits", error.Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    }

    [Fact]
    public async Task InvalidFullTuplesNeverWrite()
    {
        await using var rig = new CoreRig();
        rig.Arm();

        foreach (var invalid in new[]
        {
            TestMotion.Settings(pattern: 1, speed: 10), TestMotion.Settings(speed: 10, sensation: 49),
            TestMotion.Settings(speed: 101), TestMotion.Settings(speed: 10, stroke: 0),
            TestMotion.Settings(speed: 10, stroke: 21), TestMotion.Settings(speed: -1),
            TestMotion.Settings(speed: 10, depth: 101)
        })
            Assert.Equal("desired-invalid", Assert.Throws<ControlRejectedException>(() => rig.Submit(invalid)).Code);

        rig.Pump();
        Assert.Empty(rig.Bridge.Calls);
    }

    [Theory]
    [InlineData(9, 100)]
    [InlineData(50, 19)]
    public async Task ConflictingPendingInputRejectsEntirePolicyWithoutClearingIt(int speed, int depth)
    {
        await using var rig = new CoreRig();
        rig.Arm();
        var desired = TestMotion.Settings(speed: 10);
        rig.Submit(desired);
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict",
            Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(new(speed, depth))).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Empty(rig.Bridge.Calls);
    rig.Pump();
    Assert.Equal(desired, Assert.Single(rig.Bridge.Calls).Desired);
    }

    [Fact]
    public async Task ConflictingObservedMotionRejectsEvenWhenReplacementInputFits()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Submit(TestMotion.Settings(speed: 40));
        rig.Pump();
        rig.Submit(TestMotion.Settings(speed: 10));
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict",
            Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(new(20, 100))).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Single(rig.Bridge.Calls);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("write")]
    [InlineData("stop")]
    public async Task CompletedMotionAwaitingFeedbackStillConflictsWithLowerLimits(string replacement)
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.PublishOnCompletion = false;
        rig.Submit(TestMotion.Settings(speed: 40));
        rig.Pump();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 10));
        if (replacement == "stop") rig.Engine.Pause();
        if (replacement != "pending") rig.Engine.Tick();
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict",
            Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(new(20, 100))).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedFeedbackClearsSupersededWriteBoundsWithoutArming(bool stop)
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.PublishOnCompletion = false;
        rig.Submit(TestMotion.Settings(speed: 40));
        rig.Pump();
        if (stop) rig.Engine.Pause();
        else rig.Submit(TestMotion.Settings(speed: 10));
        rig.Pump();
        Assert.Equal("limits-conflict",
            Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(new(20, 100))).Code);
        rig.Bridge.Publish();
        rig.Pump();
        var before = rig.Engine.Snapshot;

        rig.Engine.SetLimits(new(20, 100));

        Assert.Equal(stop ? "Paused" : "Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(before.Generation, rig.Engine.Snapshot.Generation);
        Assert.Equal(new ControlLimits(20, 100), rig.Engine.Snapshot.Limits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingInFlightWriteRejectsEvenWhenPendingInputFitsOrStopIsLatched(bool stop)
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 40));
        rig.Engine.Tick();
        rig.Submit(TestMotion.Settings(speed: 10));
        if (stop) rig.Engine.Pause();
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict",
            Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(new(20, 100))).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(stop ? "Stop" : "Write", rig.Bridge.Calls[1].Kind);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task ConflictingZeroEnvelopeWriteRejectsBeforeItCanCompleteUnderNewBounds()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        rig.Submit(TestMotion.Settings(speed: 10, stroke: 15, depth: 60));
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(TestMotion.Settings(stroke: 15, depth: 60), rig.Bridge.Calls[1].Desired);
        rig.Submit(TestMotion.Settings(speed: 10, stroke: 15, depth: 40));
        var before = rig.Engine.Snapshot;

        Assert.Equal("limits-conflict",
            Assert.Throws<ControlRejectedException>(() => rig.Engine.SetLimits(new(25, 50))).Code);
        Assert.Equal(before, rig.Engine.Snapshot);
        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task CompatibleChangePreservesSerializedInFlightAndPendingWrites()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var first = TestMotion.Settings(speed: 10);
        rig.Submit(first);
        rig.Engine.Tick();
        var second = TestMotion.Settings(speed: 15);
        rig.Submit(second);
        var generation = rig.Engine.Snapshot.Generation;

        rig.Engine.SetLimits(new(25, 80));
    rig.Engine.Tick();

        Assert.Equal("Ready", rig.Engine.Snapshot.Mode);
        Assert.Equal(generation, rig.Engine.Snapshot.Generation);
        Assert.True(rig.Engine.Snapshot.OperationPending);
        Assert.Single(rig.Bridge.Calls);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(second, rig.Bridge.Calls[1].Desired);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(second, rig.Engine.Snapshot.Current);
        Assert.Equal(SerializedWrites, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Fact]
    public async Task CompatibleChangeDuringZeroWriteKeepsTheStoppedEnvelopeTransition()
    {
        await using var rig = new CoreRig();
        rig.Arm();
        rig.Bridge.AutoComplete = false;
        var desired = TestMotion.Settings(speed: 20, stroke: 15, depth: 40);
        rig.Submit(desired);
        rig.Engine.Tick();
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(0, rig.Bridge.Calls[1].Desired.Speed);
        var generation = rig.Engine.Snapshot.Generation;

        rig.Engine.SetLimits(new(25, 50));
    rig.Engine.Tick();

        Assert.Equal(2, rig.Bridge.Calls.Count);
        Assert.Equal(generation, rig.Engine.Snapshot.Generation);
        rig.Bridge.CompleteNext();
        rig.Engine.Tick();
        Assert.Equal(desired, rig.Bridge.Calls[2].Desired);
        rig.Bridge.CompleteNext();
        rig.Pump();
        Assert.Equal(desired, rig.Engine.Snapshot.Current);
        Assert.Equal(EnvelopeWrites, rig.Bridge.Calls.Select(call => call.Kind));
        Assert.Equal(1, rig.Bridge.MaxActive);
    }

    [Theory]
    [InlineData("Initializing")]
    [InlineData("Paused")]
    [InlineData("Locked")]
    [InlineData("Faulted")]
    public async Task PolicyEditsNeverArmOrClearExistingLifecycleState(string mode)
    {
        await using var rig = new CoreRig();
        if (mode == "Paused") rig.Engine.Recover();
        if (mode == "Locked") rig.Bridge.External(TestMotion.Settings(speed: 1, depth: 30));
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
        Assert.Empty(rig.Bridge.Calls);
    }
}
