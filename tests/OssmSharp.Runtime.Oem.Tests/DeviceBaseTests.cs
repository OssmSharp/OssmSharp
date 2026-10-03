using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem.Protocol;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceBaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedSpeedResponseDoesNotBecomeConfirmed(bool superseded)
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await device.ConnectAsync(token);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (command, writeToken) => command is { Setting: FirmwareSetting.Speed, Value: 6 }
            ? pending.Task.WaitAsync(writeToken) : Task.CompletedTask;
        var original = device.SetAsync(new(0, 20, 30, 40, 50), cancellation.Token);
        try
        {
            Assert.False(original.IsCompleted);
            Assert.Equal(new FirmwareSettings(0, null, 30, 40, 50), device.Transmitted);
            if (superseded)
            {
                await device.SetAsync(new(StrokePattern.RoboStroke, 60, 70, 80, 90), token);
            }
            else
            {
                await cancellation.CancelAsync();
            }

            var result = await original;
            Assert.Equal(superseded ? SetStatus.Superseded : SetStatus.Cancelled, result.Status);
            Assert.Equal(4, result.CommandsWritten);
            var expected = superseded
                ? new FirmwareSettings(2, 42, 70, 80, 90)
                : new FirmwareSettings(0, null, 30, 40, 50);
            Assert.Equal(expected, device.Transmitted);
        }
        finally
        {
            pending.TrySetResult();
            await original;
        }
    }

    [Fact]
    public async Task FailedStopRetainsPreviouslyConfirmedSpeed()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.SetAsync(new(0, 20, 30, 40, 50), token);
        device.Write = (_, _) => Task.FromException(new IOException("Stop failed."));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => device.EmergencyStopAsync(token));
            Assert.Equal(new FirmwareSettings(0, 6, 30, 40, 50), device.Transmitted);
            Assert.False(device.IsArmed);
        }
        finally
        {
            device.Write = null;
        }
    }

    [Fact]
    public async Task ConfirmedControlsDoNotUseObservationsAndResetOnReconnect()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        Assert.False(device.IsConnected);
        await device.ConnectAsync(token);
        Assert.True(device.IsConnected);
        Assert.Null(device.Transmitted);
        device.Observe(new(StrokePattern.SimpleStroke, 80, 70, 60, 50));
        Assert.Null(device.Transmitted);

        await device.SetAsync(new(StrokePattern.SimpleStroke, 20, 30, 40, 50), token);
        Assert.Equal(new FirmwareSettings(0, 6, 30, 40, 50), device.Transmitted);
        await device.EmergencyStopAsync(token);
        Assert.Equal(0, device.Transmitted?.DeviceVelocityPercent);
        await device.DisconnectAsync(token);
        Assert.False(device.IsConnected);
        await device.ConnectAsync(token);
        Assert.Null(device.Transmitted);
    }

    [Fact]
    public async Task FailedWriteExposesOnlyConfirmedPrefix()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        device.Write = (command, _) => command.Setting == FirmwareSetting.Depth
            ? Task.FromException(new IOException("Write failed.")) : Task.CompletedTask;
        try
        {
            var error = await Assert.ThrowsAsync<DeviceWriteException>(() => device.SetAsync(new(0, 20, 30, 40, 50), token));
            Assert.Equal(2, error.CommandsWritten);
            Assert.Equal(new FirmwareSettings(0, null, 30, null, null), device.Transmitted);
        }
        finally
        {
            device.Write = null;
        }
    }

    [Fact]
    public async Task UnconfirmedSpeedDoesNotAppearAndLateResponseCannotOverwriteStop()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (command, _) => command is { Setting: FirmwareSetting.Speed, Value: > 0 }
            ? pending.Task : Task.CompletedTask;
        var set = device.SetAsync(new(0, 20, 30, 40, 50), token);
        try
        {
            Assert.False(set.IsCompleted);
            Assert.Null(device.Transmitted?.DeviceVelocityPercent);
            await device.EmergencyStopAsync(token);
            Assert.Equal(0, device.Transmitted?.DeviceVelocityPercent);
        }
        finally
        {
            pending.TrySetResult();
            await set;
        }

        Assert.Equal(0, device.Transmitted?.DeviceVelocityPercent);
    }

    [Fact]
    public async Task LifecycleAndApiCallsLogAtInformationWhileCommandsLogAtDebug()
    {
        var logger = new RecordingLogger<DeviceBase>();
        await using var device = new TestDevice(logger);
        var token = TestContext.Current.CancellationToken;
        var settings = new MotionSettings(StrokePattern.SimpleStroke, 20, 30, 40, 50);

        await device.ConnectAsync(token);
        var result = await device.SetAsync(settings, token);
        await device.EmergencyStopAsync(token);
        await device.DisconnectAsync(token);

        Assert.Equal(SetStatus.Completed, result.Status);
        Assert.Equal(
            ["connecting", "connected", "disconnecting", "disconnected"],
            logger.Entries.Where(entry => entry.Id.Id == 18 && entry.Level == LogLevel.Information)
                .Select(entry => entry.StructuredState.Single(field => field.Key == "ConnectionState").Value));
        Assert.Equal(
            ["Set", "Stop"],
            logger.Entries.Where(entry => entry.Id.Id == 17 && entry.Level == LogLevel.Information)
                .Select(entry => entry.StructuredState.Single(field => field.Key == "Operation").Value));
        var finished = Assert.Single(logger.Entries, entry => entry.Id.Id == 19);
        Assert.Equal(LogLevel.Information, finished.Level);
        Assert.Contains(finished.StructuredState, field => field.Key == "Status" && Equals(field.Value, SetStatus.Completed));
        Assert.Contains(finished.StructuredState, field => field.Key == "CommandsWritten" && Equals(field.Value, result.CommandsWritten));
        Assert.Contains(logger.Entries, entry => entry.Id.Id == 20 && entry.Level == LogLevel.Information);
        Assert.Equal(
            settings.ToCommands().Select(command => command.Setting),
            logger.Entries.Where(entry => entry.Id.Id == 21 && entry.Level == LogLevel.Debug)
                .Select(entry => (FirmwareSetting)entry.StructuredState.Single(field => field.Key == "Setting").Value!));
        Assert.Equal(result.CommandsWritten, logger.Entries.Count(entry => entry.Id.Id == 22 && entry.Level == LogLevel.Debug));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Id.Id is 14 or 21 or 22);
    }

    [Fact]
    public async Task PendingInitialObservationLogsWaitWithoutConnectedState()
    {
        var logger = new RecordingLogger<DeviceBase>();
        var observation = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice(logger) { Read = _ => observation.Task };
        var connect = device.ConnectAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(connect.IsCompleted);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Id.Id == 14
                && entry.Message.Contains("waiting for initial motion observation", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id == 18
                && entry.StructuredState.Any(field => field.Key == "ConnectionState" && Equals(field.Value, "connected")));
        }
        finally
        {
            observation.TrySetResult(device.LiveState);
            await connect;
        }

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Id.Id == 18
            && entry.StructuredState.Any(field => field.Key == "ConnectionState" && Equals(field.Value, "connected")));
    }

    [Fact]
    public async Task FailedStopLogsCallAndFailureWithoutCompletion()
    {
        var logger = new RecordingLogger<DeviceBase>();
        await using var device = new TestDevice(logger);
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var failure = new IOException("Write failed.");
        device.Write = (_, _) => Task.FromException(failure);
        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => device.EmergencyStopAsync(token)));
            Assert.Contains(logger.Entries, entry => entry.Id.Id == 17 && entry.Level == LogLevel.Information);
            Assert.Contains(logger.Entries, entry => entry.Id.Id == 2 && entry.Level == LogLevel.Error && entry.Exception == failure);
            Assert.DoesNotContain(logger.Entries, entry => entry.Id.Id == 20);
        }
        finally
        {
            device.Write = null;
        }
    }

    [Fact]
    public async Task DisconnectPreservesClientAndReconnectArmsFreshStateWithoutReplay()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        var notifications = 0;
        device.StateChanged += (_, _) => notifications++;
        await device.ConnectAsync(token);
        var settings = new MotionSettings(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        await device.SetAsync(settings, token);

        await device.DisconnectAsync(token);

        device.IsArmed.Should().BeFalse();
        device.Disconnects.Should().Be(1);
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 0, 0, 0)));
        var set = () => device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        await set.Should().ThrowAsync<InvalidOperationException>();
        device.Observe(new(StrokePattern.Insist, 9, 9, 9, 9));
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 0, 0, 0)));
        var notificationsBeforeReconnect = notifications;
        var reconnected = new MotionSettings(StrokePattern.RoboStroke, 70, 70, 70, 70);
        device.LiveState = new(reconnected);

        await device.ConnectAsync(token);

        device.Connects.Should().Be(2);
        device.Disconnects.Should().Be(1);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(settings.ToCommands().Append(new FirmwareCommand(FirmwareSetting.Speed, 0)));
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(reconnected));
        notifications.Should().BeGreaterThan(notificationsBeforeReconnect);
        device.Observe(reconnected);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task ConnectionFailureClearsOnlyAfterSuccessfulReconnect()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var failure = new IOException("Connection lost.");

        device.Fail(failure);

        device.ConnectionFailure.Should().BeSameAs(failure);
        var reset = () => device.ResetAsync(token);
        (await reset.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        device.ConnectionFailure.Should().BeSameAs(failure);
        device.Disconnects.Should().Be(0);

        await device.ConnectAsync(token);

        device.Disconnects.Should().Be(1);
        device.Connects.Should().Be(2);
        device.ConnectionFailure.Should().BeNull();
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupFailurePropagatesWithoutOpeningAnotherConnection()
    {
        var failure = new IOException("Disconnect failed.");
        var cleanupFails = true;
        await using var device = new TestDevice { Disconnect = () => cleanupFails ? Task.FromException(failure) : Task.CompletedTask };
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);

        var disconnect = () => device.DisconnectAsync(token);
        (await disconnect.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        var connect = () => device.ConnectAsync(token);
        (await connect.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        device.Connects.Should().Be(1);
        device.IsArmed.Should().BeFalse();
        cleanupFails = false;
        await device.ConnectAsync(token);
        device.Connects.Should().Be(2);
        device.Disconnects.Should().Be(3);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task StopDuringSetupInterruptsConnectionWithoutSendingACommand()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice { Connect = _ => release.Task };
        var token = TestContext.Current.CancellationToken;
        var connect = device.ConnectAsync(token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            await stop;
            connect.IsCompleted.Should().BeFalse();
            device.Writes.Should().BeEmpty();
        }
        finally
        {
            release.TrySetResult();
        }

        var attempt = () => connect;
        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*interrupted*");
        device.IsArmed.Should().BeFalse();
        device.Disconnects.Should().Be(1);
        device.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task ControlObservationsDuringSetupDoNotPreventArming()
    {
        await using var device = new TestDevice();
        device.Connect = _ =>
        {
            device.Observe(new(StrokePattern.TeasingPounding, 10, 10, 10, 10));
            return Task.CompletedTask;
        };
        var token = TestContext.Current.CancellationToken;

        await device.ConnectAsync(token);

        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(device.LiveState);
        device.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectionFailureDuringSetupFailsAttemptAndReleasesResources()
    {
        var failure = new IOException("Subscription lost.");
        await using var device = new TestDevice();
        device.Connect = _ =>
        {
            device.Fail(failure);
            return Task.CompletedTask;
        };

        var connect = () => device.ConnectAsync(TestContext.Current.CancellationToken);

        (await connect.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        device.ConnectionFailure.Should().BeSameAs(failure);
        device.Disconnects.Should().Be(1);
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task DisconnectDuringSetupFailsAttemptAndReleasesResourcesOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice { Connect = _ => release.Task };
        var token = TestContext.Current.CancellationToken;
        var connect = device.ConnectAsync(token);
        var disconnect = device.DisconnectAsync(token);
        release.TrySetResult();

        var attempt = () => connect;
        await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*interrupted*");
        await disconnect;

        device.Disconnects.Should().Be(1);
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task DisposalCancelsSetupAndPreventsReconnection()
    {
        var device = new TestDevice { Connect = token => Task.Delay(Timeout.Infinite, token) };
        var connect = device.ConnectAsync(TestContext.Current.CancellationToken);

        await device.DisposeAsync();

        var attempt = () => connect;
        await attempt.Should().ThrowAsync<OperationCanceledException>();
        device.Disconnects.Should().Be(1);
        var reconnect = () => device.ConnectAsync(TestContext.Current.CancellationToken);
        await reconnect.Should().ThrowAsync<ObjectDisposedException>();
        var disconnect = () => device.DisconnectAsync(TestContext.Current.CancellationToken);
        await disconnect.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task ProviderFailureDisarmsWithoutAbandoningIoAndCannotBeReset()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => release.Task;
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var failure = new IOException("Provider disconnected.");

        try
        {
            device.Fail(failure);
            device.IsArmed.Should().BeFalse();
            set.IsCompleted.Should().BeFalse();
            var reset = () => device.ResetAsync(token);
            (await reset.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
            var live = () => device.GetStateAsync(true, token);
            (await live.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
            device.Observe(new(StrokePattern.Deeper, 50, 50, 50, 50));
            (await device.GetStateAsync(cancellationToken: token)).Should().Be(device.LiveState);
        }
        finally
        {
            release.TrySetResult();
        }

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
        device.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task LiveReadIsSerializedAndCachedReadDoesNotIssueIo()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var original = device.LiveState;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 1 ? release.Task : Task.CompletedTask;
        var settings = new MotionSettings(StrokePattern.TeasingPounding, 20, 30, 40, 50);
        var set = device.SetAsync(settings, token);
        var live = device.GetStateAsync(true, token);

        try
        {
            (await device.GetStateAsync(cancellationToken: token)).Should().Be(original);
            device.Reads.Should().Be(1);
            live.IsCompleted.Should().BeFalse();
            device.LiveState = new(settings);
        }
        finally
        {
            release.TrySetResult();
            await set;
        }

        (await live).Should().Be(new DeviceState(settings));
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(settings));
        device.Reads.Should().Be(2);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task FailedLiveReadPropagatesWithoutCachedSuccess()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var failure = new IOException("Read failed.");
        device.Read = _ => Task.FromException<DeviceState>(failure);

        var read = () => device.GetStateAsync(true, token);
        (await read.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        (await device.GetStateAsync(cancellationToken: token)).Should().Be(device.LiveState);
        device.IsArmed.Should().BeFalse();
        device.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteFailurePreservesPartialProgressAndDisarms()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var failure = new IOException("Write failed.");
        device.Write = (_, _) => device.Writes.Count == 3 ? Task.FromException(failure) : Task.CompletedTask;

        var set = () => device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var error = (await set.Should().ThrowAsync<DeviceWriteException>()).Which;

        error.CommandsWritten.Should().Be(2);
        error.Command.Should().Be(new FirmwareCommand(FirmwareSetting.Depth, 40));
        error.InnerException.Should().BeSameAs(failure);
        device.Writes.Should().HaveCount(3);
        device.IsArmed.Should().BeFalse();
        await set.Should().ThrowAsync<InvalidOperationException>();
        device.Writes.Should().HaveCount(3);
    }

    [Fact]
    public async Task ControlObservationDoesNotCancelPendingSettingsOrIssueStop()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => release.Task;
        var active = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var settings = new MotionSettings(StrokePattern.RoboStroke, 10, 10, 10, 10);
        var pending = device.SetAsync(settings, token);
        var observed = new MotionSettings(StrokePattern.TeasingPounding, 0, 99, 0, 0);

        try
        {
            device.Observe(observed);
            device.IsArmed.Should().BeTrue();
            pending.IsCompleted.Should().BeFalse();
            active.IsCompleted.Should().BeFalse();
            (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(observed));
        }
        finally
        {
            release.TrySetResult();
            await active;
        }

        (await active).Should().Be(new SetResult(SetStatus.Superseded, 1));
        (await pending).Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Writes.Should().Equal(new[] { new FirmwareCommand(FirmwareSetting.Pattern, 1) }.Concat(settings.ToCommands()));
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(observed));
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task QueuedResetCannotUndoLaterStop()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 1 ? release.Task : Task.CompletedTask;
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var reset = device.ResetAsync(token);
        var stop = device.EmergencyStopAsync(token);
        release.TrySetResult();

        var resetResult = () => reset;
        await resetResult.Should().ThrowAsync<InvalidOperationException>();
        await set;
        await stop;

        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task ResetDuringStopIsRejectedEvenWhenSettingsHoldIo()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var releaseSetting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (command, _) => command.Setting == FirmwareSetting.Pattern ? releaseSetting.Task : releaseStop.Task;
        var setting = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            var reset = () => device.ResetAsync(token);
            await reset.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stop*");
            device.IsArmed.Should().BeTrue();
            stop.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseStop.TrySetResult();
            releaseSetting.TrySetResult();
            await stop;
            await setting;
        }

        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task FailedStopStillCancelsSettingsAndRequiresExplicitReset()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var failure = new IOException("Stop failed.");
        device.Write = (_, _) => device.Writes.Count == 1 ? Task.FromException(failure) : Task.CompletedTask;

        var stop = () => device.EmergencyStopAsync(token);
        (await stop.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task FailedStopDuringDisconnectStillReleasesResourcesForReconnect()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var failure = new IOException("Stop failed.");
        device.Write = (_, _) => device.Writes.Count == 1 ? Task.FromException(failure) : Task.CompletedTask;

        var disconnect = () => device.DisconnectAsync(token);
        (await disconnect.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        device.Disconnects.Should().Be(1);
        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));

        await device.ConnectAsync(token);
        device.Connects.Should().Be(2);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task FailedStopAndCleanupDuringDisconnectAreBothReportedAndCleanupCanBeRetried()
    {
        var stopFailure = new IOException("Stop failed.");
        var cleanupFailure = new IOException("Cleanup failed.");
        await using var device = new TestDevice();
        device.Write = (_, _) => device.Writes.Count == 1 ? Task.FromException(stopFailure) : Task.CompletedTask;
        device.Disconnect = () => device.Disconnects == 1 ? Task.FromException(cleanupFailure) : Task.CompletedTask;
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);

        var disconnect = () => device.DisconnectAsync(token);
        (await disconnect.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().Equal(stopFailure, cleanupFailure);
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
        device.Disconnects.Should().Be(1);
        device.IsArmed.Should().BeFalse();

        await device.ConnectAsync(token);
        device.Connects.Should().Be(2);
        device.Disconnects.Should().Be(2);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeWaitsForIssuedIoAndDisconnectsExactlyOnce()
    {
        var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => release.Task;
        var active = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var pending = device.SetAsync(new(StrokePattern.RoboStroke, 10, 10, 10, 10), token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            device.IsArmed.Should().BeFalse();
            dispose.IsCompleted.Should().BeFalse();
            device.Disconnects.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            await dispose;
            await device.DisposeAsync();
        }

        (await pending).Should().Be(new SetResult(SetStatus.Cancelled, 0));
        (await active).Should().Be(new SetResult(SetStatus.Superseded, 1));
        device.Disconnects.Should().Be(1);
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
        var reset = () => device.ResetAsync(token);
        await reset.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task FailedConnectionReleasesPartialProviderResourcesAndCanBeRetried()
    {
        var failure = new IOException("Connection failed.");
        var device = new TestDevice { Connect = _ => Task.FromException(failure) };
        var token = TestContext.Current.CancellationToken;
        var connect = () => device.ConnectAsync(token);

        try
        {
            (await connect.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
            device.IsArmed.Should().BeFalse();
            device.Writes.Should().BeEmpty();
            device.Disconnects.Should().Be(1);
            (await connect.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
            device.Connects.Should().Be(2);
        }
        finally
        {
            await device.DisposeAsync();
        }

        device.Disconnects.Should().Be(2);
    }

    [Fact]
    public async Task FailedCleanupAfterFailedSetupReportsBothErrors()
    {
        var failure = new IOException("Connection failed.");
        var cleanupFailure = new IOException("Cleanup failed.");
        await using var device = new TestDevice { Connect = _ => Task.FromException(failure), Disconnect = () => Task.FromException(cleanupFailure) };

        var connect = () => device.ConnectAsync(TestContext.Current.CancellationToken);

        (await connect.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().Equal(failure, cleanupFailure);
        device.IsArmed.Should().BeFalse();
        device.Disconnect = null;
    }

    [Fact]
    public async Task DisconnectFailureIsPreservedForRepeatedDisposal()
    {
        var failure = new IOException("Disconnect failed.");
        var device = new TestDevice { Disconnect = () => Task.FromException(failure) };
        await device.ConnectAsync(TestContext.Current.CancellationToken);
        var firstDisposal = device.DisposeAsync().AsTask();
        var dispose = () => firstDisposal;

        (await dispose.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        var secondDisposal = device.DisposeAsync().AsTask();
        dispose = () => secondDisposal;
        (await dispose.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        device.Disconnects.Should().Be(1);
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task StateEventsReflectChangesAndSubscriberErrorsDoNotInterruptExecution()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        var notifications = 0;
        device.StateChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        device.StateChanged += (_, _) => notifications++;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        await device.ResetAsync(token);
        device.Observe(new(0, 0, 0, 0, 0));
        notifications.Should().Be(2);

        await device.SetAsync(new(0, 0, 20, 0, 0), token);
        device.Observe(new(0, 0, 20, 0, 0));
        notifications.Should().Be(3);
        device.IsArmed.Should().BeTrue();

        device.Observe(new(0, 0, 25, 0, 0));
        notifications.Should().Be(4);
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 25, 0, 0)));
        device.Observe(new(0, 0, 25, 0, 0));
        notifications.Should().Be(4);

        await device.EmergencyStopAsync(token);

        notifications.Should().Be(4);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task DelayedAndRevertedObservationsOnlyUpdateLastKnownState()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        await device.SetAsync(new(0, 0, 20, 0, 0), token);
        await device.SetAsync(new(0, 0, 30, 0, 0), token);

        device.Observe(new(0, 0, 20, 0, 0));
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 20, 0, 0)));
        device.Observe(new(0, 0, 30, 0, 0));
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 30, 0, 0)));
        device.Observe(new(0, 0, 20, 0, 0));
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 20, 0, 0)));
        device.Writes.Should().HaveCount(10);
    }

    [Fact]
    public async Task ObservationsDuringModeSelectionDoNotChangeArming()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.SetAsync(new(0, 0, 20, 0, 0), token);
        await device.SetAsync(new(0, 0, 30, 0, 0), token);
        device.Prepare = _ =>
        {
            device.Observe(new(0, 0, 20, 0, 0));
            return Task.FromResult(true);
        };

        (await device.SetAsync(new(0, 0, 40, 0, 0), token)).Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Observe(new(0, 0, 30, 0, 0));
        device.IsArmed.Should().BeTrue();
        device.Observe(new(0, 0, 40, 0, 0));
        device.IsArmed.Should().BeTrue();
        device.Observe(new(0, 0, 30, 0, 0));
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 0, 30, 0, 0)));
        device.Writes.Should().HaveCount(15);
    }

    [Fact]
    public async Task ReportedSpeedChangesDoNotDisarm()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.SetAsync(new(0, 20, 100, 0, 0), token);

        // The firmware reports speed scaled by its speed knob.
        device.Observe(new(0, 7, 100, 0, 0));
        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token))!.Speed.Should().Be(7);
        device.Observe(new(0, 13, 100, 0, 0));

        device.IsArmed.Should().BeTrue();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(new(0, 13, 100, 0, 0)));
    }

    [Fact]
    public async Task ModeSelectionPrecedesSettingsAndItsObservationsDoNotDisarm()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.SetAsync(new(0, 0, 20, 0, 0), token);
        device.Observe(new(0, 0, 20, 0, 0));
        device.Prepare = _ =>
        {
            device.Writes.Should().HaveCount(5);

            device.Observe(new(0, 0, 50, 10, 50));
            return Task.FromResult(true);
        };

        (await device.SetAsync(new(StrokePattern.Deeper, 5, 5, 5, 5), token)).Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Observe(new(StrokePattern.Deeper, 1, 5, 5, 5));

        device.Prepares.Should().Be(2);
        device.Writes.Skip(5).Should().Equal(
            new FirmwareCommand(FirmwareSetting.Pattern, 4),
            new FirmwareCommand(FirmwareSetting.Stroke, 5),
            new FirmwareCommand(FirmwareSetting.Depth, 5),
            new FirmwareCommand(FirmwareSetting.Sensation, 5),
            new FirmwareCommand(FirmwareSetting.Speed, 0));
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task SetWhenPatternsCannotRunSendsNoSettingsAndStaysArmed()
    {
        await using var device = new TestDevice { Prepare = _ => Task.FromResult(false) };
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);

        await device.Invoking(value => value.SetAsync(new(0, 5, 5, 5, 5), token))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*not ready to run patterns*");

        device.Writes.Should().BeEmpty();
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task FailedPatternPreparationDisarmsWithoutWriting()
    {
        await using var device = new TestDevice { Prepare = _ => Task.FromException<bool>(new IOException("rejected")) };
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);

        var failure = await device.Invoking(value => value.SetAsync(new(0, 5, 5, 5, 5), token)).Should().ThrowAsync<IOException>();

        failure.Which.Should().NotBeOfType<DeviceWriteException>();
        failure.Which.Message.Should().Contain("no settings were sent").And.Contain("rejected");
        device.Writes.Should().BeEmpty();
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task ResetWhileArmedDoesNotReplaySettings()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        await device.SetAsync(new(0, 20, 0, 0, 0), token);

        await device.ResetAsync(token);
        device.Observe(new(0, 20, 0, 0, 0));

        device.IsArmed.Should().BeTrue();
        device.Writes.Should().HaveCount(5);
    }

    [Fact]
    public async Task CallerCancellationWaitsForIssuedProviderWriteAndPreservesConfirmedCount()
    {
        await using var device = new TestDevice();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await device.ConnectAsync(cancellation.Token);
        await device.ResetAsync(cancellation.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issuedToken = CancellationToken.None;
        device.Write = (_, token) =>
        {
            issuedToken = token;
            return release.Task;
        };
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), cancellation.Token);

        try
        {
            await cancellation.CancelAsync();
            issuedToken.IsCancellationRequested.Should().BeTrue();
            set.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await set;
        }

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
        device.Writes.Should().HaveCount(1);
        device.IsArmed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, SetStatus.Superseded)]
    [InlineData(true, SetStatus.Cancelled)]
    public async Task FinalWritePreservesInterruptionOutcome(bool stopRequested, SetStatus expectedOutcome)
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 5 ? release.Task : Task.CompletedTask;
        var first = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        Task next = stopRequested
            ? device.EmergencyStopAsync(token)
            : device.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);

        try
        {
            first.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await next;
        }

        (await first).Should().Be(new SetResult(expectedOutcome, 5));
    }

    [Fact]
    public async Task ConnectArmsWithoutSendingMotionAndResetSendsNothing()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        (await device.GetStateAsync(cancellationToken: token)).Should().BeNull();
        var set = () => device.SetAsync(new(0, 20, 30, 40, 50), token);
        await set.Should().ThrowAsync<InvalidOperationException>();

        await device.ConnectAsync(token);
        device.IsArmed.Should().BeTrue();
        await device.EmergencyStopAsync(token);
        device.IsArmed.Should().BeTrue();
        await device.ResetAsync(token);

        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
        device.Reads.Should().Be(1);
    }

    [Fact]
    public async Task SuccessfulStopPreservesArmingAndAllowsNextSetWithoutReset()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var settings = new MotionSettings(StrokePattern.StopNGo, 20, 30, 40, 50);

        await device.EmergencyStopAsync(token);
        var result = await device.SetAsync(settings, token);

        device.IsArmed.Should().BeTrue();
        result.Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Writes.Should().Equal(
            new[] { new FirmwareCommand(FirmwareSetting.Speed, 0) }.Concat(settings.ToCommands()));
    }

    [Fact]
    public async Task SetDuringStopIsRejectedWithoutDisarming()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => release.Task;
        var stop = device.EmergencyStopAsync(token);

        try
        {
            var set = () => device.SetAsync(new(StrokePattern.StopNGo, 20, 30, 40, 50), token);
            await set.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stop*");
            device.IsArmed.Should().BeTrue();
        }
        finally
        {
            release.TrySetResult();
            await stop;
        }
    }

    [Fact]
    public async Task SuccessfulStopDoesNotRearmDisarmedDevice()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        device.Read = _ => Task.FromException<DeviceState>(new IOException("Read failed."));
        var read = () => device.GetStateAsync(true, token);
        await read.Should().ThrowAsync<IOException>();

        await device.EmergencyStopAsync(token);

        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task WritesOrderedPrimitivesWithoutInventingObservedState()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var settings = new MotionSettings(StrokePattern.StopNGo, 20, 30, 40, 50);

        var result = await device.SetAsync(settings, token);

        result.Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Writes.Should().Equal(settings.ToCommands());
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(device.LiveState);
        device.IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task SupersessionWaitsForIssuedIoAndSkipsOlderPendingSettings()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 1 ? release.Task : Task.CompletedTask;
        var first = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        var second = device.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        var latestSettings = new MotionSettings(StrokePattern.HalfNHalf, 30, 30, 30, 30);
        var latest = device.SetAsync(latestSettings, token);

        try
        {
            first.IsCompleted.Should().BeFalse();
            latest.IsCompleted.Should().BeFalse();
            (await second).Should().Be(new SetResult(SetStatus.Superseded, 0));
            device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1));
        }
        finally
        {
            release.TrySetResult();
        }

        (await first).Should().Be(new SetResult(SetStatus.Superseded, 1));
        (await latest).Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Writes.Should().Equal(new[] { new FirmwareCommand(FirmwareSetting.Pattern, 1) }.Concat(latestSettings.ToCommands()));
    }

    [Fact]
    public async Task StopIsSentWithoutWaitingForCommandInProgressAndDoesNotReplayOnReset()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 1 ? release.Task : Task.CompletedTask;
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);

        try
        {
            await device.EmergencyStopAsync(token);

            device.IsArmed.Should().BeTrue();
            set.IsCompleted.Should().BeFalse();
            device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
        }
        finally
        {
            release.TrySetResult();
        }

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task StopDropsQueuedSettingsWhileAnIssuedWriteSettles()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (command, _) => command.Setting == FirmwareSetting.Pattern ? release.Task : Task.CompletedTask;
        var active = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), token);
        var queued = device.SetAsync(new(StrokePattern.RoboStroke, 20, 20, 20, 20), token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            await stop;
            active.IsCompleted.Should().BeFalse();
            (await queued).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
        }
        finally
        {
            release.TrySetResult();
        }

        (await active).Should().Be(new SetResult(SetStatus.Superseded, 1));
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task ConcurrentStopsShareFailureAndRejectResetUntilSettled()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var failure = new IOException("Stop failed.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 1 ? release.Task : Task.CompletedTask;
        var first = device.EmergencyStopAsync(token);
        var second = device.EmergencyStopAsync(token);

        try
        {
            first.Should().BeSameAs(second);
            first.IsCompleted.Should().BeFalse();
            var reset = () => device.ResetAsync(token);
            await reset.Should().ThrowAsync<InvalidOperationException>();
            device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
        }
        finally
        {
            release.TrySetException(failure);
        }

        var firstStop = () => first;
        (await firstStop.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        var secondStop = () => second;
        (await secondStop.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        device.Writes.Should().HaveCount(1);
    }

    [Fact]
    public async Task StopIsSentWithoutWaitingForLiveRead()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var observation = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Read = _ => observation.Task;
        var live = device.GetStateAsync(true, token);

        try
        {
            await device.EmergencyStopAsync(token);

            device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
            live.IsCompleted.Should().BeFalse();
        }
        finally
        {
            observation.TrySetResult(device.LiveState);
            await live;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrDisposalAbandonsLiveRead(bool dispose)
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var observation = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Read = _ => observation.Task;
        var live = device.GetStateAsync(true, token);
        var cleanup = dispose ? device.DisposeAsync().AsTask() : device.DisconnectAsync(token);

        try
        {
            var read = () => live;
            await read.Should().ThrowAsync<OperationCanceledException>();
            await cleanup;
        }
        finally
        {
            observation.TrySetResult(device.LiveState);
            await cleanup;
        }

        device.Disconnects.Should().Be(1);
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task DisconnectAndDisposalWaitForStopInProgress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TestDevice { Write = (_, _) => release.Task };
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var stop = device.EmergencyStopAsync(token);
        var disconnect = device.DisconnectAsync(token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            disconnect.IsCompleted.Should().BeFalse();
            dispose.IsCompleted.Should().BeFalse();
            device.Disconnects.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            await stop;
            await disconnect;
            await dispose;
        }

        device.Disconnects.Should().Be(1);
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task DisposalStopsAndWaitsForIssuedIoBeforeCleanup()
    {
        var order = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TestDevice
        {
            Write = async (command, _) =>
            {
                if (command == new FirmwareCommand(FirmwareSetting.Speed, 0))
                {
                    order.Add("stop");
                    return;
                }

                await release.Task;
                order.Add("issued");
            },
            Disconnect = () =>
            {
                order.Add("disconnect");
                return Task.CompletedTask;
            },
        };
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            dispose.IsCompleted.Should().BeFalse();
            order.Should().Equal("stop");
            device.Disconnects.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            await dispose;
        }

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
        order.Should().Equal("stop", "issued", "disconnect");
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Pattern, 1), new FirmwareCommand(FirmwareSetting.Speed, 0));
    }

    [Fact]
    public async Task FailedStopDuringDisposalPropagatesAfterCleanup()
    {
        var failure = new IOException("Stop failed.");
        var device = new TestDevice { Write = (_, _) => Task.FromException(failure) };
        await device.ConnectAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        var dispose = () => disposal;

        (await dispose.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        var repeated = () => device.DisposeAsync().AsTask();
        (await repeated.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
        device.Disconnects.Should().Be(1);
    }

    [Fact]
    public async Task FailedStopAndCleanupDuringDisposalAreBothReported()
    {
        var failure = new IOException("Stop failed.");
        var cleanupFailure = new IOException("Cleanup failed.");
        var device = new TestDevice
        {
            Write = (_, _) => Task.FromException(failure),
            Disconnect = () => Task.FromException(cleanupFailure),
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        var dispose = () => disposal;

        (await dispose.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().Equal(failure, cleanupFailure);
        device.Writes.Should().Equal(new FirmwareCommand(FirmwareSetting.Speed, 0));
        device.Disconnects.Should().Be(1);
    }
}
