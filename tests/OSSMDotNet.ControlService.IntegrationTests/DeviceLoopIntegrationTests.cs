using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

public sealed class DeviceLoopIntegrationTests
{
    [Fact]
    public async Task LoopStartsTicksAndPublishesTheCurrentSimulatorStateOnceItsBridgeIsReady()
    {
        using var timeout = CreateTimeout();
        await using var harness = await DeviceLoopIntegrationHarness.StartAsync(timeout.Token);

        var delivery = await harness.Sink.ReadUntilAsync(
            call => call is
            {
                ConnectionId: "loop-observer",
                Method: "operatorState",
                Payload: OperatorLiveControlStatus
                {
                    Authority.Snapshot.BridgeReady: true,
                    Authority.Snapshot.IsSimulator: true
                }
            },
            timeout.Token);
        var state = Assert.IsType<OperatorLiveControlStatus>(delivery.Payload);

        Assert.Equal(("loop-observer", "operatorState"), (delivery.ConnectionId, delivery.Method));
        Assert.True(state.Authority.SnapshotRevision > 0);
        Assert.Equal(harness.Authority.Read().Snapshot.SessionMode, state.Authority.Snapshot.SessionMode);
        Assert.Equal("ossm-rs-sim-v1", state.Authority.Snapshot.Observed!.ProfileId);
        Assert.Null(harness.Bridge.Failure);
    }

    [Fact]
    public async Task ShutdownQueuesSafeStopWithoutClaimingItWasVerifiedAndRepeatedStopRemainsSafe()
    {
        using var timeout = CreateTimeout();
        await using var harness = await DeviceLoopIntegrationHarness.StartAsync(timeout.Token);
        await harness.Sink.ReadUntilAsync(
            call => call.Payload is OperatorLiveControlStatus
            {
                Authority.Snapshot.BridgeReady: true
            },
            timeout.Token);
        var before = harness.Authority.Read();

        await harness.Loop.StopAsync(timeout.Token);

        var stopping = harness.Authority.Read();
        Assert.True(stopping.Precondition.AuthorityRevision > before.Precondition.AuthorityRevision);
        Assert.Equal(("Paused", "control-service-stopping"), (stopping.Snapshot.Mode, stopping.Snapshot.Reason));
        Assert.True(stopping.Snapshot.OperationPending);
        Assert.Equal(0, stopping.Snapshot.VerifiedSeq);
        Assert.Equal(0, stopping.Snapshot.Desired.Speed);

        await harness.Loop.StopAsync(timeout.Token);

        var repeated = harness.Authority.Read();
        Assert.Equal(stopping.Precondition.AuthorityRevision + 1, repeated.Precondition.AuthorityRevision);
        Assert.Equal("control-service-stopping", repeated.Snapshot.Reason);
        Assert.True(repeated.Snapshot.OperationPending);
        Assert.Null(harness.Bridge.Failure);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
