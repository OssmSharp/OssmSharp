using AwesomeAssertions;
using OssmSharp.Runtime.Oem;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class DeviceBaseTests
{
    [Fact]
    public async Task DisconnectPreservesClientAndReconnectArmsFreshStateWithoutReplay()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        var notifications = 0;
        device.StateChanged += (_, _) => notifications++;
        await device.ConnectAsync(token);
        await device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);

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
        device.Writes.Should().HaveCount(5);
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
    public async Task StopDuringSetupPreventsArmingAndIsSentAfterConnection()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var device = new TestDevice { Connect = _ => release.Task };
        var token = TestContext.Current.CancellationToken;
        var connect = device.ConnectAsync(token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            stop.IsCompleted.Should().BeFalse();
            device.Writes.Should().BeEmpty();
        }
        finally
        {
            release.TrySetResult();
            await connect;
            await stop;
        }

        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
    }

    [Fact]
    public async Task UnexplainedChangeDuringSetupLeavesConnectionDisarmed()
    {
        await using var device = new TestDevice();
        device.Connect = _ =>
        {
            device.Observe(new(StrokePattern.TeasingPounding, 10, 10, 10, 10));
            return Task.CompletedTask;
        };
        var token = TestContext.Current.CancellationToken;

        await device.ConnectAsync(token);

        device.IsArmed.Should().BeFalse();
        (await device.GetStateAsync(cancellationToken: token)).Should().Be(device.LiveState);
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
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
        error.Command.Should().Be(new DeviceCommand(DeviceControl.Depth, 40));
        error.InnerException.Should().BeSameAs(failure);
        device.Writes.Should().HaveCount(3);
        device.IsArmed.Should().BeFalse();
        await set.Should().ThrowAsync<InvalidOperationException>();
        device.Writes.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExternalControlChangeCancelsPendingWorkWithoutIssuingStop()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => release.Task;
        var active = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var pending = device.SetAsync(new(StrokePattern.RoboStroke, 10, 10, 10, 10), token);
        var observed = new MotionSettings(StrokePattern.TeasingPounding, 99, 0, 0, 0);

        try
        {
            device.Observe(observed);
            device.IsArmed.Should().BeFalse();
            (await pending).Should().Be(new SetResult(SetStatus.Cancelled, 0));
            active.IsCompleted.Should().BeFalse();
            (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(observed));
        }
        finally
        {
            release.TrySetResult();
            await active;
        }

        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1));
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().HaveCount(1);
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

        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1), new DeviceCommand(DeviceControl.Speed, 0));
    }

    [Fact]
    public async Task FailedStopStillCancelsSettingsAndRequiresExplicitReset()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var failure = new IOException("Stop failed.");
        device.Write = (_, _) => Task.FromException(failure);

        var stop = () => device.EmergencyStopAsync(token);
        (await stop.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        device.IsArmed.Should().BeFalse();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().HaveCount(1);
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
        device.Writes.Should().HaveCount(1);
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

        await device.SetAsync(new(0, 20, 0, 0, 0), token);
        device.Observe(new(0, 20, 0, 0, 0));
        notifications.Should().Be(3);
        device.IsArmed.Should().BeTrue();

        device.Observe(new(0, 25, 0, 0, 0));
        notifications.Should().Be(4);
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task DelayedOwnObservationsRemainExplained()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        await device.SetAsync(new(0, 20, 0, 0, 0), token);
        await device.SetAsync(new(0, 30, 0, 0, 0), token);

        device.Observe(new(0, 20, 0, 0, 0));
        device.IsArmed.Should().BeTrue();
        device.Observe(new(0, 30, 0, 0, 0));
        device.IsArmed.Should().BeTrue();
        device.Observe(new(0, 20, 0, 0, 0));
        device.IsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task ResetWhileArmedPreservesUnobservedAssignments()
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
    public async Task CallerCancellationDoesNotCancelIssuedProviderWrite()
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
            return release.Task.WaitAsync(token);
        };
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 10, 10, 10, 10), cancellation.Token);

        try
        {
            await cancellation.CancelAsync();
            issuedToken.IsCancellationRequested.Should().BeFalse();
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
        device.IsArmed.Should().BeFalse();
        await device.ResetAsync(token);

        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
        device.Reads.Should().Be(1);
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
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1));
        }
        finally
        {
            release.TrySetResult();
        }

        (await first).Should().Be(new SetResult(SetStatus.Superseded, 1));
        (await latest).Should().Be(new SetResult(SetStatus.Completed, 5));
        device.Writes.Should().Equal(new[] { new DeviceCommand(DeviceControl.Pattern, 1) }.Concat(latestSettings.ToCommands()));
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

            device.IsArmed.Should().BeFalse();
            set.IsCompleted.Should().BeFalse();
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1), new DeviceCommand(DeviceControl.Speed, 0));
        }
        finally
        {
            release.TrySetResult();
        }

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1), new DeviceCommand(DeviceControl.Speed, 0));
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

            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
            live.IsCompleted.Should().BeFalse();
        }
        finally
        {
            observation.TrySetResult(device.LiveState);
            await live;
        }
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
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Speed, 0));
    }

    [Fact]
    public async Task DisposalRunsProviderHookAfterIssuedIoAndBeforeCleanup()
    {
        var order = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new TestDevice
        {
            Write = async (_, _) =>
            {
                await release.Task;
                order.Add("write");
            },
            Disconnect = () =>
            {
                order.Add("disconnect");
                return Task.CompletedTask;
            },
        };
        device.Disposing = () =>
        {
            order.Add($"dispose armed={device.IsArmed}");
            return ValueTask.CompletedTask;
        };
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        var set = device.SetAsync(new(StrokePattern.TeasingPounding, 20, 30, 40, 50), token);
        var dispose = device.DisposeAsync().AsTask();

        try
        {
            dispose.IsCompleted.Should().BeFalse();
            order.Should().BeEmpty();
        }
        finally
        {
            release.TrySetResult();
            await dispose;
        }

        (await set).Should().Be(new SetResult(SetStatus.Cancelled, 1));
        order.Should().Equal("write", "dispose armed=False", "disconnect");
    }

    [Fact]
    public async Task ProviderDisposalFailurePropagatesAfterCleanup()
    {
        var failure = new IOException("Stop failed.");
        var device = new TestDevice { Disposing = () => ValueTask.FromException(failure) };
        await device.ConnectAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        var dispose = () => disposal;

        (await dispose.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        device.Disconnects.Should().Be(1);
    }

    [Fact]
    public async Task ProviderDisposalAndCleanupFailuresAreBothReported()
    {
        var failure = new IOException("Stop failed.");
        var cleanupFailure = new IOException("Cleanup failed.");
        var device = new TestDevice
        {
            Disposing = () => ValueTask.FromException(failure),
            Disconnect = () => Task.FromException(cleanupFailure),
        };
        await device.ConnectAsync(TestContext.Current.CancellationToken);

        var disposal = device.DisposeAsync().AsTask();
        var dispose = () => disposal;

        (await dispose.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().Equal(failure, cleanupFailure);
    }
}
