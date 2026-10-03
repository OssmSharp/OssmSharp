using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using Microsoft.Extensions.Time.Testing;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ControlSessionTests : MockerTestBase<ControlSession>
{
    private FakeOssmDevice? _device;
    private FakeTimeProvider? _timeProvider;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _device = new();
        _timeProvider = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        mocks.AddType<Func<string, IOssmDevice>>(_ => _device);
        mocks.AddType<TimeProvider>(_timeProvider);
        mocks.SetupOptions(new ControlServiceOptions { DeviceIdentifier = "device-1" });
    };

    private FakeOssmDevice Device => _device ?? throw new InvalidOperationException("Test device is not initialized.");

    private FakeTimeProvider TimeProvider => _timeProvider ?? throw new InvalidOperationException("Test clock is not initialized.");

    [Fact]
    public async Task AllowControlRearmsOnlyWhenRequiredWithoutSendingSettings()
    {
        await JoinAndAllowAsync("guest-1");
        Assert.Equal(0, Device.Resets);
        Assert.True((await Component.StopAsync("stop-1")).Accepted);
        Device.IsArmed = false;

        var response = await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken);

        Assert.True(response.Accepted);
        Assert.Equal(1, Device.Resets);
        Assert.Equal(0, Device.Sets);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
    }

    [Fact]
    public async Task FailedRearmDoesNotGrantPermission()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopAsync("stop-1");
        Device.IsArmed = false;
        Device.ResetException = new IOException("Re-arm failed.");

        var response = await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken);

        Assert.Equal(new ControlResponse("allow-2", "device-io-failed", false), response);
        Assert.False((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
        Assert.Equal(0, Device.Sets);
    }

    [Fact]
    public async Task StopInvalidatesPendingAllowEvenAfterStopCompletes()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopAsync("stop-1");
        Device.IsArmed = false;
        Device.PendingReset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken);
        try
        {
            Assert.False(allow.IsCompleted);
            Assert.True((await Component.StopAsync("stop-2")).Accepted);
        }
        finally
        {
            Device.PendingReset.TrySetResult();
        }

        Assert.False((await allow).Accepted);
        Assert.False((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
        Assert.Equal(0, Device.Sets);
    }

    [Fact]
    public async Task GuestPermissionDoesNotExpire()
    {
        await JoinAndAllowAsync("guest-1");
        TimeProvider.Advance(TimeSpan.FromHours(1));

        var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);

        Assert.True(state.ControlAllowed);
        Assert.Null(state.ControlExpiresAt);
        Assert.Equal(0, Device.Stops);
    }

    [Fact]
    public async Task FailedGuestStopRetryKeepsPermissionAndBlocksSettingsUntilSuccess()
    {
        await JoinAndAllowAsync("guest-1");
        Device.StopException = new IOException("Stop failed.");
        Assert.False((await Component.StopGuestAsync("guest-1", "stop-1")).Accepted);
        var request = await Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        Assert.Equal(SettingsResponseStatus.Rejected, request.Status);
        Assert.Equal("device-not-ready", request.ErrorCode);
        Assert.Equal(0, Device.Sets);
        await Assert.ThrowsAsync<IOException>(Component.ExpireGuestAsync);
        var failedRetry = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.True(failedRetry.ControlAllowed);
        Assert.False(failedRetry.CanResumeAfterStop);
        Device.StopException = null;

        await Component.ExpireGuestAsync();

        var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.True(state.ControlAllowed);
        Assert.True(state.CanResumeAfterStop);
        Assert.Equal(4, Device.Stops);
    }

    [Fact]
    public async Task UserStateProjectsConnectionMetadataAndRequestedSettingsWithoutGuestDisclosure()
    {
        Device.IsConnected = false;
        Device.Name = "Test device";
        var initial = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.False(initial.Device!.IsConnected);
        Assert.Null(initial.Device.ConnectedAt);
        Assert.Null(initial.Device.TransmittedSettings);
        Device.PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        var connecting = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.True(connecting.Device!.IsConnecting);
        Assert.Null(connecting.Device.ConnectedAt);
        Assert.Null(connecting.Device.TransmittedSettings);
        Device.IsConnected = true;
        Device.PendingConnect.SetResult();
        Assert.True((await connect).Accepted);
        var connectedAt = TimeProvider.GetUtcNow();
        TimeProvider.Advance(TimeSpan.FromMinutes(1));
        Device.State = new(new(0, 80, 70, 60, 50));
        await JoinAndAllowAsync("guest-1");
        Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
        Assert.Equal(
            SettingsResponseStatus.Completed,
            (await Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 40, 50), TestContext.Current.CancellationToken)).Status);

        var user = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);

        Assert.True(user.Device!.IsConnected);
        Assert.False(user.Device.IsConnecting);
        Assert.Equal(connectedAt, user.Device.ConnectedAt);
        Assert.Equal("device-1", user.Device.Identifier);
        Assert.Equal("Test device", user.Device.Name);
        Assert.Equal(new DeviceSettingsSnapshot(0, 53, 10, 40, 50), user.Device.TransmittedSettings);
        Assert.Equal(Device.State.Speed, user.Device.Speed);
        var guest = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.Null(guest.Device!.Identifier);
        Assert.Null(guest.Device.Name);
        Assert.Null(guest.Device.TransmittedSettings);
        Device.IsConnected = false;
        var disconnected = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.Null(disconnected.Device!.ConnectedAt);
    }

    [Fact]
    public async Task GuestStopKeepsPermissionAndNextRequestRearms()
    {
        await JoinAndAllowAsync("guest-1");

        Assert.True((await Component.StopGuestAsync("guest-1", "stop-1")).Accepted);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
        Device.IsArmed = false;
        var request = await Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);

        Assert.Equal(SettingsResponseStatus.Completed, request.Status);
        Assert.Equal(1, Device.Resets);
        Assert.Equal(1, Device.Sets);
    }

    [Fact]
    public async Task LaterGuestStopInvalidatesPendingResumeWithoutSendingSettings()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopGuestAsync("guest-1", "stop-1");
        Device.IsArmed = false;
        Device.PendingReset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        try
        {
            Assert.False(submit.IsCompleted);
            Assert.True((await Component.StopGuestAsync("guest-1", "stop-2")).Accepted);
        }
        finally
        {
            Device.PendingReset.TrySetResult();
        }

        var result = await submit;
        Assert.Equal(SettingsResponseStatus.Failed, result.Status);
        Assert.Equal("device-not-ready", result.ErrorCode);
        Assert.Equal(0, Device.Sets);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
    }

    [Fact]
    public async Task CancelledAllowDoesNotGrantPermission()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopAsync("stop-1");
        Device.IsArmed = false;
        Device.PendingReset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var allow = Component.AllowGuestAsync("allow-2", cancellation.Token);
        try
        {
            Assert.False(allow.IsCompleted);
            await cancellation.CancelAsync();
            Assert.Equal(new ControlResponse("allow-2", "cancelled", false), await allow);
            Assert.False((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
            Assert.Equal(0, Device.Sets);
        }
        finally
        {
            Device.PendingReset.TrySetResult();
        }
    }

    [Fact]
    public async Task SubmitDelegatesCompleteSettingsAndMapsRuntimeResult()
    {
        var invitation = await JoinAndAllowAsync("guest-1");
        invitation.Should().NotBeNull();
        var request = new SettingsRequest("set-1", 2, 20, 30, 40, 50);

        var response = await Component.SubmitAsync(
            "guest-1",
            request,
            TestContext.Current.CancellationToken);

        response.Should().Be(new SettingsResponse("set-1", SettingsResponseStatus.Completed, 5));
        Device.Sets.Should().Be(1);
        Device.LastSettings.Should().Be(new MotionSettings(StrokePattern.RoboStroke, 20, 30, 40, 50));
    }

    [Theory]
    [InlineData(SetStatus.Cancelled)]
    [InlineData(SetStatus.Superseded)]
    public async Task InterruptedRequestDoesNotReplaceLastSuccessfulRequestedSelection(SetStatus status)
    {
        await JoinAndAllowAsync("guest-1");
        await Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        Device.SetResult = new(status, 2);

        var response = await Component.SubmitAsync("guest-1", new("set-2", 0, 80, 40, 50, 60), TestContext.Current.CancellationToken);

        Assert.Equal(status == SetStatus.Cancelled ? SettingsResponseStatus.Cancelled : SettingsResponseStatus.Superseded, response.Status);
        Assert.Equal(2, response.CommandsWritten);
        Assert.Equal(
            new DeviceSettingsSnapshot(0, 53, 10, 20, 50),
            (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
    }

    [Fact]
    public async Task PendingAndFailedRequestAndStopRetainLastSuccessfulRequestedSelection()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        Device.PendingSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Component.SubmitAsync("guest-1", new("set-2", 0, 80, 40, 50, 60), TestContext.Current.CancellationToken);
        try
        {
            Assert.False(request.IsCompleted);
            Assert.Equal(
                new DeviceSettingsSnapshot(0, 53, 10, 20, 50),
                (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
        }
        finally
        {
            Device.PendingSet.TrySetException(new DeviceWriteException(2, new IOException("Write failed.")));
        }

        var response = await request;
        Assert.Equal(SettingsResponseStatus.Failed, response.Status);
        Assert.Equal(2, response.CommandsWritten);
        Assert.Equal("device-write-failed", response.ErrorCode);
        Assert.True((await Component.StopGuestAsync("guest-1", "stop-1")).Accepted);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).CanResumeAfterStop);
        Assert.Equal(
            new DeviceSettingsSnapshot(0, 53, 10, 20, 50),
            (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
    }

    [Fact]
    public async Task OlderCompletionCannotOverwriteNewerSuccessfulRequestedSelection()
    {
        await JoinAndAllowAsync("guest-1");
        var olderCompletion = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerCompletion = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.PendingSet = olderCompletion;
        var older = Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        Device.PendingSet = newerCompletion;
        var newer = Component.SubmitAsync("guest-1", new("set-2", 0, 80, 40, 50, 60), TestContext.Current.CancellationToken);
        try
        {
            newerCompletion.SetResult(new(SetStatus.Completed, 5));
            Assert.Equal(SettingsResponseStatus.Completed, (await newer).Status);
            olderCompletion.SetResult(new(SetStatus.Completed, 5));
            Assert.Equal(SettingsResponseStatus.Completed, (await older).Status);
            Assert.Equal(
                new DeviceSettingsSnapshot(0, 80, 40, 50, 60),
                (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
        }
        finally
        {
            olderCompletion.TrySetResult(new(SetStatus.Cancelled, 0));
            newerCompletion.TrySetResult(new(SetStatus.Cancelled, 0));
            await Task.WhenAll(older, newer);
        }
    }

    [Theory]
    [InlineData(1.5, 2, 75.0)]
    [InlineData(2.5, 20, 12.5)]
    [InlineData(40, 10, 400.0)]
    [InlineData(0, 10, 0.0)]
    [InlineData(5, 0, null)]
    [InlineData(0, 0, null)]
    public async Task DeviceSnapshotPreservesRuntimeSpeed(double velocity, int stroke, double? expectedSpeed)
    {
        Device.State = new(StrokePattern.SimpleStroke, velocity, stroke, 20, 50);
        await JoinAndAllowAsync("guest-1");

        var guest = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        var user = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedSpeed, guest.Device!.Speed);
        Assert.Equal(expectedSpeed, user.Device!.Speed);
        Assert.Null(user.Device.TransmittedSettings);
    }

    [Fact]
    public async Task PermissionCheckDoesNotExpireOrStopRuntime()
    {
        await JoinAndAllowAsync("guest-1");
        TimeProvider.Advance(TimeSpan.FromSeconds(3));

        var response = await Component.RenewGuestAsync("guest-1", "renew-1");

        response.Should().Be(new ControlResponse("renew-1", "control-renewed", true));
        Device.Stops.Should().Be(0);
        var state = await Component.GetGuestStateAsync(
            "guest-1",
            TestContext.Current.CancellationToken);
        state.ControlAllowed.Should().BeTrue();
        state.ControlExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task InvalidConnectCorrelationDoesNotInvokeRuntime()
    {
        var response = await Component.ConnectAsync(
            string.Empty,
            TestContext.Current.CancellationToken);

        response.Should().Be(new ControlResponse(string.Empty, "invalid-correlation", false));
        Device.Connects.Should().Be(0);
    }

    [Fact]
    public async Task ConfiguredIdentifierConnectsWithoutDiscovery()
    {
        var response = await Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);

        Assert.True(response.Accepted);
        Assert.Equal(1, Device.Connects);
        Mocks.Verify<IDeviceDiscovery>(discovery => discovery.FindFirstAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task StaleDisconnectDoesNotStopReplacementGuest()
    {
        await JoinAndAllowAsync("guest-1");
        var ended = await Component.EndGuestAsync("end-1");
        ended.Accepted.Should().BeTrue();
        var replacementInvitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        JoinGuest("guest-2", new("join-2", replacementInvitation.Token)).Accepted.Should().BeTrue();
        var stopsAfterReplacement = Device.Stops;

        await Component.GuestDisconnectedAsync("guest-1");

        Device.Stops.Should().Be(stopsAfterReplacement);
        var replacement = await Component.GetGuestStateAsync(
            "guest-2",
            TestContext.Current.CancellationToken);
        replacement.Joined.Should().BeTrue();
    }

    [Fact]
    public async Task StopReachesRuntimeWhileSettingsRemainPending()
    {
        await JoinAndAllowAsync("guest-1");
        Device.PendingSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Component.SubmitAsync(
            "guest-1",
            new("set-1", 0, 10, 20, 30, 40),
            TestContext.Current.CancellationToken);

        var stop = await Component.StopGuestAsync("guest-1", "stop-1");

        stop.Accepted.Should().BeTrue();
        Device.Stops.Should().Be(1);
        request.IsCompleted.Should().BeFalse();
        Device.PendingSet.SetResult(new(SetStatus.Superseded, 0));
        (await request).Status.Should().Be(SettingsResponseStatus.Superseded);
    }

    [Fact]
    public async Task InvalidSettingsDoNotInvokeRuntime()
    {
        await JoinAndAllowAsync("guest-1");

        var response = await Component.SubmitAsync(
            "guest-1",
            new("set-1", 99, 10, 20, 30, 40),
            TestContext.Current.CancellationToken);

        response.Should().Be(new SettingsResponse(
            "set-1",
            SettingsResponseStatus.Rejected,
            0,
            "invalid-settings"));
        Device.Sets.Should().Be(0);
    }

    [Fact]
    public async Task DeviceWriteFailurePreservesKnownProgress()
    {
        await JoinAndAllowAsync("guest-1");
        Device.PendingSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.PendingSet.SetException(new DeviceWriteException(
            2,
            new IOException("write failed")));

        var response = await Component.SubmitAsync(
            "guest-1",
            new("set-1", 0, 10, 20, 30, 40),
            TestContext.Current.CancellationToken);

        response.Should().Be(new SettingsResponse(
            "set-1",
            SettingsResponseStatus.Failed,
            2,
            "device-write-failed"));
    }

    [Fact]
    public async Task UserStopRevokesGuestPermission()
    {
        await JoinAndAllowAsync("guest-1");

        var response = await Component.StopAsync("stop-1");

        response.Accepted.Should().BeTrue();
        Device.Stops.Should().Be(1);
        var state = await Component.GetGuestStateAsync(
            "guest-1",
            TestContext.Current.CancellationToken);
        state.Joined.Should().BeTrue();
        state.ControlAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task UserStopMapsSynchronousDisconnectedFailureAndRevokesPermission()
    {
        await JoinAndAllowAsync("guest-1");
        Device.StopException = new InvalidOperationException("not connected");

        var response = await Component.StopAsync("stop-1");

        response.Should().Be(new ControlResponse("stop-1", "device-not-connected", false));
        var state = await Component.GetGuestStateAsync(
            "guest-1",
            TestContext.Current.CancellationToken);
        state.ControlAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidInvitationDoesNotAdmitGuest()
    {
        var response = JoinGuest("guest-1", new("join-1", new string('x', 43)));

        response.Should().Be(new ControlResponse("join-1", "invalid-invitation", false));
        var state = await Component.GetGuestStateAsync(
            "guest-1",
            TestContext.Current.CancellationToken);
        state.Joined.Should().BeFalse();
    }

    [Fact]
    public async Task SecondGuestCannotReplaceCurrentGuest()
    {
        var invitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        JoinGuest("guest-1", new("join-1", invitation.Token)).Accepted.Should().BeTrue();

        var response = JoinGuest("guest-2", new("join-2", invitation.Token));

        response.Should().Be(new ControlResponse("join-2", "guest-already-joined", false));
        (await Component.GetGuestStateAsync(
            "guest-1",
            TestContext.Current.CancellationToken)).Joined.Should().BeTrue();
        (await Component.GetGuestStateAsync(
            "guest-2",
            TestContext.Current.CancellationToken)).Joined.Should().BeFalse();
    }

    [Fact]
    public async Task DeviceStateIsVisibleOnlyToTheCurrentGuest()
    {
        await JoinAndAllowAsync("guest-1");

        var current = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        var unjoined = await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken);
        await Component.EndGuestAsync("end-1");
        var ended = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);

        Assert.NotNull(current.Device);
        Assert.True(current.Device.IsArmed);
        Assert.Null(unjoined.Device);
        Assert.Null(ended.Device);
    }

    [Fact]
    public async Task LeavingGuestAllowsReplacementWithRotatedInvitation()
    {
        await JoinAndAllowAsync("guest-1");
        var replacementInvitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;

        var leave = await Component.LeaveGuestAsync("guest-1", "leave-1");
        var replacement = JoinGuest("guest-2", new("join-2", replacementInvitation.Token));

        leave.Accepted.Should().BeTrue();
        replacement.Should().Be(new ControlResponse("join-2", "joined", true));
        Device.Stops.Should().Be(1);
    }

    [Fact]
    public async Task GuestStopRejectsNonCurrentGuestWithoutStoppingRuntime()
    {
        await JoinAndAllowAsync("guest-1");

        var response = await Component.StopGuestAsync("guest-2", "stop-1");

        response.Should().Be(new ControlResponse("stop-1", "guest-not-joined", false));
        Device.Stops.Should().Be(0);
    }

    [Fact]
    public async Task RequestWithoutPermissionDoesNotInvokeRuntime()
    {
        var invitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        JoinGuest("guest-1", new("join-1", invitation.Token)).Accepted.Should().BeTrue();

        var response = await Component.SubmitAsync(
            "guest-1",
            new("set-1", 0, 10, 20, 30, 40),
            TestContext.Current.CancellationToken);

        response.Should().Be(new SettingsResponse(
            "set-1",
            SettingsResponseStatus.Rejected,
            0,
            "control-not-allowed"));
        Device.Sets.Should().Be(0);
    }

    [Fact]
    public async Task PermissionPollingDoesNotStopAllowedGuest()
    {
        await JoinAndAllowAsync("guest-1");
        TimeProvider.Advance(TimeSpan.FromSeconds(3));

        await Component.ExpireGuestAsync();
        await Component.ExpireGuestAsync();

        Device.Stops.Should().Be(0);
    }

    [Fact]
    public async Task FailedUserStopIsRetried()
    {
        await JoinAndAllowAsync("guest-1");
        TimeProvider.Advance(TimeSpan.FromSeconds(3));
        Device.StopException = new IOException("write failed");

        Assert.False((await Component.StopAsync("stop-1")).Accepted);
        Device.StopException = null;
        await Component.ExpireGuestAsync();

        Device.Stops.Should().Be(2);
    }

    [Fact]
    public async Task LeaveBlocksReplacementUntilStopSucceeds()
    {
        await JoinAndAllowAsync("guest-1");
        Device.PendingStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementInvitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;

        var leave = Component.LeaveGuestAsync("guest-1", "leave-1");
        var replacement = JoinGuest("guest-2", new("join-2", replacementInvitation.Token));

        leave.IsCompleted.Should().BeFalse();
        replacement.Should().Be(new ControlResponse("join-2", "guest-already-joined", false));
        Device.PendingStop.SetResult();
        (await leave).Accepted.Should().BeTrue();
        JoinGuest("guest-2", new("join-3", replacementInvitation.Token)).Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task FailedLeaveStopDoesNotCancelReconnectAndCompletesAfterReconnect()
    {
        await JoinAndAllowAsync("guest-1");
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        Device.StopException = new IOException("connection lost");
        var leave = await Component.LeaveGuestAsync("guest-1", "leave-1");
        Assert.Equal(new ControlResponse("leave-1", "device-io-failed", false), leave);
        Device.PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);

        try
        {
            await Component.ExpireGuestAsync();
            await Component.RenewGuestAsync("guest-1", "renew-1");
            var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
            Assert.False(Device.PendingConnect.Task.IsCanceled);
            Assert.True(state.Joined);
            Assert.False(state.ControlAllowed);
            Assert.False((await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken)).Accepted);
            Assert.False(JoinGuest("guest-2", new("join-2", invitation.Token)).Accepted);
            Assert.False(connect.IsCompleted);
            Assert.Equal(1, Device.Stops);
        }
        finally
        {
            Device.StopException = null;
            Device.PendingConnect.TrySetResult();
            await connect;
        }

        Assert.Equal(new ControlResponse("connect-1", "connected", true), await connect);
        Assert.Equal(2, Device.Stops);
        Assert.True(JoinGuest("guest-2", new("join-3", invitation.Token)).Accepted);
        await Component.ExpireGuestAsync();
        Assert.Equal(2, Device.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitUserStopStillInterruptsConnection(bool priorStopFailed)
    {
        await JoinAndAllowAsync("guest-1");
        if (priorStopFailed)
        {
            Device.StopException = new IOException("connection lost");
            Assert.False((await Component.StopAsync("stop-1")).Accepted);
        }

        Device.PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(new ControlResponse("stop-2", "stopped", true), await Component.StopAsync("stop-2"));
            Assert.True(Device.PendingConnect.Task.IsCanceled);
            Assert.Equal(new ControlResponse("connect-1", "cancelled", false), await connect);
            Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestControlAllowed);
        }
        finally
        {
            Device.PendingConnect.TrySetResult();
            await connect;
        }
    }

    [Fact]
    public async Task ElapsedTimeDoesNotInterruptConnection()
    {
        await JoinAndAllowAsync("guest-1");
        Device.PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        TimeProvider.Advance(TimeSpan.FromSeconds(3));
        try
        {
            await Component.ExpireGuestAsync();
            Assert.False(Device.PendingConnect.Task.IsCanceled);
            Assert.False(connect.IsCompleted);
            Assert.Equal(0, Device.Stops);
        }
        finally
        {
            Device.PendingConnect.TrySetResult();
            await connect;
        }
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("device-io-failed")]
    [InlineData("device-not-ready")]
    public async Task FailedReconnectPreservesStopAndPendingLeave(string failureCode)
    {
        await JoinAndAllowAsync("guest-1");
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        Device.StopException = new IOException("connection lost");
        Assert.False((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        Device.PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        Device.PendingConnect.SetException(ConnectionFailure(failureCode));

        Assert.Equal(new ControlResponse("connect-1", failureCode, false), await connect);
        Assert.Equal(1, Device.Stops);
        Assert.False((await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken)).Accepted);
        Assert.False(JoinGuest("guest-2", new("join-2", invitation.Token)).Accepted);
        await Assert.ThrowsAsync<IOException>(Component.ExpireGuestAsync);
        Assert.Equal(2, Device.Stops);

        Device.PendingConnect = null;
        Device.StopException = null;
        Assert.True((await Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken)).Accepted);
        Assert.Equal(3, Device.Stops);
        Assert.True(JoinGuest("guest-2", new("join-3", invitation.Token)).Accepted);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("device-io-failed")]
    [InlineData("device-not-ready")]
    public async Task ReconnectWaitsForOwedStopAndPreservesItsFailure(string failureCode)
    {
        await JoinAndAllowAsync("guest-1");
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        Device.StopException = new IOException("connection lost");
        Assert.False((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        Device.StopException = null;
        Device.PendingStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        try
        {
            Assert.False(connect.IsCompleted);
            Assert.Equal(2, Device.Stops);
            Assert.True((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
            Assert.False((await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken)).Accepted);
            Assert.False(JoinGuest("guest-2", new("join-2", invitation.Token)).Accepted);
        }
        finally
        {
            Device.PendingStop.TrySetException(ConnectionFailure(failureCode));
            await connect;
        }

        Assert.Equal(new ControlResponse("connect-1", failureCode, false), await connect);
        Assert.False((await Component.AllowGuestAsync("allow-3", TestContext.Current.CancellationToken)).Accepted);
        Assert.False(JoinGuest("guest-2", new("join-3", invitation.Token)).Accepted);
        Device.PendingStop = null;
        await Component.ExpireGuestAsync();
        Assert.Equal(3, Device.Stops);
        Assert.True(JoinGuest("guest-2", new("join-4", invitation.Token)).Accepted);
    }

    [Fact]
    public async Task AutomaticRetryWaitsForAllPendingConnectionAttempts()
    {
        await JoinAndAllowAsync("guest-1");
        Device.StopException = new IOException("connection lost");
        Assert.False((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.PendingConnect = firstAttempt;
        var firstConnect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        Device.PendingConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnect = Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken);
        firstAttempt.SetException(new IOException("setup failed"));
        Assert.Equal(new ControlResponse("connect-1", "device-io-failed", false), await firstConnect);

        try
        {
            await Component.ExpireGuestAsync();
            Assert.False(Device.PendingConnect.Task.IsCanceled);
            Assert.False(secondConnect.IsCompleted);
            Assert.Equal(1, Device.Stops);
        }
        finally
        {
            Device.StopException = null;
            Device.PendingConnect.TrySetResult();
            await secondConnect;
        }

        Assert.Equal(new ControlResponse("connect-2", "connected", true), await secondConnect);
        Assert.Equal(2, Device.Stops);
        Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
    }

    [Fact]
    public async Task GuestStopBlocksRequestUntilRuntimeCompletesWithoutRevokingPermission()
    {
        await JoinAndAllowAsync("guest-1");
        Device.PendingStop = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var stop = Component.StopGuestAsync("guest-1", "stop-1");
        var requestTask = Component.SubmitAsync(
            "guest-1",
            new("set-1", 0, 10, 20, 30, 40),
            TestContext.Current.CancellationToken);

        requestTask.IsCompleted.Should().BeTrue();
        var request = await requestTask;
        request.Should().Be(new SettingsResponse(
            "set-1",
            SettingsResponseStatus.Rejected,
            0,
            "device-not-ready"));
        Device.Sets.Should().Be(0);
        Device.PendingStop.SetResult();
        (await stop).Accepted.Should().BeTrue();
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
    }

    [Fact]
    public async Task AuthenticationDoesNotConsumeInvitationAndJoinDoesNotGrantControl()
    {
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var identity = Component.AuthenticateInvitation(invitation.Token);

        Assert.NotNull(identity);
        Assert.NotEqual(invitation.Token, identity);
        Assert.Equal(identity, Component.AuthenticateInvitation(invitation.Token));
        Assert.True(Component.JoinGuest("guest-1", identity, new("join-1", invitation.Token)).Accepted);
        Assert.Null(Component.AuthenticateInvitation(invitation.Token));
        var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.True(state.Joined);
        Assert.False(state.ControlAllowed);
    }

    [Fact]
    public async Task StaleIdentityCannotRedeemFreshInvitation()
    {
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var identity = Component.AuthenticateInvitation(invitation.Token);
        Assert.True(Component.JoinGuest("guest-1", identity, new("join-1", invitation.Token)).Accepted);
        Assert.True((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        var fresh = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;

        var stale = Component.JoinGuest("guest-2", identity, new("join-2", fresh.Token));

        Assert.Equal("invalid-invitation", stale.Code);
        Assert.False(stale.Accepted);
        Assert.True(JoinGuest("guest-3", new("join-3", fresh.Token)).Accepted);
    }

    private static Exception ConnectionFailure(string code) => code switch
    {
        "cancelled" => new OperationCanceledException(),
        "device-io-failed" => new IOException("connection failed"),
        "device-not-ready" => new InvalidOperationException("not connected"),
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    private async Task<GuestInvitation> JoinAndAllowAsync(string connectionId)
    {
        var invitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        JoinGuest(connectionId, new("join-1", invitation.Token)).Accepted.Should().BeTrue();
        (await Component.AllowGuestAsync("allow-1", TestContext.Current.CancellationToken)).Accepted.Should().BeTrue();
        return invitation;
    }

    private ControlResponse JoinGuest(string connectionId, GuestJoinRequest request) =>
        Component.JoinGuest(connectionId, Component.AuthenticateInvitation(request.InvitationToken), request);
}
