using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moq;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class OssmDeviceExecutionTests : OssmDeviceTestBase
{
    [Fact]
    public async Task SupersessionResponseCancellationPreservesSynchronousCallerLatestRequest()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var held = HoldFirstMotion();
        var active = Component.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        await held.Task.WaitAsync(token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var preparation = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request => preparation.TrySetResult(request) ? transfer : null;
        var stateLock = Assert.IsType<Lock>(typeof(OssmDevice)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(Lock)).GetValue(Component));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldDuringReentry = false;
        var latestSettings = new MotionSettings(StrokePattern.HalfNHalf, 30, 30, 30, 30);
        var latest = active.ContinueWith(
            _ =>
            {
                heldDuringReentry = stateLock.IsHeldByCurrentThread;
                var result = Component.SetAsync(latestSettings, token);
                entered.TrySetResult();
                return result;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
        var intervening = Component.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.False(heldDuringReentry);
        }
        finally
        {
            context.Complete(await preparation.Task.WaitAsync(token));
            Settle(transfer);
            await intervening;
            await latest;
        }

        Assert.Equal(new(SetStatus.Superseded, 0), await active);
        Assert.Equal(new(SetStatus.Superseded, 0), await intervening);
        Assert.Equal(new(SetStatus.Completed, 5), await latest);
        Assert.Equal(new[] { new FirmwareCommand(FirmwareSetting.Pattern, 1) }.Concat(latestSettings.ToCommands()), Writes());
    }

    [Fact]
    public async Task StopResponseCancellationRejectsSynchronousCallerReentryOutsideStateLock()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var held = HoldFirstMotion();
        var settings = new MotionSettings(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        var active = Component.SetAsync(settings, token);
        await held.Task.WaitAsync(token);
        var stateLock = Assert.IsType<Lock>(typeof(OssmDevice)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(Lock)).GetValue(Component));
        var heldDuringReentry = false;
        var reentry = active.ContinueWith(
            _ =>
            {
                heldDuringReentry = stateLock.IsHeldByCurrentThread;
                return Record.ExceptionAsync(() => Component.SetAsync(settings, token)).AsTask();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
        var heldStop = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request => BleTestContext.IsMotionWrite(request)
            && request["path"]!.GetValue<string>() == "motion.speed"
            && request["args"]!["value"]!.GetValue<int>() == 0
            && heldStop.TrySetResult(request)
                ? Observable.Return(BleTestContext.WriteResult([])) : null;
        var stop = Component.EmergencyStopAsync(token);
        try
        {
            var failure = Assert.IsType<InvalidOperationException>(await reentry.WaitAsync(token));
            Assert.Contains("Stop", failure.Message);
            Assert.False(heldDuringReentry);
            Assert.False(stop.IsCompleted);
            Assert.Equal(new(SetStatus.Cancelled, 0), await active);
        }
        finally
        {
            context.Complete(await heldStop.Task.WaitAsync(token));
            await stop;
            await active;
        }

        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task StopCancellationRejectsSynchronousCallerReentryOutsideStateLock()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var settings = new MotionSettings(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        var active = Component.SetAsync(settings, token);
        var queued = Component.SetAsync(settings, token);
        var stateLock = Assert.IsType<Lock>(typeof(OssmDevice)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(Lock)).GetValue(Component));
        var heldDuringReentry = false;
        var reentry = queued.ContinueWith(
            _ =>
            {
                heldDuringReentry = stateLock.IsHeldByCurrentThread;
                return Record.ExceptionAsync(() => Component.SetAsync(settings, token)).AsTask();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
        var stop = Component.EmergencyStopAsync(token);
        try
        {
            var failure = Assert.IsType<InvalidOperationException>(await reentry.WaitAsync(token));
            Assert.Contains("Stop", failure.Message);
            Assert.False(heldDuringReentry);
            Assert.False(stop.IsCompleted);
            Assert.Equal(new(SetStatus.Cancelled, 0), await queued);
        }
        finally
        {
            Settle(transfer);
            await stop;
            await active;
        }

        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task StateChangedRunsOutsideTheSingleStateLock()
    {
        var token = TestContext.Current.CancellationToken;
        var device = Component;
        var locks = typeof(OssmDevice)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(Lock))
            .Select(field => Assert.IsType<Lock>(field.GetValue(device)))
            .ToArray();
        var notifications = 0;
        var heldDuringNotification = false;
        device.StateChanged += (_, _) =>
        {
            notifications++;
            heldDuringNotification |= locks.Any(stateLock => stateLock.IsHeldByCurrentThread);
        };

        await context.ConnectAsync(device, token);
        Publish(new(StrokePattern.Insist, 9, 9, 9, 9));
        context.PublishFrame([1]);

        Assert.True(notifications >= 3);
        Assert.False(heldDuringNotification);
        Assert.Single(locks);
    }

    [Fact]
    public async Task FailedStopLogsCallAndFailureWithoutCompletion()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("stop_failed");
        try
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => Component.EmergencyStopAsync(token));
            Assert.Contains("stop_failed", failure.Message);
            Assert.Contains(logger.Entries, entry => entry.Id.Id == 17 && entry.Level == LogLevel.Information);
            Assert.Contains(logger.Entries, entry => entry.Id.Id == 2 && entry.Level == LogLevel.Error && entry.Exception == failure);
            Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id == 20);
        }
        finally
        {
            context.Write = null;
        }
    }

    [Fact]
    public async Task DisconnectPreservesClientAndReconnectArmsFreshStateWithoutReplay()
    {
        var token = TestContext.Current.CancellationToken;
        var notifications = 0;
        Component.StateChanged += (_, _) => notifications++;
        await context.ConnectAsync(Component, token);
        var settings = new MotionSettings(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        await Component.SetAsync(settings, token);
        await Component.DisconnectAsync(token);
        Assert.False(Component.IsArmed);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(settings, token));
        Publish(new(StrokePattern.Insist, 9, 9, 9, 9));
        Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
        var notificationsBeforeReconnect = notifications;
        var reconnected = new DeviceState(StrokePattern.RoboStroke, 70, 70, 70, 70);
        context.Motion = Motion(reconnected);
        await context.ConnectAsync(Component, token);
        Assert.Equal(2, context.RequestsFor("control.acquire").Count);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.True(Component.IsArmed);
        Assert.Equal(settings.ToCommands().Append(new(FirmwareSetting.Speed, 0)), Writes());
        Assert.Equal(reconnected, await Component.GetStateAsync(cancellationToken: token));
        Assert.True(notifications > notificationsBeforeReconnect);
        Publish(reconnected);
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task ConnectionFailureClearsOnlyAfterSuccessfulReconnect()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.PublishFrame([1]);
        var failure = Assert.IsType<InvalidDataException>(Component.ConnectionFailure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() => Component.ResetAsync(token)));
        Assert.Same(failure, Component.ConnectionFailure);
        Assert.Equal(0, context.DisconnectCalls);
        await context.ConnectAsync(Component, token);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Equal(2, context.RequestsFor("control.acquire").Count);
        Assert.Null(Component.ConnectionFailure);
        Assert.True(Component.IsArmed);
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task CleanupFailurePropagatesWithoutOpeningAnotherConnection()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var failure = new IOException("Disconnect failed.");
        context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Throws(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.DisconnectAsync(token)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.ConnectAsync(token)));
        Assert.Single(context.RequestsFor("control.acquire"));
        Assert.False(Component.IsArmed);
        RestoreDisconnect();
        await context.ConnectAsync(Component, token);
        Assert.Equal(2, context.RequestsFor("control.acquire").Count);
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task StopDuringSetupInterruptsConnectionWithoutSendingACommand()
    {
        using var protocol = new Subject<BleCharacteristicResult>();
        HoldProtocol(protocol);
        var token = TestContext.Current.CancellationToken;
        var connect = Component.ConnectAsync(token);
        await Component.EmergencyStopAsync(token);
        Assert.False(connect.IsCompleted);
        Assert.Empty(Writes());
        protocol.OnNext(new(BleTestContext.Characteristic(BleProtocolConstants.ProtocolInfo), BleCharacteristicEvent.Read, context.ProtocolInfo));
        protocol.OnCompleted();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.False(Component.IsArmed);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task ControlObservationsDuringSetupDoNotPreventArming()
    {
        var token = TestContext.Current.CancellationToken;
        var observed = new DeviceState(StrokePattern.TeasingPounding, 10, 10, 10, 10);
        var connect = Component.ConnectAsync(token);
        Publish(observed);
        await connect;
        Assert.True(Component.IsArmed);
        Assert.Equal(observed, await Component.GetStateAsync(cancellationToken: token));
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task ConnectionFailureDuringSetupFailsAttemptAndReleasesResources()
    {
        var failure = new IOException("Subscription lost.");
        var connect = Component.ConnectAsync(TestContext.Current.CancellationToken);
        context.Responses.OnError(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => connect));
        Assert.Same(failure, Component.ConnectionFailure);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.False(Component.IsArmed);
    }

    [Fact]
    public async Task DisconnectDuringSetupFailsAttemptAndReleasesResourcesOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var connect = Component.ConnectAsync(token);
        var disconnect = Component.DisconnectAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await disconnect;
        Assert.Equal(1, context.DisconnectCalls);
        Assert.False(Component.IsArmed);
    }

    [Fact]
    public async Task DisposalCancelsSetupAndPreventsReconnection()
    {
        var token = TestContext.Current.CancellationToken;
        var connect = Component.ConnectAsync(token);
        await Component.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.Equal(1, context.DisconnectCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Component.ConnectAsync(token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Component.DisconnectAsync(token));
    }

    [Fact]
    public async Task ConnectionFailureDisarmsWithoutAbandoningIoAndCannotBeReset()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var failure = new IOException("Bluetooth disconnected.");
        try
        {
            context.Responses.OnError(failure);
            Assert.False(Component.IsArmed);
            Assert.False(set.IsCompleted);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.ResetAsync(token)));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.GetStateAsync(true, token)));
            Publish(new(StrokePattern.Deeper, 50, 50, 50, 50));
            Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
        }
        finally
        {
            Settle(transfer);
        }

        var error = await Assert.ThrowsAsync<DeviceWriteException>(() => set);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(0, error.CommandsWritten);
        Assert.Single(Writes());
    }

    [Fact]
    public async Task ConnectionFailureInvalidatesResetQueuedBehindIo()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var reset = Component.ResetAsync(token);
        var version = ConnectionVersion();
        var failure = new IOException("Bluetooth disconnected.");
        try
        {
            context.Responses.OnError(failure);
            Assert.True(ConnectionVersion() > version);
        }
        finally
        {
            Settle(transfer);
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => reset));
        await Assert.ThrowsAsync<DeviceWriteException>(() => set);
        Assert.False(Component.IsArmed);
    }

    [Fact]
    public async Task LiveReadIsSerializedAndCachedReadDoesNotIssueIo()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var held = HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var live = Component.GetStateAsync(true, token);
        var observed = new DeviceState(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        try
        {
            Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
            Publish(observed);
            Assert.False(live.IsCompleted);
            Assert.Equal(observed, await Component.GetStateAsync(cancellationToken: token));
        }
        finally
        {
            context.Complete(await held.Task.WaitAsync(token));
            Settle(transfer);
            await set;
        }

        context.Motion = Motion(observed);
        Assert.Equal(observed, await context.StreamUntilAsync(live, token));
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task FailedLiveReadPropagatesWithoutCachedSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var read = Component.GetStateAsync(true, token);
        context.PublishFrame([1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => read);
        Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
        Assert.False(Component.IsArmed);
        Assert.Empty(Writes());
    }

    [Fact]
    public async Task WriteFailurePreservesPartialProgressAndDisarms()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("write_failed", "motion.depth");
        var settings = new MotionSettings(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        var error = await Assert.ThrowsAsync<DeviceWriteException>(() => Component.SetAsync(settings, token));
        Assert.Equal(2, error.CommandsWritten);
        Assert.Equal(new(FirmwareSetting.Depth, 40), Writes()[2]);
        Assert.Contains("write_failed", error.InnerException!.Message);
        Assert.Equal(3, Writes().Count);
        Assert.False(Component.IsArmed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(settings, token));
        Assert.Equal(3, Writes().Count);
    }

    [Fact]
    public async Task ControlObservationDoesNotCancelPendingSettingsOrIssueStop()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var active = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var settings = new MotionSettings(StrokePattern.RoboStroke, 10, 10, 10, 10);
        var pending = Component.SetAsync(settings, token);
        var observed = new DeviceState(StrokePattern.TeasingPounding, 0, 99, 0, 0);
        try
        {
            Publish(observed);
            Assert.True(Component.IsArmed);
            Assert.False(pending.IsCompleted);
            Assert.False(active.IsCompleted);
            Assert.Equal(observed, await Component.GetStateAsync(cancellationToken: token));
        }
        finally
        {
            Settle(transfer);
        }

        Assert.Equal(new(SetStatus.Superseded, 0), await active);
        Assert.Equal(new(SetStatus.Completed, 5), await pending);
        Assert.Equal(new[] { new FirmwareCommand(FirmwareSetting.Pattern, 1) }.Concat(settings.ToCommands()), Writes());
        Assert.Equal(observed, await Component.GetStateAsync(cancellationToken: token));
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task QueuedResetCannotUndoLaterStop()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var reset = Component.ResetAsync(token);
        var stop = Component.EmergencyStopAsync(token);
        Settle(transfer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reset);
        await set;
        await stop;
        Assert.True(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task ResetDuringStopIsRejectedEvenWhenSettingsHoldIo()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var setting = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var stop = Component.EmergencyStopAsync(token);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ResetAsync(token));
            Assert.Contains("Stop", failure.Message);
            Assert.True(Component.IsArmed);
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            Settle(transfer);
            await stop;
            await setting;
        }

        Assert.True(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task FailedStopStillCancelsSettingsAndRequiresExplicitReset()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("stop_failed");
        await Assert.ThrowsAsync<IOException>(() => Component.EmergencyStopAsync(token));
        Assert.False(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        await Component.ResetAsync(token);
        Assert.True(Component.IsArmed);
        Assert.Single(Writes());
        context.Write = null;
    }

    [Fact]
    public async Task FailedStopDuringDisconnectStillReleasesResourcesForReconnect()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("stop_failed");
        await Assert.ThrowsAsync<IOException>(() => Component.DisconnectAsync(token));
        Assert.Equal(1, context.DisconnectCalls);
        Assert.False(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        context.Write = null;
        await context.ConnectAsync(Component, token);
        Assert.Equal(2, context.RequestsFor("control.acquire").Count);
        Assert.True(Component.IsArmed);
        Assert.Single(Writes());
    }

    [Fact]
    public async Task FailedStopAndCleanupDuringDisconnectAreBothReportedAndCleanupCanBeRetried()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("stop_failed");
        var cleanupFailure = new IOException("Cleanup failed.");
        context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Throws(cleanupFailure);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => Component.DisconnectAsync(token));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains("stop_failed", failure.InnerExceptions[0].Message);
        Assert.Same(cleanupFailure, failure.InnerExceptions[1]);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        Assert.False(Component.IsArmed);
        context.Write = null;
        RestoreDisconnect();
        await context.ConnectAsync(Component, token);
        Assert.Equal(2, context.RequestsFor("control.acquire").Count);
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task DisposeWaitsForIssuedIoAndDisconnectsExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var active = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var pending = Component.SetAsync(new(StrokePattern.RoboStroke, 10, 10, 10, 10), token);
        var dispose = Component.DisposeAsync().AsTask();
        try
        {
            Assert.False(Component.IsArmed);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, context.DisconnectCalls);
        }
        finally
        {
            Settle(transfer);
            await dispose;
            await Component.DisposeAsync();
        }

        Assert.Equal(new(SetStatus.Cancelled, 0), await pending);
        Assert.Equal(new(SetStatus.Superseded, 0), await active);
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Component.ResetAsync(token));
    }

    [Fact]
    public async Task FailedConnectionReleasesPartialProviderResourcesAndCanBeRetried()
    {
        var failure = new IOException("Connection failed.");
        context.Peripheral.Setup(peripheral => peripheral.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo))
            .Returns(Observable.Throw<BleCharacteristicResult>(failure));
        var token = TestContext.Current.CancellationToken;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.ConnectAsync(token)));
        Assert.False(Component.IsArmed);
        Assert.Empty(Writes());
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.ConnectAsync(token)));
        Assert.Equal(2, context.DisconnectCalls);
    }

    [Fact]
    public async Task FailedCleanupAfterFailedSetupReportsBothErrors()
    {
        var failure = new IOException("Connection failed.");
        var cleanupFailure = new IOException("Cleanup failed.");
        context.Peripheral.Setup(peripheral => peripheral.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo))
            .Returns(Observable.Throw<BleCharacteristicResult>(failure));
        context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Throws(cleanupFailure);
        var error = await Assert.ThrowsAsync<AggregateException>(() => Component.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal([failure, cleanupFailure], error.InnerExceptions);
        Assert.False(Component.IsArmed);
        RestoreDisconnect();
    }

    [Fact]
    public async Task DisconnectFailureIsPreservedForRepeatedDisposal()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var failure = new IOException("Disconnect failed.");
        context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Throws(failure);
        expectedDisposalFailure = failure;
        var first = Component.DisposeAsync().AsTask();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first));
        var second = Component.DisposeAsync().AsTask();
        Assert.Same(first, second);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => second));
        Assert.False(Component.IsArmed);
        context.Status.OnNext(ConnectionState.Disconnected);
    }

    [Fact]
    public async Task StateEventsReflectChangesAndSubscriberErrorsDoNotInterruptExecution()
    {
        var token = TestContext.Current.CancellationToken;
        var notifications = 0;
        Component.StateChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        Component.StateChanged += (_, _) => notifications++;
        await context.ConnectAsync(Component, token);
        await Component.ResetAsync(token);
        await Component.ResetAsync(token);
        Publish(Initial);
        Assert.Equal(2, notifications);
        await Component.SetAsync(new(0, 0, 20, 0, 0), token);
        Publish(new(0, 0, 20, 0, 0));
        Assert.Equal(3, notifications);
        Assert.True(Component.IsArmed);
        Publish(new(0, 0, 25, 0, 0));
        Assert.Equal(4, notifications);
        Assert.Equal(new DeviceState(0, 0, 25, 0, 0), await Component.GetStateAsync(cancellationToken: token));
        Publish(new(0, 0, 25, 0, 0));
        await Component.EmergencyStopAsync(token);
        Assert.Equal(4, notifications);
        Assert.True(Component.IsArmed);
        Assert.Contains(logger.Entries, entry => entry.Exception?.Message == "Subscriber failed.");
    }

    [Fact]
    public async Task DelayedAndRevertedObservationsOnlyUpdateLastKnownState()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        await Component.SetAsync(new(0, 0, 20, 0, 0), token);
        await Component.SetAsync(new(0, 0, 30, 0, 0), token);
        foreach (var stroke in new[] { 20, 30, 20 })
        {
            var observation = new DeviceState(0, 0, stroke, 0, 0);
            Publish(observation);
            Assert.True(Component.IsArmed);
            Assert.Equal(observation, await Component.GetStateAsync(cancellationToken: token));
        }

        Assert.Equal(10, Writes().Count);
    }

    [Fact]
    public async Task ObservationsDuringModeSelectionDoNotChangeArming()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        await Component.SetAsync(new(0, 0, 20, 0, 0), token);
        await Component.SetAsync(new(0, 0, 30, 0, 0), token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) == "target.set")
            {
                Publish(new(0, 0, 20, 0, 0));
            }

            return null;
        };
        Assert.Equal(new(SetStatus.Completed, 5), await Component.SetAsync(new(0, 0, 40, 0, 0), token));
        foreach (var stroke in new[] { 30, 40, 30 })
        {
            Publish(new(0, 0, stroke, 0, 0));
            Assert.True(Component.IsArmed);
        }

        Assert.Equal(new DeviceState(0, 0, 30, 0, 0), await Component.GetStateAsync(cancellationToken: token));
        Assert.Equal(15, Writes().Count);
    }

    [Fact]
    public async Task ReportedSpeedChangesDoNotDisarm()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        await Component.SetAsync(new(0, 20, 100, 0, 0), token);
        Publish(new(0, 7, 100, 0, 0));
        Assert.True(Component.IsArmed);
        Assert.Equal(7, (await Component.GetStateAsync(cancellationToken: token))!.Speed);
        Publish(new(0, 13, 100, 0, 0));
        Assert.True(Component.IsArmed);
        Assert.Equal(new DeviceState(0, 13, 100, 0, 0), await Component.GetStateAsync(cancellationToken: token));
    }

    [Fact]
    public async Task ModeSelectionPrecedesSettingsAndItsObservationsDoNotDisarm()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        await Component.SetAsync(new(0, 0, 20, 0, 0), token);
        Publish(new(0, 0, 20, 0, 0));
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) == "target.set")
            {
                Assert.Equal(5, Writes().Count);
                Publish(new(0, 0, 50, 10, 50));
            }

            return null;
        };
        var settings = new MotionSettings(StrokePattern.Deeper, 5, 5, 5, 5);
        Assert.Equal(new(SetStatus.Completed, 5), await Component.SetAsync(settings, token));
        Publish(new(StrokePattern.Deeper, 1, 5, 5, 5));
        Assert.Equal(2, context.RequestsFor("target.set").Count);
        Assert.Equal(settings.ToCommands(), Writes().Skip(5));
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task SetWhenPatternsCannotRunSendsNoSettingsAndStaysArmed()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.State = "strokeEngine.preflight";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(new(0, 5, 5, 5, 5), token));
        Assert.Contains("not ready to run patterns", error.Message);
        Assert.Empty(Writes());
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task FailedPatternPreparationDisarmsWithoutWriting()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        context.Write = request =>
        {
            if (BleTestContext.Operation(request) != "target.set")
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: "rejected");
            return Observable.Return(BleTestContext.WriteResult([]));
        };
        var error = await Assert.ThrowsAsync<IOException>(() => Component.SetAsync(new(0, 5, 5, 5, 5), token));
        Assert.IsNotType<DeviceWriteException>(error);
        Assert.Contains("no settings were sent", error.Message);
        Assert.Contains("rejected", error.Message);
        Assert.Empty(Writes());
        Assert.False(Component.IsArmed);
    }

    [Fact]
    public async Task ResetWhileArmedDoesNotReplaySettings()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        await Component.SetAsync(new(0, 20, 0, 0, 0), token);
        await Component.ResetAsync(token);
        Publish(new(0, 0, 0, 0, 0));
        Assert.True(Component.IsArmed);
        Assert.Equal(5, Writes().Count);
    }

    [Fact]
    public async Task CallerCancellationWaitsForIssuedTransferAndPreservesConfirmedCount()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), cancellation.Token);
        try
        {
            await cancellation.CancelAsync();
            Assert.False(set.IsCompleted);
        }
        finally
        {
            Settle(transfer);
        }

        Assert.Equal(new(SetStatus.Cancelled, 0), await set);
        Assert.Single(Writes());
        Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
        Assert.True(Component.IsArmed);
    }

    [Theory]
    [InlineData(false, SetStatus.Superseded)]
    [InlineData(true, SetStatus.Cancelled)]
    public async Task FinalWritePreservesInterruptionOutcome(bool stopRequested, SetStatus expectedOutcome)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        context.Write = request => BleTestContext.IsMotionWrite(request) && Writes().Count == 5 ? transfer : null;
        var first = Component.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        Task next = stopRequested
            ? Component.EmergencyStopAsync(token)
            : Component.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        try
        {
            Assert.False(first.IsCompleted);
        }
        finally
        {
            Settle(transfer);
            await next;
        }

        Assert.Equal(new(expectedOutcome, 4), await first);
    }

    [Fact]
    public async Task ConnectArmsWithoutSendingMotionAndResetSendsNothing()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Null(await Component.GetStateAsync(cancellationToken: token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(new(0, 20, 30, 40, 50), token));
        await context.ConnectAsync(Component, token);
        Assert.True(Component.IsArmed);
        Assert.Empty(Writes());
        await Component.EmergencyStopAsync(token);
        Assert.True(Component.IsArmed);
        await Component.ResetAsync(token);
        Assert.True(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        Assert.Single(context.RequestsFor("stream.start"));
    }

    [Fact]
    public async Task SuccessfulStopPreservesArmingAndAllowsNextSetWithoutReset()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var settings = new MotionSettings(StrokePattern.StopNGo, 20, 30, 40, 50);
        await Component.EmergencyStopAsync(token);
        var result = await Component.SetAsync(settings, token);
        Assert.True(Component.IsArmed);
        Assert.Equal(new(SetStatus.Completed, 5), result);
        Assert.Equal(new[] { new FirmwareCommand(FirmwareSetting.Speed, 0) }.Concat(settings.ToCommands()), Writes());
    }

    [Fact]
    public async Task SetDuringStopIsRejectedWithoutDisarming()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var stop = Component.EmergencyStopAsync(token);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Component.SetAsync(new(StrokePattern.StopNGo, 20, 30, 40, 50), token));
            Assert.Contains("Stop", failure.Message);
            Assert.True(Component.IsArmed);
        }
        finally
        {
            context.Complete(context.RequestsFor("setting.write", "motion.")[0]);
            Settle(transfer);
            await stop;
        }
    }

    [Fact]
    public async Task SuccessfulStopDoesNotRearmDisarmedDevice()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("write_failed", "motion.pattern");
        await Assert.ThrowsAsync<DeviceWriteException>(() => Component.SetAsync(new(0, 20, 30, 40, 50), token));
        context.Write = null;
        await Component.EmergencyStopAsync(token);
        Assert.False(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Pattern, 0), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task WritesOrderedPrimitivesWithoutInventingObservedState()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var settings = new MotionSettings(StrokePattern.StopNGo, 20, 30, 40, 50);
        var result = await Component.SetAsync(settings, token);
        Assert.Equal(new(SetStatus.Completed, 5), result);
        Assert.Equal(settings.ToCommands(), Writes());
        Assert.Equal(Initial, await Component.GetStateAsync(cancellationToken: token));
        Assert.True(Component.IsArmed);
    }

    [Fact]
    public async Task SupersessionWaitsForIssuedIoAndSkipsOlderPendingSettings()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var first = Component.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        var second = Component.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        var latestSettings = new MotionSettings(StrokePattern.HalfNHalf, 30, 30, 30, 30);
        var latest = Component.SetAsync(latestSettings, token);
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(latest.IsCompleted);
            Assert.Equal(new(SetStatus.Superseded, 0), await second);
            Assert.Equal([new(FirmwareSetting.Pattern, 1)], Writes());
        }
        finally
        {
            Settle(transfer);
        }

        Assert.Equal(new(SetStatus.Superseded, 0), await first);
        Assert.Equal(new(SetStatus.Completed, 5), await latest);
        Assert.Equal(new[] { new FirmwareCommand(FirmwareSetting.Pattern, 1) }.Concat(latestSettings.ToCommands()), Writes());
    }

    [Fact]
    public async Task StopIsSentWithoutWaitingForCommandInProgressAndDoesNotReplayOnReset()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var held = HoldFirstMotion();
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        await held.Task.WaitAsync(token);
        await Component.EmergencyStopAsync(token);
        Assert.Equal(new(SetStatus.Cancelled, 0), await set);
        Assert.True(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
        await Component.ResetAsync(token);
        Assert.True(Component.IsArmed);
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task StopDropsQueuedSettingsWhileAnIssuedWriteSettles()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var active = Component.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        var queued = Component.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        var stop = Component.EmergencyStopAsync(token);
        try
        {
            Assert.False(active.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Equal(new(SetStatus.Cancelled, 0), await queued);
            Assert.Equal([new(FirmwareSetting.Pattern, 1)], Writes());
        }
        finally
        {
            Settle(transfer);
        }

        await stop;
        Assert.Equal(new(SetStatus.Superseded, 0), await active);
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task StopOvertakesRenewalQueuedBehindIssuedMotionTransfer()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        var held = HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        await held.Task.WaitAsync(token);
        context.Clock.Advance(TimeSpan.FromSeconds(5));
        while (!logger.Entries.Any(entry => entry.Id.Id == 23 && entry.Message.Contains("control.renew", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var stop = Component.EmergencyStopAsync(token);
        Settle(transfer);
        await stop;
        while (!context.Requests.Any(request => BleTestContext.Operation(request) == "control.renew"))
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        Assert.Equal(new(SetStatus.Cancelled, 0), await set);
        var requests = context.Requests.ToList();
        var issuedIndex = requests.FindIndex(request => BleTestContext.IsMotionWrite(request)
            && request["path"]!.GetValue<string>() == "motion.pattern");
        var stopIndex = requests.FindIndex(request => BleTestContext.IsMotionWrite(request)
            && request["path"]!.GetValue<string>() == "motion.speed"
            && request["args"]!["value"]!.GetValue<int>() == 0);
        var renewalIndex = requests.FindIndex(request => BleTestContext.Operation(request) == "control.renew");
        Assert.InRange(stopIndex, issuedIndex + 1, renewalIndex - 1);
    }

    [Fact]
    public async Task ConcurrentStopsShareFailureAndRejectResetUntilSettled()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var held = HoldFirstMotion();
        var first = Component.EmergencyStopAsync(token);
        var second = Component.EmergencyStopAsync(token);
        try
        {
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Component.ResetAsync(token));
            Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        }
        finally
        {
            context.Respond(await held.Task.WaitAsync(token), "failed", ok: false, code: "stop_failed");
        }

        var failure = await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => second));
        Assert.Single(Writes());
        context.Write = null;
    }

    [Fact]
    public async Task StopIsSentWithoutWaitingForLiveRead()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var live = Component.GetStateAsync(true, token);
        try
        {
            await Component.EmergencyStopAsync(token);
            Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
            Assert.False(live.IsCompleted);
        }
        finally
        {
            Publish(Initial);
            await live;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrDisposalAbandonsLiveRead(bool dispose)
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var live = Component.GetStateAsync(true, token);
        var cleanup = dispose ? Component.DisposeAsync().AsTask() : Component.DisconnectAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live);
        await cleanup;
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task DisconnectAndDisposalWaitForStopInProgress()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        var held = HoldFirstMotion();
        var stop = Component.EmergencyStopAsync(token);
        var disconnect = Component.DisconnectAsync(token);
        var dispose = Component.DisposeAsync().AsTask();
        try
        {
            Assert.False(disconnect.IsCompleted);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, context.DisconnectCalls);
        }
        finally
        {
            context.Complete(await held.Task.WaitAsync(token));
            await stop;
            await disconnect;
            await dispose;
        }

        Assert.Equal(1, context.DisconnectCalls);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
    }

    [Fact]
    public async Task DisposalStopsAndWaitsForIssuedIoBeforeCleanup()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        using var transfer = new Subject<BleCharacteristicResult>();
        HoldFirstMotion(transfer);
        var set = Component.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var dispose = Component.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted);
            Assert.Equal([new(FirmwareSetting.Pattern, 1)], Writes());
            Assert.Equal(0, context.DisconnectCalls);
        }
        finally
        {
            Settle(transfer);
            await dispose;
        }

        Assert.Equal(new(SetStatus.Cancelled, 0), await set);
        Assert.Equal(["setting.write", "setting.write", "stream.stop", "control.release"],
            context.Requests.SkipWhile(request => !BleTestContext.IsMotionWrite(request)).Select(BleTestContext.Operation));
        Assert.Equal([new(FirmwareSetting.Pattern, 1), new(FirmwareSetting.Speed, 0)], Writes());
        Assert.Equal(1, context.DisconnectCalls);
    }

    [Fact]
    public async Task FailedStopDuringDisposalPropagatesAfterCleanup()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("stop_failed");
        var disposal = Component.DisposeAsync().AsTask();
        var failure = await Assert.ThrowsAsync<IOException>(() => disposal);
        expectedDisposalFailure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Component.DisposeAsync().AsTask()));
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        Assert.Equal(1, context.DisconnectCalls);
        Assert.Single(context.RequestsFor("control.release"));
    }

    [Fact]
    public async Task FailedStopAndCleanupDuringDisposalAreBothReported()
    {
        var token = TestContext.Current.CancellationToken;
        await context.ConnectAsync(Component, token);
        RejectMotion("stop_failed");
        var cleanupFailure = new IOException("Cleanup failed.");
        context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Throws(cleanupFailure);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => Component.DisposeAsync().AsTask());
        expectedDisposalFailure = failure;
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains("stop_failed", failure.InnerExceptions[0].Message);
        Assert.Same(cleanupFailure, failure.InnerExceptions[1]);
        Assert.Equal([new(FirmwareSetting.Speed, 0)], Writes());
        context.Status.OnNext(ConnectionState.Disconnected);
    }

    private static string Motion(DeviceState observation) => new JsonObject
    {
        ["pattern"] = (int)observation.Pattern,
        ["speed"] = observation.CurrentDeviceVelocityPercent,
        ["stroke"] = observation.Stroke,
        ["depth"] = observation.Depth,
        ["sensation"] = observation.Sensation,
    }.ToJsonString();

    private static void Settle(Subject<BleCharacteristicResult> transfer)
    {
        transfer.OnNext(BleTestContext.WriteResult([]));
        transfer.OnCompleted();
    }

    private void Publish(DeviceState observation) => context.Publish(Motion(observation));

    private List<FirmwareCommand> Writes() => context.RequestsFor("setting.write", "motion.")
        .Select(request => new FirmwareCommand(request["path"]!.GetValue<string>() switch
        {
            "motion.pattern" => FirmwareSetting.Pattern,
            "motion.speed" => FirmwareSetting.Speed,
            "motion.stroke" => FirmwareSetting.Stroke,
            "motion.depth" => FirmwareSetting.Depth,
            "motion.sensation" => FirmwareSetting.Sensation,
            _ => throw new InvalidOperationException("Unknown firmware control."),
        }, request["args"]!["value"]!.GetValue<int>())).ToList();

    private long ConnectionVersion() => (long)typeof(OssmDevice)
        .GetField("connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(Component)!;

    private TaskCompletionSource<JsonObject> HoldFirstMotion(IObservable<BleCharacteristicResult>? transfer = null)
    {
        var held = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Write = request => BleTestContext.IsMotionWrite(request) && held.TrySetResult(request)
            ? transfer ?? Observable.Return(BleTestContext.WriteResult([])) : null;
        return held;
    }

    private void HoldProtocol(IObservable<BleCharacteristicResult> protocol)
        => context.Peripheral.Setup(peripheral => peripheral.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo)).Returns(protocol);

    private void RejectMotion(string code, string? path = null)
    {
        context.Write = request =>
        {
            if (!BleTestContext.IsMotionWrite(request) || (path is not null && request["path"]!.GetValue<string>() != path))
            {
                return null;
            }

            context.Respond(request, "failed", ok: false, code: code);
            return Observable.Return(BleTestContext.WriteResult([]));
        };
    }

    private void RestoreDisconnect() => context.Peripheral.Setup(peripheral => peripheral.CancelConnection()).Callback(() => context.Status.OnNext(ConnectionState.Disconnected));
}
