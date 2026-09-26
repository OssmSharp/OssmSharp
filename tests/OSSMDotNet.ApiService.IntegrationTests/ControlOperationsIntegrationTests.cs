using OssmSharp.Web.Application.Models;
using OssmSharp.Web.Application.Tests;

namespace OSSMDotNet.ApiService.IntegrationTests;

/// <summary>
/// Verifies control operations and fault handling against an observed simulator device.
/// </summary>
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
        fixture.DrainSamples();
        var before = fixture.Operations.Read();

        var exception = Assert.Throws<ControlRejectedException>(() =>
            fixture.Operations.ApplySessionLimits(null!, before.Precondition));

        var after = fixture.Operations.Read();
        Assert.Equal("invalid-limits", exception.Code);
        Assert.Equal(before, after);
        Assert.False(after.Snapshot.OperationPending);
        Assert.False(fixture.HasPendingDeviceChanges);
    }

    [Theory]
    [InlineData("external-zero", 0, 20, "Paused")]
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

        await fixture.Faults.InjectAsync(new(kind));

        var observed = await fixture.ReadUntilAsync(
            sample => sample.MotionSettings.Speed == expectedSpeed &&
                sample.MotionSettings.Depth == expectedDepth,
            timeout.Token);
        fixture.Authority.Tick();
        var status = fixture.Operations.Read().Snapshot;

        Assert.Equal(expectedSpeed, observed.MotionSettings.Speed);
        Assert.Equal(expectedDepth, observed.MotionSettings.Depth);
        Assert.Equal(expectedMode, status.Mode);
        Assert.Equal(expectedDepth, status.Observed!.MotionSettings.Depth);
        Assert.Equal(DateTimeKind.Utc, observed.Timestamp.Kind);
    }

    [Fact]
    public async Task DelayFaultDefersTheNextOwnedWriteUntilTheSimulatorSettlesIt()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);

        await fixture.Faults.InjectAsync(new("delay"));
        var delayedWrite = fixture.Device.SetMotionAsync(TestMotion.Settings(speed: 12), timeout.Token);

        Assert.False(delayedWrite.IsCompleted);
        await delayedWrite.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
        var observed = await fixture.ReadUntilAsync(
            sample => sample.MotionSettings.Speed == 12,
            timeout.Token);

        Assert.Equal(12, observed.MotionSettings.Speed);
        Assert.True(observed.VelocityMillimetresPerSecond is { } velocity && double.IsFinite(velocity));
        Assert.Equal(DateTimeKind.Utc, observed.Timestamp.Kind);
    }

    [Fact]
    public async Task StaleAndFreshFaultKindsFreezeThenResumeTheCurrentSimulatorObservation()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        var before = fixture.LatestState;
        fixture.DrainSamples();

        await fixture.Faults.InjectAsync(new("stale"));
        fixture.DrainSamples();
        await fixture.Faults.InjectAsync(new("external-depth"));

        Assert.False(fixture.HasPendingDeviceChanges);

        await fixture.Faults.InjectAsync(new("fresh"));
        var refreshed = await fixture.ReadUntilAsync(
            sample => sample.MotionSettings.Depth == before.MotionSettings.Depth + 1,
            timeout.Token);

        Assert.Equal(before.MotionSettings.Depth + 1, refreshed.MotionSettings.Depth);
        Assert.True(refreshed.Timestamp > before.Timestamp);
    }

    [Fact]
    public async Task DisconnectAndConnectFaultKindsExposeTheSafeDisconnectedThenReadyBridgeStatus()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalSimulatorControlFixture.StartAsync(timeout.Token);
        fixture.DrainSamples();
        fixture.DrainConnections();

        await fixture.Faults.InjectAsync(new("disconnect"));
        var disconnected = await fixture.ReadConnectionUntilAsync(
            state => state == DeviceConnectionState.Disconnected,
            timeout.Token);
        var safeState = await fixture.ReadUntilAsync(
            state => state.MotionSettings.Speed == 0,
            timeout.Token);
        var faulted = fixture.Operations.Read().Snapshot;
        await fixture.Faults.InjectAsync(new("connect"));
        var reconnected = await fixture.ReadConnectionUntilAsync(
            state => state == DeviceConnectionState.Connected,
            timeout.Token);
        var readyState = await fixture.ReadUntilAsync(
            state => state.MotionSettings.Speed == 0,
            timeout.Token);
        var reconnectedStatus = fixture.Operations.Read().Snapshot;

        Assert.Equal(DeviceConnectionState.Disconnected, disconnected);
        Assert.Equal(0, safeState.MotionSettings.Speed);
        Assert.Equal("Faulted", faulted.Mode);
        Assert.Equal(DeviceConnectionState.Connected, reconnected);
        Assert.Equal(0, readyState.MotionSettings.Speed);
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
            fixture.Faults.InjectAsync(new("unknown")));

        var after = fixture.Operations.Read();
        Assert.Equal("unknown-simulator-fault", exception.Code);
        Assert.Equal(before, after);
        Assert.False(after.Snapshot.OperationPending);
        Assert.False(fixture.HasPendingDeviceChanges);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
