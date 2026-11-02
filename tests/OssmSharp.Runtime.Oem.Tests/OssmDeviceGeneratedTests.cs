using System.Reactive.Linq;
using System.Text.Json.Nodes;
using CsCheck;
using Shiny.BluetoothLE;
using Xunit;
using Episode = OssmSharp.Runtime.Oem.Tests.PendingDeviceRequest;
using Event = OssmSharp.Runtime.Oem.Tests.Models.OssmDeviceEvent;
using Kind = OssmSharp.Runtime.Oem.Tests.Models.OssmDeviceEventKind;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceGeneratedTests(ITestOutputHelper output) : OssmDeviceGeneratedCase
{
    private static readonly string[] Paths = ["motion.pattern", "motion.stroke", "motion.depth", "motion.sensation", "motion.speed"];

    [Fact]
    public Task GeneratedObsoleteConnectionCallbacksCannotChangeNewObservationsOrControl() =>
        Gen.Int[0, 2].Array[0, 20].SampleAsync(async events =>
        {
            await using var test = CreateCase();
            var observers = test.CaptureFailureObservers();
            await test.ConnectAsync();
            var oldFailure = observers[0];
            await test.Device.DisconnectAsync(TestContext.Current.CancellationToken);
            test.Ble.MotionStreamId = 8;
            var currentObserverIndex = observers.Count;
            await test.ConnectAsync();
            var expected = new DeviceState(StrokePattern.SimpleStroke, 0, 50, 10, 50);

            async Task AssertCurrentConnectionUnchangedAsync()
            {
                Assert.True(test.Device.IsConnected);
                Assert.True(test.Device.IsArmed);
                Assert.Null(test.Device.ConnectionFailure);
                Assert.Equal(expected, await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal(1, test.Ble.DisconnectCalls);
            }

            foreach (var action in events.Append(0))
            {
                if (action == 0)
                    oldFailure.OnNext(new BleException("Obsolete failure event."));
                else if (action == 1)
                    test.Publish(new(StrokePattern.Insist, 99, 1, 100, 100), BleTestContext.StreamId);
                else
                {
                    expected = new(StrokePattern.RoboStroke, 12.5, 25, 17, 29);
                    test.Publish(expected, 8);
                }
                await AssertCurrentConnectionUnchangedAsync();
            }
            // OnError terminates the observer; exercise repeatable failure events before the terminal callback.
            oldFailure.OnError(new IOException("Obsolete terminal callback."));
            await AssertCurrentConnectionUnchangedAsync();
            // A current-generation callback is deliberately different from an obsolete one.
            var failure = new IOException("Current callback.");
            // The first observer added by this connection owns lifecycle failures.
            // Later temporary observers can deliberately have no OnError handler.
            observers[currentObserverIndex].OnError(failure);
            Assert.False(test.Device.IsArmed);
            Assert.Same(failure, test.Device.ConnectionFailure);
            Assert.Equal(expected, await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken));
        }, threads: 1, writeLine: output.WriteLine);

    [Fact]
    public Task GeneratedTransferResponseAndCancellationEventsKeepThreeTruthsSeparate() =>
        Gen.Int[0, 100].Array[4]
            .Select(values => new Event((Kind)(values[0] % 10), values[1], values[2], values[3] % 5))
            .Array[0, 24].SampleAsync(async trace =>
            {
                await using var test = CreateCase();
                await test.ConnectAsync();
                Episode? active = null;
                var expectedRequests = new List<(string Path, int Value)>();
                var observed = new DeviceState(StrokePattern.SimpleStroke, 0, 50, 10, 50);
                var disarmed = false;
                try
                {
                    foreach (var action in trace)
                    {
                        switch (action.Kind)
                        {
                            case Kind.Start when active is null:
                                if (disarmed)
                                {
                                    await Assert.ThrowsAsync<InvalidOperationException>(() => test.Device.SetAsync(Settings(action), TestContext.Current.CancellationToken));
                                    break;
                                }
                                active = new Episode(action);
                                var current = active;
                                var writes = 0;
                                test.Ble.Write = request => BleTestContext.IsMotionWrite(request) && writes++ == action.HeldIndex
                                    ? Hold(current, request) : null;
                                expectedRequests.AddRange(Expected(action).Take(action.HeldIndex + 1));
                                active.Operation = test.Device.SetAsync(Settings(action), active.Cancellation.Token);
                                await active.Issued.Task.WaitAsync(TestContext.Current.CancellationToken);
                                Assert.False(active.Operation.IsCompleted);
                                break;
                            case Kind.Start when active?.Stopping is not null:
                                await Assert.ThrowsAsync<InvalidOperationException>(() => test.Device.SetAsync(Settings(action), TestContext.Current.CancellationToken));
                                break;
                            case Kind.Transfer when active is not null:
                                active.SettleTransfer();
                                break;
                            case Kind.Accepted when active is not null:
                                test.Ble.Respond(active.Request!, "accepted");
                                test.Ble.Respond(active.Request!, "accepted");
                                var unrelated = active.Request!.DeepClone().AsObject();
                                unrelated["id"] = uint.MaxValue;
                                test.Ble.Respond(unrelated, "completed");
                                if (!active.Terminal && active.Interrupted is null)
                                    Assert.False(active.Operation!.IsCompleted);
                                break;
                            case Kind.Completed when active is not null:
                                test.Ble.Complete(active.Request!);
                                test.Ble.Complete(active.Request!);
                                active.Terminal = true;
                                break;
                            case Kind.Reject when active is { Terminal: false, Interrupted: null }:
                                test.Ble.Respond(active.Request!, "failed", ok: false, code: "invalid_value");
                                active.Terminal = active.Rejected = true;
                                break;
                            case Kind.Cancel when active is not null:
                                await active.Cancellation.CancelAsync();
                                active.Interrupted ??= SetStatus.Cancelled;
                                break;
                            case Kind.Replace when active is { Terminal: false, Replacement: null, Stopping: null } && !disarmed:
                                active.Interrupted = SetStatus.Superseded;
                                active.ReplacementSettings = action;
                                active.Replacement = test.Device.SetAsync(Settings(action), TestContext.Current.CancellationToken);
                                break;
                            case Kind.Stop when active is not null && active.Stopping is null:
                                if (active.Replacement is null)
                                    active.Interrupted = SetStatus.Cancelled;
                                active.ReplacementCancelled = active.Replacement is not null;
                                active.Stopping = test.Device.EmergencyStopAsync(TestContext.Current.CancellationToken);
                                if (!active.TransferSettled)
                                {
                                    Assert.False(active.Stopping.IsCompleted);
                                    Assert.Equal(expectedRequests, test.Requests());
                                }
                                break;
                            case Kind.Observe:
                                observed = new(StrokePattern.RoboStroke, action.Speed / 10.0, action.Stroke, 17, 29);
                                test.Publish(observed);
                                break;
                            case Kind.Reset when active is null:
                                await test.Device.ResetAsync(TestContext.Current.CancellationToken);
                                disarmed = false;
                                break;
                        }

                        if (active is { TransferSettled: true } && (active.Terminal || active.Interrupted is not null))
                        {
                            if (active.Rejected)
                            {
                                var error = await Assert.ThrowsAsync<DeviceWriteException>(() => active.Operation!);
                                Assert.Equal(active.Action.HeldIndex, error.CommandsWritten);
                                Assert.Equal("invalid_value", Assert.IsType<DeviceRequestException>(error.GetBaseException()).Code);
                                disarmed = true;
                            }
                            else
                            {
                                var result = await active.Operation!;
                                var confirmed = active.Interrupted is null ? 5 : active.Action.HeldIndex + (active.Terminal ? 1 : 0);
                                Assert.Equal(new(active.Interrupted ?? SetStatus.Completed, confirmed), result);
                                if (active.Interrupted is null)
                                    expectedRequests.AddRange(Expected(active.Action).Skip(active.Action.HeldIndex + 1));
                            }
                            if (active.Replacement is not null)
                            {
                                Assert.Equal(new(active.ReplacementCancelled ? SetStatus.Cancelled : SetStatus.Completed, active.ReplacementCancelled ? 0 : 5), await active.Replacement);
                                if (!active.ReplacementCancelled)
                                    expectedRequests.AddRange(Expected(active.ReplacementSettings!));
                            }
                            if (active.Stopping is not null)
                            {
                                await active.Stopping;
                                expectedRequests.Add(("motion.speed", 0));
                            }
                            Assert.Equal(expectedRequests, test.Requests());
                            active.Dispose();
                            active = null;
                        }

                        // A transfer, accepted response, terminal result, cancellation or Stop cannot
                        // manufacture the motion observations that the firmware has not delivered.
                        Assert.Equal(observed, await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken));
                    }
                    Assert.Equal(expectedRequests, test.Requests());
                    Assert.Equal(observed, await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken));
                }
                finally
                {
                    test.Ble.Write = null;
                    if (active is not null)
                    {
                        await active.Cancellation.CancelAsync();
                        active.SettleTransfer();
                        test.Ble.Complete(active.Request!);
                        try
                        {
                            await active.Operation!;
                        }
                        catch (DeviceWriteException) when (active.Rejected)
                        {
                        }
                        if (active.Replacement is not null)
                            await active.Replacement;
                        if (active.Stopping is not null)
                            await active.Stopping;
                        active.Dispose();
                    }
                }
            }, threads: 1, writeLine: output.WriteLine);

    [Fact]
    public async Task Requested53AtStroke10CompletesRequestsWithoutInventingCurrent50()
    {
        await using var test = CreateCase();
        await test.ConnectAsync();
        var requested = new MotionSettings(StrokePattern.SimpleStroke, 53, 10, 17, 29);
        Assert.Equal(new(SetStatus.Completed, 5), await test.Device.SetAsync(requested, TestContext.Current.CancellationToken));
        Assert.Equal(
            new[] { ("motion.pattern", 0), ("motion.stroke", 10), ("motion.depth", 17), ("motion.sensation", 29), ("motion.speed", 5) },
            test.Requests());
        Assert.Equal(53, requested.Speed);
        var beforeObservation = await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.0, beforeObservation!.Speed);
        Assert.Equal(50, beforeObservation.Stroke);
        test.Publish(new(StrokePattern.SimpleStroke, 5, 10, 17, 29));
        var current = await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(50.0, current!.Speed);
        Assert.Equal(5.0, current.CurrentDeviceVelocityPercent);
        Assert.Equal(10, current.Stroke);
        Assert.Equal(53, requested.Speed);
    }

    [Fact]
    public async Task StopWaitsForIssuedTransferButNotTheEarlierTerminalResponse()
    {
        await using var test = CreateCase();
        await test.ConnectAsync();
        using var episode = new Episode(new(Kind.Start, 53, 10, 0));
        test.Ble.Write = request => BleTestContext.IsMotionWrite(request) && episode.Request is null ? Hold(episode, request) : null;
        episode.Operation = test.Device.SetAsync(Settings(episode.Action), TestContext.Current.CancellationToken);
        await episode.Issued.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stop = test.Device.EmergencyStopAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.Equal(new[] { ("motion.pattern", 1) }, test.Requests());
            episode.SettleTransfer();
            await stop;
            Assert.Equal(new(SetStatus.Cancelled, 0), await episode.Operation);
            Assert.Equal(new[] { ("motion.pattern", 1), ("motion.speed", 0) }, test.Requests());
            // No terminal response for the first command was needed.
            test.Ble.Complete(episode.Request!);
            Assert.Equal(new[] { ("motion.pattern", 1), ("motion.speed", 0) }, test.Requests());
        }
        finally
        {
            episode.SettleTransfer();
            test.Ble.Complete(episode.Request!);
            await episode.Operation;
            await stop;
        }
    }

    [Fact]
    public Task GeneratedRegisteredTimeoutsRetryTheSameIdAndPayloadWithoutDoubleCounting() =>
        Gen.Int[0, 2].Array[0, 12].SampleAsync(async events =>
        {
            await using var test = CreateCase();
            await test.ConnectAsync();
            var first = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var retry = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            test.Ble.TimerCreated = due =>
            {
                if (due == test.Ble.DeviceOptions.ResponseTimeout)
                    timer.TrySetResult();
            };
            JsonObject? original = null;
            test.Ble.Write = request =>
            {
                if (!BleTestContext.IsMotionWrite(request) || (original is not null && request["id"]!.GetValue<uint>() != original["id"]!.GetValue<uint>()))
                    return null;
                if (original is null)
                {
                    original = request;
                    first.TrySetResult(request);
                }
                else
                {
                    retry.TrySetResult(request);
                }
                return Observable.Return(BleTestContext.WriteResult([]));
            };
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var set = test.Device.SetAsync(new(StrokePattern.Insist, 53, 10, 17, 29), cancellation.Token);
            try
            {
                await first.Task.WaitAsync(TestContext.Current.CancellationToken);
                foreach (var action in events)
                {
                    if (action == 0)
                        test.Ble.Respond(original!, "accepted");
                    else if (action == 1)
                    {
                        var unrelated = original!.DeepClone().AsObject();
                        unrelated["id"] = uint.MaxValue;
                        test.Ble.Complete(unrelated);
                    }
                    else
                        test.Publish(new(StrokePattern.RoboStroke, 12.5, 25, 17, 29));
                    Assert.False(set.IsCompleted);
                }
                await timer.Task.WaitAsync(TestContext.Current.CancellationToken);
                test.Ble.Clock.Advance(test.Ble.DeviceOptions.ResponseTimeout);
                var retransmitted = await retry.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(original!.ToJsonString(), retransmitted.ToJsonString());
                test.Ble.Complete(original);
                test.Ble.Complete(original);
                Assert.Equal(new(SetStatus.Completed, 5), await set);
                var writes = test.Requests(allowRetries: true);
                Assert.Equal(6, writes.Count);
                Assert.Equal(("motion.pattern", 6), writes[0]);
                Assert.Equal(writes[0], writes[1]);
                Assert.Equal(new[] { ("motion.stroke", 10), ("motion.depth", 17), ("motion.sensation", 29), ("motion.speed", 5) }, writes.Skip(2));
                var lastKnownState = await test.Device.GetStateAsync(cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(events.Contains(2) ? 50.0 : 0.0, lastKnownState!.Speed);
                Assert.Equal(events.Contains(2) ? 25 : 50, lastKnownState.Stroke);
            }
            finally
            {
                await cancellation.CancelAsync();
                test.Ble.Write = null;
                if (original is not null)
                    test.Ble.Complete(original);
                await set;
            }
        }, threads: 1, writeLine: output.WriteLine);

    private static MotionSettings Settings(Event action) => new(StrokePattern.TeasingPounding, action.Speed, action.Stroke, 17, 29);
    private static IEnumerable<(string Path, int Value)> Expected(Event action)
    {
        var raw = Enumerable.Range(0, 101).Single(value =>
            100 * value <= action.Speed * action.Stroke && action.Speed * action.Stroke < 100 * (value + 1));
        return Paths.Zip(new[] { 1, action.Stroke, 17, 29, raw });
    }
    private static IObservable<BleCharacteristicResult> Hold(Episode episode, JsonObject request)
    {
        episode.Request = request;
        episode.Issued.TrySetResult(request);
        return episode.Transfer;
    }

}
