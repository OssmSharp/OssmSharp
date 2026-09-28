using AwesomeAssertions;
using OssmSharp.Device.Primitives;
using Xunit;

namespace OssmSharp.Device.Runtime.Tests;

public sealed class DeviceBaseTests
{
    [Fact]
    public async Task ReadIsPublishedBeforeObservationsResume()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var settings = new MotionSettings(1, 20, 30, 40, 50);
        await device.SetAsync(settings, token);
        device.ReadCompleted = () =>
        {
            device.Observe(settings);
            return Task.CompletedTask;
        };

        await device.GetStateAsync(true, token);

        (await device.GetStateAsync(cancellationToken: token)).Should().Be(new DeviceState(settings));
        device.IsArmed.Should().BeTrue();
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
        var set = device.SetAsync(new(1, 20, 30, 40, 50), token);
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
            device.Observe(new(4, 50, 50, 50, 50));
            (await device.GetStateAsync(cancellationToken: token)).Should().Be(device.LiveState);
        }
        finally
        {
            release.TrySetResult();
        }

        (await set).Should().Be(new SettingsResult(SettingsOutcome.Cancelled, 1));
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
        var settings = new MotionSettings(1, 20, 30, 40, 50);
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

        var set = () => device.SetAsync(new(1, 20, 30, 40, 50), token);
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
        var active = device.SetAsync(new(1, 20, 30, 40, 50), token);
        var pending = device.SetAsync(new(2, 10, 10, 10, 10), token);
        var observed = new MotionSettings(1, 99, 0, 0, 0);

        try
        {
            device.Observe(observed);
            device.IsArmed.Should().BeFalse();
            (await pending).Should().Be(new SettingsResult(SettingsOutcome.Cancelled, 0));
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
        var set = device.SetAsync(new(1, 20, 30, 40, 50), token);
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
        var active = device.SetAsync(new(1, 20, 30, 40, 50), token);
        var pending = device.SetAsync(new(2, 10, 10, 10, 10), token);
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

        (await pending).Should().Be(new SettingsResult(SettingsOutcome.Cancelled, 0));
        (await active).Should().Be(new SettingsResult(SettingsOutcome.Superseded, 1));
        device.Disconnects.Should().Be(1);
        device.Writes.Should().HaveCount(1);
        var reset = () => device.ResetAsync(token);
        await reset.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task FailedConnectionStillReleasesPartialProviderResources()
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
            await connect.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await device.DisposeAsync();
        }

        device.Disconnects.Should().Be(1);
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
        var set = device.SetAsync(new(1, 10, 10, 10, 10), cancellation.Token);

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

        (await set).Should().Be(new SettingsResult(SettingsOutcome.Cancelled, 1));
        device.Writes.Should().HaveCount(1);
        device.IsArmed.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, SettingsOutcome.Superseded)]
    [InlineData(true, SettingsOutcome.Cancelled)]
    public async Task FinalWritePreservesInterruptionOutcome(bool stopRequested, SettingsOutcome expectedOutcome)
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 5 ? release.Task : Task.CompletedTask;
        var first = device.SetAsync(new(1, 10, 10, 10, 10), token);
        Task next = stopRequested
            ? device.EmergencyStopAsync(token)
            : device.SetAsync(new(2, 20, 20, 20, 20), token);

        try
        {
            first.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await next;
        }

        (await first).Should().Be(new SettingsResult(expectedOutcome, 5));
    }

    [Fact]
    public async Task ConnectAndResetDoNotSendMotion()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        (await device.GetStateAsync(cancellationToken: token)).Should().BeNull();

        await device.ConnectAsync(token);
        device.IsArmed.Should().BeFalse();
        var set = () => device.SetAsync(new(0, 20, 30, 40, 50), token);
        await set.Should().ThrowAsync<InvalidOperationException>();
        await device.ResetAsync(token);

        device.IsArmed.Should().BeTrue();
        device.Writes.Should().BeEmpty();
        device.Reads.Should().Be(1);
    }

    [Fact]
    public async Task WritesOrderedPrimitivesWithoutInventingObservedState()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var settings = new MotionSettings(50, 20, 30, 40, 50);

        var result = await device.SetAsync(settings, token);

        result.Should().Be(new SettingsResult(SettingsOutcome.Completed, 5));
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
        var first = device.SetAsync(new(1, 10, 10, 10, 10), token);
        var second = device.SetAsync(new(2, 20, 20, 20, 20), token);
        var latestSettings = new MotionSettings(3, 30, 30, 30, 30);
        var latest = device.SetAsync(latestSettings, token);

        try
        {
            first.IsCompleted.Should().BeFalse();
            latest.IsCompleted.Should().BeFalse();
            (await second).Should().Be(new SettingsResult(SettingsOutcome.Superseded, 0));
            device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1));
        }
        finally
        {
            release.TrySetResult();
        }

        (await first).Should().Be(new SettingsResult(SettingsOutcome.Superseded, 1));
        (await latest).Should().Be(new SettingsResult(SettingsOutcome.Completed, 5));
        device.Writes.Should().Equal(new[] { new DeviceCommand(DeviceControl.Pattern, 1) }.Concat(latestSettings.ToCommands()));
    }

    [Fact]
    public async Task StopWaitsForIssuedIoDisarmsAndDoesNotReplayOnReset()
    {
        await using var device = new TestDevice();
        var token = TestContext.Current.CancellationToken;
        await device.ConnectAsync(token);
        await device.ResetAsync(token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Write = (_, _) => device.Writes.Count == 1 ? release.Task : Task.CompletedTask;
        var set = device.SetAsync(new(1, 10, 10, 10, 10), token);
        var stop = device.EmergencyStopAsync(token);

        try
        {
            device.IsArmed.Should().BeFalse();
            stop.IsCompleted.Should().BeFalse();
            device.Writes.Should().HaveCount(1);
        }
        finally
        {
            release.TrySetResult();
        }

        (await set).Should().Be(new SettingsResult(SettingsOutcome.Cancelled, 1));
        await stop;
        await device.ResetAsync(token);
        device.IsArmed.Should().BeTrue();
        device.Writes.Should().Equal(new DeviceCommand(DeviceControl.Pattern, 1), new DeviceCommand(DeviceControl.Speed, 0));
    }
}
