using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceResetPositionTests : OssmDeviceTestBase
{
    [Theory]
    [InlineData(25)]
    [InlineData(0.001)]
    [InlineData(-0.001)]
    public async Task ResetPositionWaitsForFreshEndpointBeyondNominalDuration(double positionMm)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) == "sensor.read")
            {
                return PositionResponse(request, positionMm);
            }

            if (request["path"]?.GetValue<string>() == "target.menu")
            {
                checkpoint.TrySetResult();
            }

            return null;
        };

        var reset = await StartRetractAsync(token);
        Assert.Single(context.RequestsFor("sensor.read", "motion.position"));
        context.TimerCreated = delay =>
        {
            if (delay == TimeSpan.FromMilliseconds(250))
            {
                checkpoint.TrySetResult();
            }
        };
        context.Clock.Advance(TimeSpan.FromSeconds(11));
        try
        {
            await checkpoint.Task.WaitAsync(token);
            Assert.Empty(context.RequestsFor("target.set", "target.menu"));
            Assert.False(reset.IsCompleted);
            Assert.Equal(2, context.RequestsFor("sensor.read", "motion.position").Count);
            positionMm = 0;
            context.Clock.Advance(TimeSpan.FromMilliseconds(250));
            await reset;
            Assert.True(Component.IsArmed);
            Assert.Single(context.RequestsFor("target.set", "target.menu"));
            var readings = context.RequestsFor("sensor.read", "motion.position");
            Assert.Equal(3, readings.Count);
            Assert.Equal(3, readings.Select(request => request["id"]!.GetValue<uint>()).Distinct().Count());
            Assert.All(readings, request => Assert.Equal(42u, request["lease"]!.GetValue<uint>()));
        }
        finally
        {
            await Component.EmergencyStopAsync(token);
            try
            {
                await reset;
            }
            catch (OperationCanceledException)
            {
            }

            context.Write = null;
        }
    }

    [Fact]
    public async Task ResetPositionRetractsFullRangeThenStopsWithoutReplayingPattern()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var reset = await StartRetractAsync(token);
        var heldMenu = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) == "sensor.read")
            {
                return PositionResponse(request, 0);
            }

            return request["path"]?.GetValue<string>() == "target.menu" && heldMenu.TrySetResult(request)
                ? Observable.Return(BleTestContext.WriteResult([])) : null;
        };
        context.Clock.Advance(TimeSpan.FromMilliseconds(250));
        try
        {
            await heldMenu.Task.WaitAsync(token);
            Assert.False(reset.IsCompleted);
        }
        finally
        {
            context.Complete(await heldMenu.Task.WaitAsync(token));
            await reset;
            context.Write = null;
        }

        Assert.Equal(
            ["motion.speed", "setting.speedKnobAsLimit", "target.streaming", "motion.stroke", "motion.depth", "motion.sensation", "motion.speed", "motion.position", "motion.speed", "target.menu"],
            ControlRequests().Select(request => request["path"]!.GetValue<string>()));
        var position = Assert.Single(context.RequestsFor("target.set", "motion.position"));
        Assert.Equal(100, position["args"]!["value"]!.GetValue<int>());
        Assert.Equal(5000, position["args"]!["durationMs"]!.GetValue<int>());
        Assert.Equal(42u, position["lease"]!.GetValue<uint>());
        foreach (var path in new[] { "motion.stroke", "motion.depth", "motion.sensation" })
        {
            Assert.Equal(100, Assert.Single(context.RequestsFor("setting.write", path))["args"]!["value"]!.GetValue<int>());
        }

        Assert.Equal([0, 40, 0], context.RequestsFor("setting.write", "motion.speed").Select(request => request["args"]!["value"]!.GetValue<int>()));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        Assert.True(Component.IsArmed);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("ResetPosition", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrCallerCancellationInterruptsRetractAndRejectsCompetingRequests(bool callerCancels)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reset = await StartRetractAsync(caller.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(new(StrokePattern.RoboStroke, 50, 50, 50, 50), token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ResetAsync(token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ResetPositionAsync(token));

        var heldMenu = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request => request["path"]?.GetValue<string>() == "target.menu" && heldMenu.TrySetResult(request)
            ? Observable.Return(BleTestContext.WriteResult([])) : null;
        Task stop;
        if (callerCancels)
        {
            await caller.CancelAsync();
            await heldMenu.Task.WaitAsync(token);
            stop = Component.EmergencyStopAsync(token);
        }
        else
        {
            stop = Component.EmergencyStopAsync(token);
        }

        try
        {
            await heldMenu.Task.WaitAsync(token);
            Assert.False(reset.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Same(stop, Component.EmergencyStopAsync(new CancellationToken(true)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(new(StrokePattern.RoboStroke, 50, 50, 50, 50), token));
        }
        finally
        {
            context.Complete(await heldMenu.Task.WaitAsync(token));
            await stop;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
            context.Write = null;
        }

        Assert.Equal([0, 40, 0], context.RequestsFor("setting.write", "motion.speed").Select(request => request["args"]!["value"]!.GetValue<int>()));
        Assert.Single(context.RequestsFor("target.set", "target.menu"));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task StopWaitsForIssuedPositionTransferThenExitsStreamingWithoutFurtherMotion()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.State = "streaming.idle";
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (request["path"]?.GetValue<string>() != "motion.position")
            {
                return null;
            }

            issued.TrySetResult();
            return transfer;
        };
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.TimerCreated = delay =>
        {
            if (delay == TimeSpan.FromMilliseconds(100))
            {
                startup.TrySetResult();
            }
        };
        var reset = Component.ResetPositionAsync(token);
        await startup.Task.WaitAsync(token);
        context.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await issued.Task.WaitAsync(token);
        var stop = Component.EmergencyStopAsync(token);
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.Empty(context.RequestsFor("target.set", "target.menu"));
        }
        finally
        {
            transfer.OnNext(BleTestContext.WriteResult([]));
            transfer.OnCompleted();
            await stop;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
            context.Write = null;
        }

        Assert.Equal(["motion.position", "motion.speed", "target.menu"], ControlRequests().TakeLast(3).Select(request => request["path"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("motion.depth")]
    [InlineData("motion.position")]
    public async Task FailedRetractStillStopsWithoutDisarming(string rejectedPath)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (request["path"]?.GetValue<string>() != rejectedPath)
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: "reset_failed");
            return Observable.Return(BleTestContext.WriteResult([]));
        };
        var reset = await StartRetractAsync(token, waitForRetract: false);
        var failure = await Assert.ThrowsAsync<IOException>(() => reset);
        Assert.Contains("reset_failed", failure.Message);
        Assert.True(Component.IsArmed);
        Assert.Equal(["motion.speed", "target.menu"], ControlRequests().TakeLast(2).Select(request => request["path"]!.GetValue<string>()));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        context.Write = null;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"value\":0}")]
    [InlineData("{\"path\":0,\"value\":0}")]
    [InlineData("{\"path\":\"motion.speed\",\"value\":0}")]
    [InlineData("{\"path\":\"motion.position\"}")]
    [InlineData("{\"path\":\"motion.position\",\"value\":null}")]
    [InlineData("{\"path\":\"motion.position\",\"value\":\"0\"}")]
    [InlineData("{\"path\":\"motion.position\",\"value\":false}")]
    [InlineData("{\"path\":\"motion.position\",\"value\":1e400}")]
    [InlineData("{\"path\":\"motion.position\",\"value\":-1e400}")]
    public async Task MalformedPositionReadFailsAndStopsWithoutDisarming(string result)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "sensor.read")
            {
                return null;
            }

            context.Respond(request, "completed", result: JsonNode.Parse(result));
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var reset = await StartRetractAsync(token, waitForRetract: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => reset);
        Assert.True(Component.IsConnected);
        Assert.True(Component.IsArmed);
        Assert.Equal(["motion.speed", "target.menu"], ControlRequests().TakeLast(2).Select(request => request["path"]!.GetValue<string>()));
        Assert.Single(context.RequestsFor("sensor.read", "motion.position"));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        context.Write = null;
    }

    [Fact]
    public async Task FailedPositionReadStopsWithoutDisarming()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "sensor.read")
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: "position_failed");
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var reset = await StartRetractAsync(token, waitForRetract: false);
        Assert.Contains("position_failed", (await Assert.ThrowsAsync<IOException>(() => reset)).Message);
        Assert.True(Component.IsArmed);
        Assert.Equal(["motion.speed", "target.menu"], ControlRequests().TakeLast(2).Select(request => request["path"]!.GetValue<string>()));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        context.Write = null;
    }

    [Fact]
    public async Task StopInterruptsPendingPositionReadWithoutWaitingForResponse()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "sensor.read")
            {
                return null;
            }

            reading.TrySetResult();
            return Observable.Return(BleTestContext.WriteResult([]));
        };

        var reset = await StartRetractAsync(token, waitForRetract: false);
        await reading.Task.WaitAsync(token);
        await Component.EmergencyStopAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
        Assert.True(Component.IsArmed);
        Assert.Single(context.RequestsFor("sensor.read", "motion.position"));
        Assert.Single(context.RequestsFor("target.set", "target.menu"));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        context.Write = null;
    }

    [Fact]
    public async Task FailureAndCleanupFailureAreBothReturned()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            var path = request["path"]?.GetValue<string>();
            if (path is not ("motion.position" or "target.menu"))
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: path == "motion.position" ? "reset_failed" : "exit_failed");
            return Observable.Return(BleTestContext.WriteResult([]));
        };
        var reset = await StartRetractAsync(token, waitForRetract: false);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => reset);
        Assert.Collection(
            failure.InnerExceptions,
            exception => Assert.Contains("reset_failed", exception.Message),
            exception => Assert.Contains("exit_failed", exception.Message));
        Assert.False(Component.IsArmed);
        context.Write = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrDisposalInterruptsRetractAndJoinsStopBeforeReleasingLease(bool disposing)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var reset = await StartRetractAsync(token);
        if (disposing)
        {
            await Component.DisposeAsync();
        }
        else
        {
            await Component.DisconnectAsync(token);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
        Assert.False(Component.IsConnected);
        Assert.False(Component.IsArmed);
        var menuIndex = context.Requests.FindIndex(request => request["path"]?.GetValue<string>() == "target.menu");
        var releaseIndex = context.Requests.FindIndex(request => BleTestContext.Operation(request) == "control.release");
        Assert.True(menuIndex >= 0 && releaseIndex > menuIndex);
        Assert.Single(context.RequestsFor("target.set", "motion.position"));
        Assert.Single(context.RequestsFor("target.set", "target.menu"));
        Assert.Empty(context.RequestsFor("setting.write", "motion.pattern"));
        Assert.Equal(1, context.DisconnectCalls);
    }

    [Fact]
    public async Task NotReadyDeviceDoesNotReceivePositionAndStillRunsStopCleanup()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.State = "streaming.preflight";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ResetPositionAsync(token));
        Assert.Empty(context.RequestsFor("target.set", "motion.position"));
        Assert.True(Component.IsArmed);
        Assert.Equal(["motion.speed", "target.menu"], ControlRequests().TakeLast(2).Select(request => request["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AlreadyCancelledResetSendsNoCommands()
    {
        await context.ConnectAsync(Component, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Component.ResetPositionAsync(new CancellationToken(true)));
        Assert.Empty(ControlRequests());
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task ResetPositionCancelsPendingSettingsAndInvalidatesQueuedRearm()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request =>
        {
            if (request["path"]?.GetValue<string>() != "motion.pattern")
            {
                return null;
            }

            issued.TrySetResult();
            return transfer;
        };
        var settings = Component.SetAsync(new(StrokePattern.RoboStroke, 50, 50, 50, 50), token);
        await issued.Task.WaitAsync(token);
        var rearm = Component.ResetAsync(token);
        var retract = StartRetractAsync(token);
        transfer.OnNext(BleTestContext.WriteResult([]));
        transfer.OnCompleted();
        Assert.Equal(new(SetStatus.Cancelled, 0), await settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rearm);
        var reset = await retract;
        context.Write = request => BleTestContext.Operation(request) == "sensor.read" ? PositionResponse(request, 0) : null;
        context.Clock.Advance(TimeSpan.FromMilliseconds(250));
        await reset;
        Assert.Single(context.RequestsFor("setting.write", "motion.pattern"));
        Assert.Equal([0, 40, 0], context.RequestsFor("setting.write", "motion.speed").Select(request => request["args"]!["value"]!.GetValue<int>()));
        Assert.Equal(["motion.speed", "target.menu"], ControlRequests().TakeLast(2).Select(request => request["path"]!.GetValue<string>()));
        context.Write = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedZeroVelocityStillExitsStreamingAndPreservesCleanupErrors(bool menuAlsoFails)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var reset = await StartRetractAsync(token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) == "sensor.read")
            {
                return PositionResponse(request, 0);
            }

            var path = request["path"]?.GetValue<string>();
            if (path != "motion.speed" && !(menuAlsoFails && path == "target.menu"))
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: path == "motion.speed" ? "speed_failed" : "exit_failed");
            return Observable.Return(BleTestContext.WriteResult([]));
        };
        context.Clock.Advance(TimeSpan.FromMilliseconds(250));
        if (menuAlsoFails)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => reset);
            Assert.Collection(
                failure.InnerExceptions,
                exception => Assert.Contains("speed_failed", exception.Message),
                exception => Assert.Contains("exit_failed", exception.Message));
        }
        else
        {
            Assert.Contains("speed_failed", (await Assert.ThrowsAsync<IOException>(() => reset)).Message);
        }

        Assert.Single(context.RequestsFor("target.set", "target.menu"));
        Assert.False(Component.IsArmed);
        context.Write = null;
    }

    private async Task<Task> StartRetractAsync(CancellationToken token, bool waitForRetract = true)
    {
        context.State = "streaming.idle";
        var write = context.Write;
        context.Write = request => write?.Invoke(request)
            ?? (BleTestContext.Operation(request) == "sensor.read" ? PositionResponse(request, 25) : null);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.TimerCreated = delay =>
        {
            if (delay == TimeSpan.FromMilliseconds(100))
            {
                startup.TrySetResult();
            }
            else if (delay == TimeSpan.FromMilliseconds(250))
            {
                waiting.TrySetResult();
            }
        };
        var reset = Component.ResetPositionAsync(token);
        await startup.Task.WaitAsync(token);
        context.Clock.Advance(TimeSpan.FromMilliseconds(100));
        if (waitForRetract)
        {
            await waiting.Task.WaitAsync(token);
        }

        return reset;
    }

    private IObservable<BleCharacteristicResult> PositionResponse(JsonObject request, double positionMm)
    {
        context.Respond(request, "completed", result: new JsonObject { ["path"] = "motion.position", ["value"] = positionMm });
        return Observable.Return(BleTestContext.WriteResult([]));
    }

    private List<JsonObject> ControlRequests() => [.. context.Requests.Where(request => BleTestContext.Operation(request) is "target.set" or "setting.write")];
}