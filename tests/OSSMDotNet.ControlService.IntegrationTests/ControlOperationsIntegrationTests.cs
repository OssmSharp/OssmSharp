using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

public sealed class ControlOperationsIntegrationTests
{
    [Fact]
    public async Task StopReturnsTheCurrentPausedStatusAndWritesOnlyTheSafeActivityPhrase()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        var before = fixture.Operations.Read();
        var activities = fixture.Activity.ReadAfter(0);
        var beforeActivity = activities.Count == 0 ? 0 : activities[^1].Sequence;

        var stopped = fixture.Operations.Stop();

        var activity = Assert.Single(fixture.Activity.ReadAfter(beforeActivity));
        Assert.Equal(before.Precondition.AuthorityRevision + 1, stopped.Precondition.AuthorityRevision);
        Assert.Equal(before.SnapshotRevision + 1, stopped.SnapshotRevision);
        Assert.Equal(("Paused", "operator-stop"), (stopped.Snapshot.Mode, stopped.Snapshot.Reason));
        Assert.True(stopped.Snapshot.OperationPending);
        Assert.Equal("Stop requested.", activity.Message);
        Assert.DoesNotContain("invite", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("desired", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectedLimitsLeaveAuthorityAndTheCurrentBridgeObservationUnchanged()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        var before = fixture.Operations.Read();

        var exception = Assert.Throws<ControlRejectedException>(() =>
            fixture.Operations.ApplySessionLimits(null!, before.Precondition));

        var after = fixture.Operations.Read();
        Assert.Equal("invalid-limits", exception.Code);
        Assert.Equal(before, after);
        Assert.False(after.Snapshot.OperationPending);
        Assert.Null(after.Snapshot.Observed!.CommandId);
    }

    [Theory]
    [InlineData("external-zero", 0, 20, "Initializing")]
    [InlineData("external-depth", 0, 21, "Locked")]
    public async Task ExternalFaultKindsMapToTheirExpectedObservedState(
        string kind,
        int expectedSpeed,
        int expectedDepth,
        string expectedMode)
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        fixture.DrainSamples();

        await fixture.Operations.InjectSimulatorFaultAsync(new(kind));

        var observed = await fixture.ReadUntilAsync(
            sample => sample.CommandId is null &&
                sample.Desired.Speed == expectedSpeed &&
                sample.Desired.Depth == expectedDepth,
            timeout.Token);
        fixture.Authority.Tick();
        var status = fixture.Operations.Read().Snapshot;

        Assert.Equal(expectedSpeed, observed.Desired.Speed);
        Assert.Equal(expectedDepth, observed.Desired.Depth);
        Assert.Equal(expectedMode, status.Mode);
        Assert.Equal(expectedDepth, status.Observed!.Desired.Depth);
        Assert.Null(observed.CommandId);
    }

    [Fact]
    public async Task DelayFaultDefersTheNextOwnedWriteUntilTheSimulatorSettlesIt()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);

        await fixture.Operations.InjectSimulatorFaultAsync(new("delay"));
        var delayedWrite = fixture.Bridge.SetMotionAsync(41, new(0, 12, 10, 20, 50), timeout.Token);

        Assert.False(delayedWrite.IsCompleted);
        await delayedWrite.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
        var observed = await fixture.ReadUntilAsync(
            sample => sample.CommandId == 41 && sample.Desired.Speed == 12,
            timeout.Token);

        Assert.True(observed.Enabled);
        Assert.Equal(12, observed.Desired.Speed);
        Assert.Null(fixture.Bridge.Failure);
    }

    [Fact]
    public async Task StaleAndFreshFaultKindsFreezeThenResumeTheCurrentSimulatorObservation()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        fixture.DrainSamples();
        var changed = MotionSettings.Stopped with { Depth = 25 };

        await fixture.Operations.InjectSimulatorFaultAsync(new("stale"));
        fixture.DrainSamples();
        await fixture.Bridge.InjectAsync("external", changed);
        var frozen = await fixture.ReadUntilAsync(_ => true, timeout.Token);

        await fixture.Operations.InjectSimulatorFaultAsync(new("fresh"));
        var refreshed = await fixture.ReadUntilAsync(
            sample => sample.ObservationSequenceNumber > frozen.ObservationSequenceNumber && sample.Desired == changed,
            timeout.Token);

        Assert.NotEqual(changed, frozen.Desired);
        Assert.Equal(changed, refreshed.Desired);
        Assert.True(refreshed.ObservationSequenceNumber > frozen.ObservationSequenceNumber);
        Assert.Null(refreshed.CommandId);
    }

    [Fact]
    public async Task DisconnectAndConnectFaultKindsExposeTheSafeDisconnectedThenReadyBridgeStatus()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        fixture.DrainSamples();

        await fixture.Operations.InjectSimulatorFaultAsync(new("disconnect"));
        var disconnected = await fixture.ReadUntilAsync(sample => !sample.Connected, timeout.Token);
        var faulted = fixture.Operations.Read().Snapshot;
        await fixture.Operations.InjectSimulatorFaultAsync(new("connect"));
        var reconnected = await fixture.ReadUntilAsync(sample => sample.Connected && sample.CommandId is null, timeout.Token);
        var reconnectedStatus = fixture.Operations.Read().Snapshot;

        Assert.False(disconnected.Connected);
        Assert.Equal(("Faulted", "bridge-disconnected"), (faulted.Mode, faulted.Reason));
        Assert.True(reconnected.Connected);
        Assert.True(reconnectedStatus.BridgeReady);
        Assert.Equal(0, reconnectedStatus.Desired.Speed);
    }

    [Fact]
    public async Task UnknownSimulatorFaultRejectsWithoutChangingAuthorityOrIssuingBridgeWork()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        var before = fixture.Operations.Read();
        fixture.DrainSamples();

        var exception = await Assert.ThrowsAsync<ControlRejectedException>(() =>
            fixture.Operations.InjectSimulatorFaultAsync(new("unknown")));

        var after = fixture.Operations.Read();
        Assert.Equal("unknown-simulator-fault", exception.Code);
        Assert.Equal(before, after);
        Assert.False(after.Snapshot.OperationPending);
        Assert.Null(after.Snapshot.Observed!.CommandId);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
