using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Api.Control;
using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Contracts;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class ControlSessionTests : MockerTestBase<ControlSession>, IAsyncDisposable
{
    private Mock<IOssmDevice>? _device;
    private FakeTimeProvider? _clock;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _device = DeviceMockSetup.ConnectedDevice(mocks);
        mocks.AddType<Func<string, IOssmDevice>>(_ => _device.Object);
        _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        mocks.AddType<TimeProvider>(_clock);
        mocks.SetupOptions(new ControlServiceOptions { DeviceIdentifier = "device-1" });
        mocks.GetOrCreateMock<ILogger<ControlSession>>().AsMoq()
            .Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    };

    private Mock<IOssmDevice> Device => _device ?? throw new InvalidOperationException("Test device is not initialized.");
    private FakeTimeProvider Clock => _clock ?? throw new InvalidOperationException("Test clock is not initialized.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
            Device.Verify(device => device.DisposeAsync(), Times.Once);
            Device.VerifyAdd(device => device.StateChanged += It.IsAny<EventHandler>(), Times.Once);
            Device.VerifyRemove(device => device.StateChanged -= It.IsAny<EventHandler>(), Times.Once);
        }
        finally
        {
            base.Dispose();
        }
    }

    [Fact]
    public async Task JoiningConsumesInvitationWithoutReplacingItsCode()
    {
        var token = TestContext.Current.CancellationToken;
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;

        Assert.True(JoinGuest("guest-1", new("join", invitation.Token)).Accepted);

        Assert.Equal(invitation, (await Component.GetUserStateAsync(token)).Invitation);
        Assert.Null(Component.AuthenticateInvitation(invitation.Token));
        Assert.False((await Component.GetGuestStateAsync("guest-1", token)).ControlAllowed);
    }

    [Fact]
    public async Task TransportLossStopsMotionWithoutEndingAdmissionOrPermission()
    {
        var token = TestContext.Current.CancellationToken;
        await JoinAndAllowAsync("guest-1");

        await Component.GuestDisconnectedAsync("guest-1");

        var state = await Component.GetUserStateAsync(token);
        Assert.True(state.GuestJoined);
        Assert.True(state.GuestControlAllowed);
        Assert.False((await Component.GetGuestStateAsync("guest-1", token)).Joined);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticStopFailureLeavesDiagnosticsAndRetriesWithoutReplayingSettings(bool expiry)
    {
        var token = TestContext.Current.CancellationToken;
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Throws(new IOException("Stop failed."));

        if (expiry)
            Clock.Advance(TimeSpan.FromHours(24));
        else
            await Component.GuestDisconnectedAsync("guest-1");

        var state = await Component.GetUserStateAsync(token);
        Assert.Equal(!expiry, state.GuestJoined);
        Assert.False(state.GuestConnected);
        Mocks.VerifyLogged(LogLevel.Warning, "Guest-session Stop cleanup did not complete (device-io-failed)", TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);

        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await Component.ExpireGuestAsync();

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Exactly(2));
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.VerifyLogged(LogLevel.Warning, "Guest-session Stop cleanup did not complete", TimesSpec.Once);
    }

    [Fact]
    public async Task SavedSessionResumesOnlyAfterStopAndNeverReplaysSettings()
    {
        var token = TestContext.Current.CancellationToken;
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;
        var redeemed = Component.RedeemInvitation(Component.AuthenticateInvitation(invitation.Token), new("redeem", invitation.Token));
        var identity = Component.AuthenticateGuestSession(redeemed.SessionToken);
        Assert.True(Component.JoinGuest("first", identity, "join").Accepted);
        Assert.True((await Component.AllowGuestAsync("allow", token)).Accepted);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Device.SetupGet(device => device.IsArmed).Returns(false))
            .Returns(stopped.Task);
        var disconnect = Component.GuestDisconnectedAsync("first");
        try
        {
            Assert.Equal(ControlResponseCodes.DeviceNotReady, Component.JoinGuest("second", identity, "early").Code);
            Assert.False((await Component.GetUserStateAsync(token)).GuestConnected);
            stopped.SetResult();
            await disconnect;
            Assert.True(Component.JoinGuest("second", identity, "resume").Accepted);
            var resumed = await Component.GetGuestStateAsync("second", token);
            Assert.True(resumed.ControlAllowed);
            Assert.True(resumed.CanResumeAfterStop);
            Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            await Component.GuestDisconnectedAsync("first");
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
            Assert.Equal(SettingsResponseStatus.Completed,
                (await Component.SubmitAsync("second", new("fresh", 0, 50, 30, 50, 50), token)).Status);
            Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        }
        finally
        {
            stopped.TrySetResult();
            await disconnect;
        }
    }

    [Fact]
    public async Task SecondTabCannotReplaceOrStopTheAttachedController()
    {
        var token = TestContext.Current.CancellationToken;
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;
        var redeemed = Component.RedeemInvitation(Component.AuthenticateInvitation(invitation.Token), new("redeem", invitation.Token));
        var identity = Component.AuthenticateGuestSession(redeemed.SessionToken);
        Assert.True(Component.JoinGuest("first", identity, "join").Accepted);
        Assert.True((await Component.AllowGuestAsync("allow", token)).Accepted);

        Assert.Equal(ControlResponseCodes.GuestAlreadyJoined, Component.JoinGuest("second", identity, "other").Code);
        await Component.GuestDisconnectedAsync("second");

        Assert.True((await Component.GetGuestStateAsync("first", token)).ControlAllowed);
        Assert.True(Component.GetSavedGuestSession(identity)!.Connected);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task OfflineLeaveCannotRevokeAnAttachedController()
    {
        var token = TestContext.Current.CancellationToken;
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;
        var redeemed = Component.RedeemInvitation(Component.AuthenticateInvitation(invitation.Token), new("redeem", invitation.Token));
        var identity = Component.AuthenticateGuestSession(redeemed.SessionToken);
        Assert.True(Component.GetSavedGuestSession(identity)!.CanAttach);
        Assert.True(Component.JoinGuest("first", identity, "join").Accepted);
        Assert.True((await Component.AllowGuestAsync("allow", token)).Accepted);

        var result = await Component.LeaveSavedGuestAsync(identity, "waiting-tab");

        Assert.False(result.Accepted);
        Assert.Equal(ControlResponseCodes.GuestAlreadyJoined, result.Code);
        Assert.Equal(identity, Component.AuthenticateGuestSession(redeemed.SessionToken));
        Assert.True((await Component.GetGuestStateAsync("first", token)).ControlAllowed);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Assert.True((await Component.LeaveGuestAsync("first", "owner-leave")).Accepted);
        Assert.Null(Component.AuthenticateGuestSession(redeemed.SessionToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedSessionExpiryDoesNotExtendAndRevokesAtTheExactBoundary(bool offline)
    {
        var token = TestContext.Current.CancellationToken;
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;
        var redeemed = Component.RedeemInvitation(Component.AuthenticateInvitation(invitation.Token), new("redeem", invitation.Token));
        var identity = Component.AuthenticateGuestSession(redeemed.SessionToken);
        var expiry = Clock.GetUtcNow().AddHours(24);
        Assert.Equal(expiry, redeemed.ExpiresAt);
        Assert.True(Component.JoinGuest("first", identity, "join").Accepted);
        Assert.True((await Component.AllowGuestAsync("allow", token)).Accepted);
        Clock.Advance(TimeSpan.FromHours(12));
        await Component.GuestDisconnectedAsync("first");
        if (!offline)
            Assert.True(Component.JoinGuest("second", identity, "resume").Accepted);
        Assert.Equal(expiry, Component.GetSavedGuestSession(identity)!.ExpiresAt);
        Clock.Advance(TimeSpan.FromHours(12) - TimeSpan.FromTicks(1));
        Assert.Equal(identity, Component.AuthenticateGuestSession(redeemed.SessionToken));

        Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(Component.AuthenticateGuestSession(redeemed.SessionToken));
        Assert.Null(Component.GetSavedGuestSession(identity));
        Assert.False((await Component.GetUserStateAsync(token)).GuestJoined);
        Assert.Equal(SettingsResponseStatus.Rejected,
            (await Component.SubmitAsync("second", new("late", 0, 50, 30, 50, 50), token)).Status);
        Assert.NotEqual(invitation, (await Component.GetUserStateAsync(token)).Invitation);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await Component.ExpireGuestAsync();
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Exactly(2));
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("end")]
    [InlineData("refresh")]
    public async Task SessionRevocationPrecedesStopFailureAndInvalidatesAuthenticatedIdentity(string action)
    {
        var token = TestContext.Current.CancellationToken;
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;
        var redeemed = Component.RedeemInvitation(Component.AuthenticateInvitation(invitation.Token), new("redeem", invitation.Token));
        var identity = Component.AuthenticateGuestSession(redeemed.SessionToken);
        Assert.True(Component.JoinGuest("first", identity, "join").Accepted);
        Assert.True((await Component.AllowGuestAsync("allow", token)).Accepted);
        if (action == "leave")
            await Component.GuestDisconnectedAsync("first");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("stop failed"));

        var result = await (action switch
        {
            "leave" => Component.LeaveSavedGuestAsync(identity, "leave"),
            "end" => Component.EndGuestAsync("end"),
            _ => Component.RefreshInvitationAsync("refresh"),
        });

        Assert.False(result.Accepted);
        Assert.Null(Component.AuthenticateGuestSession(redeemed.SessionToken));
        Assert.Null(Component.GetSavedGuestSession(identity));
        Assert.False((await Component.GetGuestStateAsync("first", token)).Joined);
        Assert.False((await Component.LeaveSavedGuestAsync(identity, "stale")).Accepted);
        Assert.NotEqual(invitation, (await Component.GetUserStateAsync(token)).Invitation);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task PreviewDimensionsAreSharedOnlyWithTheJoinedGuestAndIssueNoDeviceCommands()
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(new PreviewDimensions(), (await Component.GetUserStateAsync(token)).Preview);
        Assert.Null((await Component.GetGuestStateAsync("not-joined", token)).Preview);
        var invitation = (await Component.GetUserStateAsync(token)).Invitation;
        Assert.True(JoinGuest("guest-1", new("join", invitation.Token)).Accepted);
        var dimensions = new PreviewDimensions(45.5, 27.25);

        Assert.Equal(new ControlResponse("preview", ControlResponseCodes.PreviewUpdated, true), Component.SetPreviewDimensions("preview", dimensions));

        Assert.Equal(dimensions, (await Component.GetUserStateAsync(token)).Preview);
        Assert.Equal(dimensions, (await Component.GetGuestStateAsync("guest-1", token)).Preview);
        Assert.Null((await Component.GetGuestStateAsync("not-joined", token)).Preview);
        Assert.False((await Component.GetGuestStateAsync("guest-1", token)).ControlAllowed);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IOssmDevice>(device => device.ResetPositionAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(500, 500)]
    [InlineData(40, 80)]
    [InlineData(40.5, 24.25)]
    public async Task PreviewDimensionBoundariesPreserveTheirValues(double rail, double attachment)
    {
        var dimensions = new PreviewDimensions(rail, attachment);
        Assert.True(Component.SetPreviewDimensions("preview", dimensions).Accepted);
        Assert.Equal(dimensions, (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Preview);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(501, 24)]
    [InlineData(40, -1)]
    [InlineData(40, 501)]
    [InlineData(double.NaN, 24)]
    [InlineData(40, double.PositiveInfinity)]
    public async Task InvalidPreviewDimensionsDoNotReplaceSharedValues(double rail, double attachment)
    {
        var original = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Preview;
        var response = Component.SetPreviewDimensions("preview", new(rail, attachment));
        Assert.Equal(new ControlResponse("preview", ControlResponseCodes.InvalidPreviewDimensions, false), response);
        Assert.Equal(original, (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Preview);
        Assert.Equal(ControlResponseCodes.InvalidPreviewDimensions, Component.SetPreviewDimensions("missing", null).Code);
        Assert.Equal(ControlResponseCodes.InvalidCorrelation, Component.SetPreviewDimensions(string.Empty, new()).Code);
    }

    [Fact]
    public async Task AllowControlRearmsOnlyWhenRequiredWithoutSendingSettings()
    {
        await JoinAndAllowAsync("guest-1");
        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Assert.True((await Component.StopAsync("stop-1")).Accepted);
        Device.SetupGet(device => device.IsArmed).Returns(false);

        var response = await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken);

        Assert.True(response.Accepted);
        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
    }

    [Fact]
    public async Task FailedRearmDoesNotGrantPermission()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopAsync("stop-1");
        Device.SetupGet(device => device.IsArmed).Returns(false);
        Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>())).Throws(new IOException("Re-arm failed."));

        var response = await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken);

        Assert.Equal(new ControlResponse("allow-2", "device-io-failed", false), response);
        Assert.False((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task StopInvalidatesPendingAllowEvenAfterStopCompletes()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopAsync("stop-1");
        Device.SetupGet(device => device.IsArmed).Returns(false);
        var pendingReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>())).Returns(pendingReset.Task);
        var allow = Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken);
        try
        {
            Assert.False(allow.IsCompleted);
            Assert.True((await Component.StopAsync("stop-2")).Accepted);
        }
        finally
        {
            pendingReset.TrySetResult();
            await allow;
        }

        Assert.False((await allow).Accepted);
        Assert.False((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task GuestPermissionDoesNotExpire()
    {
        await JoinAndAllowAsync("guest-1");
        Clock.Advance(TimeSpan.FromHours(1));

        var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);

        Assert.True(state.ControlAllowed);
        Assert.Null(state.ControlExpiresAt);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task ResetPositionDiscardsPendingSettingsAndLeavesGuestStopped()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.SubmitAsync("guest-1", new("sent", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        var pendingSet = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>())).Returns(pendingSet.Task);
        Device.Setup(device => device.ResetPositionAsync(It.IsAny<CancellationToken>())).Returns(pendingReset.Task);
        var submit = Component.SubmitAsync("guest-1", new("pending", 0, 80, 40, 50, 60), TestContext.Current.CancellationToken);
        var reset = Component.ResetPositionGuestAsync("guest-1", "position", TestContext.Current.CancellationToken);
        try
        {
            Assert.False(reset.IsCompleted);
            Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
            Assert.Equal(
                SettingsResponseStatus.Rejected,
                (await Component.SubmitAsync("guest-1", new("during", 0, 80, 40, 50, 60), TestContext.Current.CancellationToken)).Status);
            pendingSet.TrySetResult(new(SetStatus.Completed, 5));
            Assert.Equal(SettingsResponseStatus.Cancelled, (await submit).Status);
            pendingReset.TrySetResult();
            Assert.Equal(new ControlResponse("position", "position-reset", true), await reset);
            Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
            Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).CanResumeAfterStop);
            Device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
            Device.Verify(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            pendingSet.TrySetResult(new(SetStatus.Cancelled, 0));
            pendingReset.TrySetResult();
            await Task.WhenAll(submit, reset);
        }
    }

    [Fact]
    public async Task FailedGuestStopRetryKeepsPermissionAndBlocksSettingsUntilSuccess()
    {
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("Stop failed."));
        Assert.False((await Component.StopGuestAsync("guest-1", "stop-1")).Accepted);
        var request = await Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        Assert.Equal(SettingsResponseStatus.Rejected, request.Status);
        Assert.Equal("device-not-ready", request.ErrorCode);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        await Assert.ThrowsAsync<IOException>(Component.ExpireGuestAsync);
        var failedRetry = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.True(failedRetry.ControlAllowed);
        Assert.False(failedRetry.CanResumeAfterStop);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await Component.ExpireGuestAsync();

        var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.True(state.ControlAllowed);
        Assert.True(state.CanResumeAfterStop);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Exactly(4));
    }

    [Theory]
    [InlineData("guest-2", "position", "guest-not-joined")]
    [InlineData("guest-1", "position", "control-not-allowed")]
    [InlineData("guest-1", "", "invalid-correlation")]
    [InlineData("guest-1", "bad\ncorrelation", "invalid-correlation")]
    public async Task ResetPositionRejectsUnauthorizedOrInvalidRequests(string guest, string correlation, string code)
    {
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        Assert.True(JoinGuest("guest-1", new("join", invitation.Token)).Accepted);

        Assert.Equal(
            new ControlResponse(correlation, code, false),
            await Component.ResetPositionGuestAsync(guest, correlation, TestContext.Current.CancellationToken));
        Assert.False((await Component.ResetPositionAsync(string.Empty, TestContext.Current.CancellationToken)).Accepted);

        Device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Never);
        Device.Verify(device => device.ResetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AdminResetPositionRearmsWithoutGuestOrSettingsReplay()
    {
        Device.SetupGet(device => device.IsArmed).Returns(false);

        Assert.Equal(
            new ControlResponse("position", "position-reset", true),
            await Component.ResetPositionAsync("position", TestContext.Current.CancellationToken));

        var state = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.False(state.GuestJoined);
        Assert.False(state.GuestControlAllowed);
        Assert.False(state.Device!.IsResettingPosition);
        Device.Verify(device => device.ResetAsync(It.IsAny<CancellationToken>()), Times.Once);
        Device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
        Device.Verify(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("guest-stop")]
    [InlineData("user-stop")]
    [InlineData("guest-disconnect")]
    [InlineData("device-disconnect")]
    [InlineData("caller-cancel")]
    public async Task ResetPositionInterruptionClearsBusyWithoutReplay(string interruption)
    {
        await JoinAndAllowAsync("guest-1");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => pending.Task.WaitAsync(token));
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => pending.TrySetCanceled(CancellationToken.None)).Returns(Task.CompletedTask);
        var reset = Component.ResetPositionGuestAsync("guest-1", "position", cancellation.Token);
        try
        {
            Assert.True((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.IsResettingPosition);
            Assert.False((await Component.ResetPositionAsync("overlap", TestContext.Current.CancellationToken)).Accepted);
            switch (interruption)
            {
                case "guest-stop":
                    Assert.True((await Component.StopGuestAsync("guest-1", "stop")).Accepted);
                    break;
                case "user-stop":
                    Assert.True((await Component.StopAsync("stop")).Accepted);
                    break;
                case "guest-disconnect":
                    await Component.GuestDisconnectedAsync("guest-1");
                    break;
                case "device-disconnect":
                    Device.SetupGet(device => device.IsConnected).Returns(false);
                    Device.Raise(device => device.StateChanged += null, EventArgs.Empty);
                    break;
                case "caller-cancel":
                    await cancellation.CancelAsync();
                    break;
            }

            Assert.Equal(new ControlResponse("position", "cancelled", false), await reset);
            var state = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
            Assert.False(state.Device!.IsResettingPosition);
            Assert.Null(state.Device.TransmittedSettings);
            if (interruption is "user-stop" or "device-disconnect")
                Assert.False(state.GuestControlAllowed);
            if (interruption == "guest-disconnect")
                Assert.True(state.GuestControlAllowed);
            Device.Verify(device => device.ResetPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
            Device.Verify(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            pending.TrySetCanceled(CancellationToken.None);
            await reset;
        }
    }

    [Fact]
    public async Task ResetPositionCleanupFailureIsRejectedAndClearsBusy()
    {
        var failure = new AggregateException(new IOException("Position write failed."), new IOException("Stop cleanup failed."));
        Device.Setup(device => device.ResetPositionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var response = await Component.ResetPositionAsync("position", TestContext.Current.CancellationToken);

        Assert.Equal(new ControlResponse("position", "device-io-failed", false), response);
        Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.IsResettingPosition);
    }

    [Fact]
    public async Task UserStateProjectsConnectionMetadataAndRequestedSettingsWithoutGuestDisclosure()
    {
        Device.SetupGet(device => device.IsConnected).Returns(false);
        Device.SetupGet(device => device.Name).Returns("Test device");
        var initial = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.False(initial.Device!.IsConnected);
        Assert.Null(initial.Device.ConnectedAt);
        Assert.Null(initial.Device.TransmittedSettings);
        var pendingConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(pendingConnect.Task);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        try
        {
            var connecting = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
            Assert.True(connecting.Device!.IsConnecting);
            Assert.Null(connecting.Device.ConnectedAt);
            Assert.Null(connecting.Device.TransmittedSettings);
            Device.SetupGet(device => device.IsConnected).Returns(true);
            pendingConnect.SetResult();
            Assert.True((await connect).Accepted);
        }
        finally
        {
            pendingConnect.TrySetResult();
            await connect;
        }
        var connectedAt = Clock.GetUtcNow();
        Clock.Advance(TimeSpan.FromMinutes(1));
        var observed = new DeviceState(StrokePattern.SimpleStroke, 80, 70, 60, 50);
        Device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(observed);
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
        Assert.Equal(observed.Speed, user.Device.Speed);
        var guest = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        Assert.Null(guest.Device!.Identifier);
        Assert.Null(guest.Device.Name);
        Assert.Null(guest.Device.TransmittedSettings);
        Device.SetupGet(device => device.IsConnected).Returns(false);
        var disconnected = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);
        Assert.Null(disconnected.Device!.ConnectedAt);
    }

    [Fact]
    public async Task GuestStopKeepsPermissionAndNextRequestRearms()
    {
        await JoinAndAllowAsync("guest-1");

        Assert.True((await Component.StopGuestAsync("guest-1", "stop-1")).Accepted);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
        Device.SetupGet(device => device.IsArmed).Returns(false);
        var request = await Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);

        Assert.Equal(SettingsResponseStatus.Completed, request.Status);
        Mocks.Verify<IOssmDevice>(device => device.ResetAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Fact]
    public async Task LaterGuestStopInvalidatesPendingResumeWithoutSendingSettings()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopGuestAsync("guest-1", "stop-1");
        Device.SetupGet(device => device.IsArmed).Returns(false);
        var pendingReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>())).Returns(pendingReset.Task);
        var submit = Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        try
        {
            Assert.False(submit.IsCompleted);
            Assert.True((await Component.StopGuestAsync("guest-1", "stop-2")).Accepted);
        }
        finally
        {
            pendingReset.TrySetResult();
            await submit;
        }

        var result = await submit;
        Assert.Equal(SettingsResponseStatus.Failed, result.Status);
        Assert.Equal("device-not-ready", result.ErrorCode);
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
    }

    [Fact]
    public async Task CancelledAllowDoesNotGrantPermission()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.StopAsync("stop-1");
        Device.SetupGet(device => device.IsArmed).Returns(false);
        var pendingReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => pendingReset.Task.WaitAsync(token));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var allow = Component.AllowGuestAsync("allow-2", cancellation.Token);
        try
        {
            Assert.False(allow.IsCompleted);
            await cancellation.CancelAsync();
            Assert.Equal(new ControlResponse("allow-2", "cancelled", false), await allow);
            Assert.False((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
            Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        }
        finally
        {
            pendingReset.TrySetResult();
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
        Mocks.Verify<IOssmDevice>(
            device => device.SetAsync(new MotionSettings(StrokePattern.RoboStroke, 20, 30, 40, 50), FastArg.Any<CancellationToken>()),
            TimesSpec.Once);
    }

    [Theory]
    [InlineData(SetStatus.Cancelled)]
    [InlineData(SetStatus.Superseded)]
    public async Task InterruptedRequestDoesNotReplaceLastSuccessfulRequestedSelection(SetStatus status)
    {
        await JoinAndAllowAsync("guest-1");
        await Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetResult(status, 2));

        var response = await Component.SubmitAsync("guest-1", new("set-2", 0, 80, 40, 50, 60), TestContext.Current.CancellationToken);

        Assert.Equal(status == SetStatus.Cancelled ? SettingsResponseStatus.Cancelled : SettingsResponseStatus.Superseded, response.Status);
        Assert.Equal(2, response.CommandsWritten);
        Assert.Equal(
            new DeviceSettingsSnapshot(0, 53, 10, 20, 50),
            (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
    }

    [Fact]
    public async Task FirmwareRejectionsRetainTheirCauseInsteadOfBecomingInvalidInput()
    {
        await JoinAndAllowAsync("guest-1");
        foreach (var (code, expected) in new[]
        {
            ("invalid_value", "invalid-settings"),
            ("guard_rejected", "device-not-ready"),
            ("lease_required", "device-control-lost"),
        })
        {
            Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DeviceWriteException(2, new IOException("Request failed.", new DeviceRequestException(code, "strokeEngine.pattern"))));
            var result = await Component.SubmitAsync("guest-1", new("set", 0, 57, 37, 100, 50), TestContext.Current.CancellationToken);
            Assert.Equal(SettingsResponseStatus.Failed, result.Status);
            Assert.Equal(2, result.CommandsWritten);
            Assert.Equal(expected, result.ErrorCode);
        }
    }

    [Fact]
    public async Task PendingAndFailedRequestAndStopRetainLastSuccessfulRequestedSelection()
    {
        await JoinAndAllowAsync("guest-1");
        await Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        var pendingSet = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>())).Returns(pendingSet.Task);
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
            pendingSet.TrySetException(new DeviceWriteException(2, new IOException("Write failed.")));
            await request;
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
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>())).Returns(olderCompletion.Task);
        var older = Component.SubmitAsync("guest-1", new("set-1", 0, 53, 10, 20, 50), TestContext.Current.CancellationToken);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>())).Returns(newerCompletion.Task);
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
    [InlineData(1.5, 75.0, 2)]
    [InlineData(2.5, 12.5, 20)]
    [InlineData(40.0, 400.0, 10)]
    [InlineData(0.0, 0.0, 10)]
    [InlineData(0.0, null, 0)]
    public async Task DeviceSnapshotPreservesRuntimeSpeed(double rawVelocity, double? speed, int stroke)
    {
        var observed = new DeviceState(StrokePattern.SimpleStroke, rawVelocity, stroke, 20, 50);
        Device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(observed);
        await JoinAndAllowAsync("guest-1");

        var guest = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
        var user = await Component.GetUserStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(speed, guest.Device!.Speed);
        Assert.Equal(speed, user.Device!.Speed);
        Assert.Null(user.Device.TransmittedSettings);
    }

    [Fact]
    public async Task PermissionCheckDoesNotExpireOrStopRuntime()
    {
        await JoinAndAllowAsync("guest-1");
        Clock.Advance(TimeSpan.FromSeconds(3));

        var response = await Component.RenewGuestAsync("guest-1", "renew-1");

        response.Should().Be(new ControlResponse("renew-1", "control-renewed", true));
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
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
        Mocks.Verify<IOssmDevice>(device => device.ConnectAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task ConfiguredIdentifierConnectsWithoutDiscovery()
    {
        var response = await Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);

        Assert.True(response.Accepted);
        Mocks.Verify<IOssmDevice>(device => device.ConnectAsync(TestContext.Current.CancellationToken), TimesSpec.Once);
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

        await Component.GuestDisconnectedAsync("guest-1");

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        var replacement = await Component.GetGuestStateAsync(
            "guest-2",
            TestContext.Current.CancellationToken);
        replacement.Joined.Should().BeTrue();
    }

    [Fact]
    public async Task StopReachesRuntimeWhileSettingsRemainPending()
    {
        await JoinAndAllowAsync("guest-1");
        var pendingSet = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>())).Returns(pendingSet.Task);
        var request = Component.SubmitAsync(
            "guest-1",
            new("set-1", 0, 10, 20, 30, 40),
            TestContext.Current.CancellationToken);

        try
        {
            var stop = await Component.StopGuestAsync("guest-1", "stop-1");

            stop.Accepted.Should().BeTrue();
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
            request.IsCompleted.Should().BeFalse();
            pendingSet.SetResult(new(SetStatus.Superseded, 0));
            (await request).Status.Should().Be(SettingsResponseStatus.Superseded);
        }
        finally
        {
            pendingSet.TrySetResult(new(SetStatus.Superseded, 0));
            await request;
        }
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
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task DeviceWriteFailurePreservesKnownProgress()
    {
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DeviceWriteException(2, new IOException("write failed")));

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
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
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
        Device.SetupGet(device => device.IsConnected).Returns(false);

        var response = await Component.StopAsync("stop-1");

        response.Should().Be(new ControlResponse("stop-1", "device-not-connected", false));
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
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
        var leave = await Component.LeaveGuestAsync("guest-1", "leave-1");
        var replacementInvitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        var replacement = JoinGuest("guest-2", new("join-2", replacementInvitation.Token));

        leave.Accepted.Should().BeTrue();
        replacement.Should().Be(new ControlResponse("join-2", "joined", true));
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Fact]
    public async Task GuestStopRejectsNonCurrentGuestWithoutStoppingRuntime()
    {
        await JoinAndAllowAsync("guest-1");

        var response = await Component.StopGuestAsync("guest-2", "stop-1");

        response.Should().Be(new ControlResponse("stop-1", "guest-not-joined", false));
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
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
        Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task PermissionPollingDoesNotStopAllowedGuest()
    {
        await JoinAndAllowAsync("guest-1");
        Clock.Advance(TimeSpan.FromSeconds(3));

        await Component.ExpireGuestAsync();
        await Component.ExpireGuestAsync();

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Fact]
    public async Task DisconnectedDeviceAbandonsFailedStopWithoutRetryOrReplay()
    {
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("connection lost"));
        Assert.False((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Device.SetupGet(device => device.IsConnected).Returns(false);

        await Component.ExpireGuestAsync();
        await Component.ExpireGuestAsync();

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Device.SetupGet(device => device.IsConnected).Returns(true))
            .Returns(Task.CompletedTask);
        Assert.True((await Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken)).Accepted);
        Assert.True((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.IsConnected);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Fact]
    public async Task FailedUserStopIsRetried()
    {
        await JoinAndAllowAsync("guest-1");
        Clock.Advance(TimeSpan.FromSeconds(3));
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("write failed"));

        Assert.False((await Component.StopAsync("stop-1")).Accepted);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await Component.ExpireGuestAsync();

        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Exactly(2));
    }

    [Fact]
    public async Task LeaveBlocksReplacementUntilStopSucceeds()
    {
        await JoinAndAllowAsync("guest-1");
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(pendingStop.Task);
        var leave = Component.LeaveGuestAsync("guest-1", "leave-1");
        var replacementInvitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        try
        {
            var replacement = JoinGuest("guest-2", new("join-2", replacementInvitation.Token));

            leave.IsCompleted.Should().BeFalse();
            replacement.Should().Be(new ControlResponse("join-2", "guest-already-joined", false));
            pendingStop.SetResult();
            (await leave).Accepted.Should().BeTrue();
            JoinGuest("guest-2", new("join-3", replacementInvitation.Token)).Accepted.Should().BeTrue();
        }
        finally
        {
            pendingStop.TrySetResult();
            await leave;
        }
    }

    [Fact]
    public async Task FailedLeaveStopIsDiscardedWithoutCancellingReconnect()
    {
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("connection lost"));
        var leave = await Component.LeaveGuestAsync("guest-1", "leave-1");
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        Assert.Equal(new ControlResponse("leave-1", "device-io-failed", false), leave);
        Device.SetupGet(device => device.IsConnected).Returns(false);
        var pendingConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(pendingConnect.Task);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);

        try
        {
            await Component.ExpireGuestAsync();
            await Component.RenewGuestAsync("guest-1", "renew-1");
            var state = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
            Assert.False(pendingConnect.Task.IsCanceled);
            Assert.False(state.Joined);
            Assert.False(state.ControlAllowed);
            Assert.False((await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken)).Accepted);
            Assert.True(JoinGuest("guest-2", new("join-2", invitation.Token)).Accepted);
            Assert.False(connect.IsCompleted);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        }
        finally
        {
            Device.SetupGet(device => device.IsConnected).Returns(true);
            pendingConnect.TrySetResult();
            await connect;
        }

        Assert.Equal(new ControlResponse("connect-1", "connected", true), await connect);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Assert.True((await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken)).Joined);
        Assert.False((await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken)).ControlAllowed);
        await Component.ExpireGuestAsync();
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitUserStopStillInterruptsConnection(bool priorStopFailed)
    {
        await JoinAndAllowAsync("guest-1");
        if (priorStopFailed)
        {
            Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("connection lost"));
            Assert.False((await Component.StopAsync("stop-1")).Accepted);
        }

        Device.SetupGet(device => device.IsConnected).Returns(false);
        var pendingConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => pendingConnect.Task.WaitAsync(token));
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback(() => pendingConnect.TrySetCanceled(CancellationToken.None))
            .Returns(Task.CompletedTask);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(new ControlResponse("stop-2", "stopped", true), await Component.StopAsync("stop-2"));
            Assert.True(pendingConnect.Task.IsCanceled);
            Assert.Equal(new ControlResponse("connect-1", "cancelled", false), await connect);
            Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestControlAllowed);
        }
        finally
        {
            pendingConnect.TrySetResult();
            await connect;
        }
    }

    [Fact]
    public async Task ElapsedTimeDoesNotInterruptConnection()
    {
        await JoinAndAllowAsync("guest-1");
        Device.SetupGet(device => device.IsConnected).Returns(false);
        var pendingConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(pendingConnect.Task);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        Clock.Advance(TimeSpan.FromSeconds(3));
        try
        {
            await Component.ExpireGuestAsync();
            Assert.False(pendingConnect.Task.IsCanceled);
            Assert.False(connect.IsCompleted);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        }
        finally
        {
            Device.SetupGet(device => device.IsConnected).Returns(true);
            pendingConnect.TrySetResult();
            await connect;
        }
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("device-io-failed")]
    [InlineData("device-not-ready")]
    public async Task FailedReconnectDoesNotRestoreDiscardedStopOrLeave(string failureCode)
    {
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("connection lost"));
        Assert.False((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        Device.SetupGet(device => device.IsConnected).Returns(false);
        var pendingConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(pendingConnect.Task);
        var connect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        pendingConnect.SetException(ConnectionFailure(failureCode));

        Assert.Equal(new ControlResponse("connect-1", failureCode, false), await connect);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Assert.False((await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken)).Accepted);
        await Component.ExpireGuestAsync();
        Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
        Assert.True(JoinGuest("guest-2", new("join-2", invitation.Token)).Accepted);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);

        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Device.SetupGet(device => device.IsConnected).Returns(true))
            .Returns(Task.CompletedTask);
        Assert.True((await Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken)).Accepted);
        Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Assert.True((await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken)).Joined);
        Assert.False((await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken)).ControlAllowed);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("device-io-failed")]
    [InlineData("device-not-ready")]
    public async Task LateStopCannotChangeReplacementGuestAfterReconnect(string failureCode)
    {
        await JoinAndAllowAsync("guest-1");
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken stopToken = default;
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback((CancellationToken token) => stopToken = token)
            .Returns(pendingStop.Task);
        var leave = Component.LeaveGuestAsync("guest-1", "leave-1");
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        try
        {
            Assert.False(leave.IsCompleted);
            Device.SetupGet(device => device.IsConnected).Returns(false);
            await Component.ExpireGuestAsync();
            Assert.True(stopToken.IsCancellationRequested);
            Assert.False(pendingStop.Task.IsCompleted);
            Assert.Equal(new ControlResponse("leave-1", "cancelled", false),
                await leave.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Device.SetupGet(device => device.IsConnected).Returns(true))
                .Returns(Task.CompletedTask);
            Assert.True((await Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken)).Accepted);
            Assert.True(JoinGuest("guest-2", new("join-2", invitation.Token)).Accepted);
            Assert.True((await Component.AllowGuestAsync("allow-2", TestContext.Current.CancellationToken)).Accepted);
            if (failureCode == "completed")
            {
                pendingStop.SetResult();
            }
            else
            {
                pendingStop.SetException(ConnectionFailure(failureCode));
            }
            Assert.False((await leave).Accepted);
            var state = await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken);
            Assert.True(state.Joined);
            Assert.True(state.ControlAllowed);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        }
        finally
        {
            pendingStop.TrySetCanceled(CancellationToken.None);
            await leave;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSettingsCannotChangeReplacementGuestAfterUnpolledReconnect(bool rearming)
    {
        await JoinAndAllowAsync("guest-1");
        if (rearming)
        {
            Assert.True((await Component.StopGuestAsync("guest-1", "stop-1")).Accepted);
            Device.SetupGet(device => device.IsArmed).Returns(false);
        }

        var pendingSet = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>()))
            .Callback((CancellationToken token) => workToken = token)
            .Returns(pendingReset.Task);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Callback((MotionSettings settings, CancellationToken token) => workToken = token)
            .Returns(pendingSet.Task);
        var request = Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        try
        {
            Assert.False(request.IsCompleted);
            Device.SetupGet(device => device.IsConnected).Returns(false);
            Device.Raise(device => device.StateChanged += null, EventArgs.Empty);
            Device.SetupGet(device => device.IsConnected).Returns(true);
            Device.Raise(device => device.StateChanged += null, EventArgs.Empty);

            Assert.Equal(new SettingsResponse("set-1", SettingsResponseStatus.Cancelled, 0),
                await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(workToken.IsCancellationRequested);
            Assert.False(rearming ? pendingReset.Task.IsCompleted : pendingSet.Task.IsCompleted);
            var disconnectedGuest = await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken);
            Assert.True(disconnectedGuest.Joined);
            Assert.False(disconnectedGuest.ControlAllowed);
            Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);

            Assert.True((await Component.EndGuestAsync("end-1")).Accepted);
            Device.SetupGet(device => device.IsArmed).Returns(true);
            Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SetResult(SetStatus.Completed, 5));
            await JoinAndAllowAsync("guest-2");
            Assert.Equal(SettingsResponseStatus.Completed,
                (await Component.SubmitAsync("guest-2", new("set-2", 0, 60, 70, 80, 90), TestContext.Current.CancellationToken)).Status);

            pendingReset.TrySetResult();
            pendingSet.TrySetResult(new(SetStatus.Completed, 5));
            var replacement = await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken);
            Assert.True(replacement.Joined);
            Assert.True(replacement.ControlAllowed);
            Assert.Equal(new DeviceSettingsSnapshot(0, 60, 70, 80, 90),
                (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
            Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()),
                TimesSpec.Exactly(rearming ? 1 : 2));
        }
        finally
        {
            pendingReset.TrySetResult();
            pendingSet.TrySetResult(new(SetStatus.Cancelled, 0));
            await request;
        }
    }

    [Fact]
    public async Task PendingSettingsCannotChangeReplacementGuestOnSameConnection()
    {
        await JoinAndAllowAsync("guest-1");
        var pendingSet = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Returns(pendingSet.Task);
        var request = Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), TestContext.Current.CancellationToken);
        try
        {
            Assert.False(request.IsCompleted);
            Assert.True((await Component.EndGuestAsync("end-1")).Accepted);
            await JoinAndAllowAsync("guest-2");

            // End guest cancels through thread-pool callbacks; observe that before releasing the late device result.
            Assert.Equal(new SettingsResponse("set-1", SettingsResponseStatus.Cancelled, 0),
                await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            pendingSet.SetResult(new(SetStatus.Completed, 2));
            var replacement = await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken);
            Assert.True(replacement.Joined);
            Assert.True(replacement.ControlAllowed);
            Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
            Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.Once);
        }
        finally
        {
            pendingSet.TrySetResult(new(SetStatus.Cancelled, 0));
            await request;
        }
    }

    [Fact]
    public async Task CallerCancellationTerminatesPendingSettingsWithoutDisconnect()
    {
        await JoinAndAllowAsync("guest-1");
        var pendingSet = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Callback((MotionSettings settings, CancellationToken token) => workToken = token)
            .Returns(pendingSet.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), cancellation.Token);
        try
        {
            await cancellation.CancelAsync();

            Assert.True(workToken.IsCancellationRequested);
            Assert.False(pendingSet.Task.IsCompleted);
            Assert.Equal(new SettingsResponse("set-1", SettingsResponseStatus.Cancelled, 0),
                await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            pendingSet.SetException(new DeviceWriteException(2, new IOException("Late write failure.")));
            Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
            Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
        }
        finally
        {
            pendingSet.TrySetResult(new(SetStatus.Cancelled, 0));
            await request;
        }
    }

    [Fact]
    public async Task CancellationPreservesRuntimeProgressWhenAlreadyAvailable()
    {
        await JoinAndAllowAsync("guest-1");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
            .Returns((MotionSettings settings, CancellationToken token) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new SetResult(SetStatus.Cancelled, 2));
            });

        var response = await Component.SubmitAsync("guest-1", new("set-1", 0, 10, 20, 30, 40), cancellation.Token);

        Assert.Equal(new SettingsResponse("set-1", SettingsResponseStatus.Cancelled, 2), response);
        Assert.Null((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Device!.TransmittedSettings);
    }

    [Fact]
    public async Task PendingStopWaitersTerminateAcrossUnpolledDisconnectAndReconnect()
    {
        await JoinAndAllowAsync("guest-1");
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken stopToken = default;
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
            .Callback((CancellationToken token) => stopToken = token)
            .Returns(pendingStop.Task);
        var stop = Component.StopGuestAsync("guest-1", "stop-1");
        var leave = Component.LeaveGuestAsync("guest-1", "leave-1");
        try
        {
            Device.SetupGet(device => device.IsConnected).Returns(false);
            Device.Raise(device => device.StateChanged += null, EventArgs.Empty);
            Device.SetupGet(device => device.IsConnected).Returns(true);
            Device.Raise(device => device.StateChanged += null, EventArgs.Empty);

            Assert.True(stopToken.IsCancellationRequested);
            Assert.False(pendingStop.Task.IsCompleted);
            Assert.Equal(new ControlResponse("stop-1", "cancelled", false),
                await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(new ControlResponse("leave-1", "cancelled", false),
                await leave.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
            await JoinAndAllowAsync("guest-2");
            pendingStop.SetException(new IOException("Late Stop failure."));
            await Component.ExpireGuestAsync();
            var replacement = await Component.GetGuestStateAsync("guest-2", TestContext.Current.CancellationToken);
            Assert.True(replacement.Joined);
            Assert.True(replacement.ControlAllowed);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        }
        finally
        {
            pendingStop.TrySetResult();
            await Task.WhenAll(stop, leave);
        }
    }

    [Fact]
    public async Task DiscardedStopIsNotRetriedDuringConcurrentConnections()
    {
        await JoinAndAllowAsync("guest-1");
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("connection lost"));
        Assert.False((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ControlResponse>? secondConnect = null;
        Device.SetupGet(device => device.IsConnected).Returns(false);
        Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(firstAttempt.Task);
        var firstConnect = Component.ConnectAsync("connect-1", TestContext.Current.CancellationToken);
        try
        {
            Device.Setup(device => device.ConnectAsync(It.IsAny<CancellationToken>())).Returns(secondAttempt.Task);
            secondConnect = Component.ConnectAsync("connect-2", TestContext.Current.CancellationToken);
            firstAttempt.SetException(new IOException("setup failed"));
            Assert.Equal(new ControlResponse("connect-1", "device-io-failed", false), await firstConnect);

            await Component.ExpireGuestAsync();
            Assert.False(secondAttempt.Task.IsCanceled);
            Assert.False(secondConnect.IsCompleted);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);

            Device.SetupGet(device => device.IsConnected).Returns(true);
            secondAttempt.SetResult();
            Assert.Equal(new ControlResponse("connect-2", "connected", true), await secondConnect);
            Mocks.Verify<IOssmDevice>(device => device.EmergencyStopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
            Assert.False((await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestJoined);
        }
        finally
        {
            firstAttempt.TrySetResult();
            secondAttempt.TrySetResult();
            await Task.WhenAll(firstConnect, (Task?)secondConnect ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task GuestStopBlocksRequestUntilRuntimeCompletesWithoutRevokingPermission()
    {
        await JoinAndAllowAsync("guest-1");
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(pendingStop.Task);

        var stop = Component.StopGuestAsync("guest-1", "stop-1");
        try
        {
            var requestTask = Component.SubmitAsync(
                "guest-1",
                new("set-1", 0, 10, 20, 30, 40),
                TestContext.Current.CancellationToken);
            try
            {
                requestTask.IsCompleted.Should().BeTrue();
                var request = await requestTask;
                request.Should().Be(new SettingsResponse(
                    "set-1",
                    SettingsResponseStatus.Rejected,
                    0,
                    "device-not-ready"));
                Mocks.Verify<IOssmDevice>(device => device.SetAsync(FastArg.Any<MotionSettings>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
                pendingStop.SetResult();
                (await stop).Accepted.Should().BeTrue();
                Assert.True((await Component.GetGuestStateAsync("guest-1", TestContext.Current.CancellationToken)).ControlAllowed);
            }
            finally
            {
                pendingStop.TrySetResult();
                await requestTask;
            }
        }
        finally
        {
            pendingStop.TrySetResult();
            await stop;
        }
    }

    [Fact]
    public async Task AuthenticationDoesNotConsumeInvitationAndJoinDoesNotGrantControl()
    {
        var invitation = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;
        var identity = Component.AuthenticateInvitation(invitation.Token);

        Assert.NotNull(identity);
        Assert.NotEqual(invitation.Token, identity);
        Assert.Equal(identity, Component.AuthenticateInvitation(invitation.Token));
        Assert.True(JoinGuest("guest-1", new("join-1", invitation.Token)).Accepted);
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
        Assert.True(JoinGuest("guest-1", new("join-1", invitation.Token)).Accepted);
        Assert.True((await Component.LeaveGuestAsync("guest-1", "leave-1")).Accepted);
        var fresh = (await Component.GetUserStateAsync(TestContext.Current.CancellationToken)).Invitation;

        var stale = Component.RedeemInvitation(identity, new("join-2", fresh.Token));

        Assert.Equal("invalid-invitation", stale.Code);
        Assert.False(stale.Accepted);
        Assert.True(JoinGuest("guest-3", new("join-3", fresh.Token)).Accepted);
    }

    private static Exception ConnectionFailure(string code) => code switch
    {
        "cancelled" => new OperationCanceledException(),
        "device-io-failed" => new IOException("connection failed"),
        "device-not-ready" => new InvalidOperationException("not connected"),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    private async Task<GuestInvitation> JoinAndAllowAsync(string connectionId)
    {
        var invitation = (await Component.GetUserStateAsync(
            TestContext.Current.CancellationToken)).Invitation;
        JoinGuest(connectionId, new("join-1", invitation.Token)).Accepted.Should().BeTrue();
        (await Component.AllowGuestAsync("allow-1", TestContext.Current.CancellationToken)).Accepted.Should().BeTrue();
        return invitation;
    }

    private ControlResponse JoinGuest(string connectionId, GuestJoinRequest request)
    {
        var redeemed = Component.RedeemInvitation(Component.AuthenticateInvitation(request.InvitationToken), request);
        return redeemed.Accepted
            ? Component.JoinGuest(connectionId, Component.AuthenticateGuestSession(redeemed.SessionToken), request.Correlation)
            : new(request.Correlation, redeemed.Code, false);
    }
}
