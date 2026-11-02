using CsCheck;
using Moq;
using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using OssmSharp.Web.Contracts;
using Xunit;
using static OssmSharp.Web.Contracts.ControlResponseCodes;
using AllowEvent = OssmSharp.Web.Api.Tests.Control.Models.PendingAllowEvent;
using Event = OssmSharp.Web.Api.Tests.Control.Models.PendingSessionEvent;
using EventKind = OssmSharp.Web.Api.Tests.Control.Models.PendingSessionEventKind;
using Pending = OssmSharp.Web.Api.Tests.Control.Models.PendingSettingsRequest;

namespace OssmSharp.Web.Api.Tests.Control;

public sealed class ControlSessionPendingTests(ITestOutputHelper output) : ControlSessionTestCase
{
    [Fact]
    public async Task GeneratedPendingSettingsKeepNewerSelectionsPermissionAndObservationsIndependent()
    {
        // Favor overlapping requests and replies without omitting any lifecycle event.
        await Gen.Int[0, 100].Array[3].Select(values => new Event(
                (values[0] % 32) switch
                {
                    < 8 => EventKind.Settings,
                    < 12 => EventKind.CompleteOldest,
                    < 16 => EventKind.CompleteNewest,
                    var choice => (EventKind)(choice - 16),
                }, values[1], values[2]))
            .Array[0, 24].SampleAsync(CheckPendingSettingsAsync, threads: 1, writeLine: output.WriteLine);

        // Reduced from the native mutation counterexample: the older completion
        // must not replace the independently observed newer completed request.
        await CheckPendingSettingsAsync(
        [
            new(EventKind.Settings, 6, 12),
            new(EventKind.Settings, 12, 19),
            new(EventKind.CompleteNewest, 0, 0),
            new(EventKind.CompleteOldest, 0, 0),
        ]);

        async Task CheckPendingSettingsAsync(Event[] events)
            {
                await using var test = CreateCase();
                var redeemed = test.Redeem(true);
                Assert.True(test.Component.JoinGuest("tab-1", test.Identity, "join").Accepted);
                Assert.True((await test.Component.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);
                var admitted = true;
                var permission = true;
                var connected = true;
                string? attached = "tab-1";
                long epoch = 0, sequence = 0, completedSequence = 0;
                var armed = true;
                DeviceSettingsSnapshot? sent = null;
                double expectedSpeed = 50;
                var expectedStroke = 25;
                var emitted = new List<MotionSettings>();
                var expectedEmitted = new List<SettingsRequest>();
                var pending = new List<Pending>();
                Pending? justIssued = null;
                SettingsRequest? requested = null;
                test.Device.SetupGet(device => device.IsArmed).Returns(() => armed);
                test.Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => armed = true).Returns(Task.CompletedTask);
                test.Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
                    .Callback(() => armed = false).Returns(Task.CompletedTask);
                test.Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
                    .Returns((MotionSettings settings, CancellationToken work) =>
                    {
                        emitted.Add(settings);
                        var result = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        justIssued = new(requested!, epoch, sequence, result, work);
                        pending.Add(justIssued);
                        return result.Task; // Deliberately ignores cancellation: the device may complete late.
                    });
                try
                {
                    foreach (var action in events)
                    {
                        switch (action.Kind)
                        {
                            case EventKind.Settings:
                            case EventKind.InvalidSettings:
                                requested = new($"request-{expectedEmitted.Count}", 0, action.Kind == EventKind.InvalidSettings ? 101 : action.Speed, action.Stroke, 17, 29);
                                justIssued = null;
                                var caller = attached ?? "tab-1";
                                var eligible = action.Kind != EventKind.InvalidSettings && admitted && attached is not null && permission && connected;
                                if (eligible)
                                {
                                    expectedEmitted.Add(requested);
                                    sequence++;
                                }
                                var reply = test.Component.SubmitAsync(caller, requested, TestContext.Current.CancellationToken);
                                if (eligible)
                                {
                                    Assert.NotNull(justIssued);
                                    justIssued.Reply = reply;
                                    Assert.False(reply.IsCompleted);
                                }
                                else
                                {
                                    var result = await reply;
                                    Assert.Equal(SettingsResponseStatus.Rejected, result.Status);
                                    Assert.Equal(action.Kind == EventKind.InvalidSettings ? InvalidSettings : attached is null || !admitted ? GuestNotJoined : ControlNotAllowed, result.ErrorCode);
                                    Assert.Equal(0, result.CommandsWritten);
                                    Assert.Equal(requested.Correlation, result.Correlation);
                                }
                                break;
                            case EventKind.CompleteOldest:
                            case EventKind.CompleteNewest:
                                var candidates = pending.Where(work => !work.Settled).ToArray();
                                if (candidates.Length == 0)
                                    break;
                                var completion = action.Kind == EventKind.CompleteOldest ? candidates[0] : candidates[^1];
                                completion.DeviceResult.TrySetResult(new(SetStatus.Completed, 5));
                                var completed = await completion.Reply!;
                                var current = completion.Epoch == epoch && admitted && permission && attached is not null && connected;
                                Assert.Equal(current ? SettingsResponseStatus.Completed : SettingsResponseStatus.Cancelled, completed.Status);
                                Assert.Equal(completion.Request.Correlation, completed.Correlation);
                                if (current)
                                {
                                    Assert.Equal(5, completed.CommandsWritten);
                                    if (completion.Sequence > completedSequence)
                                    {
                                        completedSequence = completion.Sequence;
                                        sent = new(0, completion.Request.Speed, completion.Request.Stroke, 17, 29);
                                    }
                                }
                                else if (!completion.WorkToken.IsCancellationRequested)
                                    Assert.Equal(5, completed.CommandsWritten);
                                else
                                    Assert.Contains(completed.CommandsWritten, new[] { 0, 5 });
                                completion.Settled = true;
                                break;
                            case EventKind.GuestStop:
                                var guestStop = await test.Component.StopGuestAsync(attached ?? "missing", "guest-stop");
                                Assert.Equal(attached is null || !admitted ? GuestNotJoined : !connected ? DeviceNotConnected : Stopped, guestStop.Code);
                                Assert.Equal(attached is not null && admitted && connected, guestStop.Accepted);
                                if (attached is not null && admitted)
                                    epoch++;
                                break;
                            case EventKind.UserStop:
                            case EventKind.FailedUserStop:
                                if (action.Kind == EventKind.FailedUserStop)
                                    test.Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("Isolated stop rejection."));
                                var userStop = await test.Component.StopAsync("user-stop");
                                permission = false;
                                epoch++;
                                Assert.Equal(!connected ? DeviceNotConnected : action.Kind == EventKind.FailedUserStop ? DeviceIoFailed : Stopped, userStop.Code);
                                Assert.Equal(connected && action.Kind != EventKind.FailedUserStop, userStop.Accepted);
                                Assert.False((await test.Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestControlAllowed);
                                test.Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>()))
                                    .Callback(() => armed = false).Returns(Task.CompletedTask);
                                await test.Component.ExpireGuestAsync(); // Explicit successful stop retry, never restores permission.
                                break;
                            case EventKind.Detach:
                                if (attached is not null)
                                {
                                    await test.Component.GuestDisconnectedAsync(attached);
                                    attached = null;
                                    sent = null;
                                    epoch++;
                                }
                                break;
                            case EventKind.JoinFirst:
                            case EventKind.JoinSecond:
                                var next = action.Kind == EventKind.JoinFirst ? "tab-1" : "tab-2";
                                var joined = test.Component.JoinGuest(next, test.Identity, "join");
                                Assert.Equal(!admitted ? InvalidGuestSession : attached is not null ? GuestAlreadyJoined : Joined, joined.Code);
                                Assert.Equal(admitted && attached is null, joined.Accepted);
                                if (admitted && attached is null)
                                    attached = next;
                                break;
                            case EventKind.Refresh:
                                var refreshed = await test.Component.RefreshInvitationAsync("refresh");
                                Assert.Equal(!connected && admitted ? DeviceNotConnected : InvitationRefreshed, refreshed.Code);
                                // Without a connected device an admitted session still revokes, even if Stop fails.
                                Assert.Equal(connected || !admitted, refreshed.Accepted);
                                admitted = permission = false;
                                attached = null;
                                sent = null;
                                epoch++;
                                break;
                            case EventKind.Expire:
                                test.Clock.Advance(TimeSpan.FromHours(24));
                                await test.Component.ExpireGuestAsync();
                                admitted = permission = false;
                                attached = null;
                                sent = null;
                                epoch++;
                                break;
                            case EventKind.DeviceLoss:
                                test.LoseDevice();
                                connected = permission = false;
                                sent = null;
                                epoch++;
                                break;
                            case EventKind.Reconnect:
                                Assert.True((await test.ReconnectAsync()).Accepted);
                                connected = true;
                                armed = true;
                                break;
                            case EventKind.Observe:
                                test.Device.Setup(device => device.GetStateAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                                    .ReturnsAsync(new DeviceState(StrokePattern.RoboStroke, 7.5, 3, 17, 29));
                                expectedSpeed = 250;
                                expectedStroke = 3;
                                break;
                            case EventKind.Allow:
                                var allowed = await test.Component.AllowGuestAsync("allow", TestContext.Current.CancellationToken);
                                Assert.Equal(!admitted ? GuestNotJoined : !connected ? DeviceNotReady : ControlAllowed, allowed.Code);
                                Assert.Equal(admitted && connected, allowed.Accepted);
                                if (admitted && connected)
                                    permission = true;
                                break;
                        }

                        Assert.Equal(expectedEmitted.Select(request => (request.Pattern, request.Speed, request.Stroke, request.Depth, request.Sensation)),
                            emitted.Select(settings => ((int)settings.Pattern, settings.Speed, settings.Stroke, settings.Depth, settings.Sensation)));
                        var user = await test.Component.GetUserStateAsync(TestContext.Current.CancellationToken);
                        Assert.Equal(admitted, user.GuestJoined);
                        Assert.Equal(attached is not null, user.GuestConnected);
                        Assert.Equal(permission, user.GuestControlAllowed);
                        Assert.Equal(sent, user.Device!.TransmittedSettings);
                        Assert.Equal(expectedSpeed, user.Device.Speed);
                        Assert.Equal(expectedStroke, user.Device.Stroke);
                        Assert.Equal(admitted, test.Component.AuthenticateGuestSession(redeemed.SessionToken) is not null);
                    }
                }
                finally
                {
                    test.LoseDevice();
                    foreach (var work in pending)
                        work.DeviceResult.TrySetResult(new(SetStatus.Completed, 5));
                    await Task.WhenAll(pending.Select(work => work.Reply!));
                }
            }
    }

    [Fact]
    public async Task GeneratedPendingGuestRearmIssuesSettingsOnlyForCurrentPermission()
    {
        await Gen.Int[0, 6].Array[0, 16].SampleAsync(CheckRearmAsync, threads: 1, writeLine: output.WriteLine);

        // Native seed 0LwE7rz5_oI1 reduced the cancellation/settlement race to refresh alone.
        await CheckRearmAsync([3]);

        async Task CheckRearmAsync(int[] events)
        {
            await using var test = CreateCase();
            test.Redeem(true);
            Assert.True(test.Component.JoinGuest("tab-1", test.Identity, "join").Accepted);
            Assert.True((await test.Component.AllowGuestAsync("allow", TestContext.Current.CancellationToken)).Accepted);
            Assert.True((await test.Component.StopGuestAsync("tab-1", "stop")).Accepted);
            var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            test.Device.SetupGet(device => device.IsArmed).Returns(false);
            test.Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>()))
                .Callback(() => issued.TrySetResult()).Returns(reset.Task);
            var settings = new List<MotionSettings>();
            test.Device.Setup(device => device.SetAsync(It.IsAny<MotionSettings>(), It.IsAny<CancellationToken>()))
                .Callback<MotionSettings, CancellationToken>((value, _) => settings.Add(value))
                .ReturnsAsync(new SetResult(SetStatus.Completed, 5));
            var rearm = test.Component.SubmitAsync("tab-1", new("rearm", 0, 53, 10, 17, 29), TestContext.Current.CancellationToken);
            var invalidated = false;
            var workCancelled = false;
            var permission = true;
            var attached = true;
            var admitted = true;
            try
            {
                await issued.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(rearm.IsCompleted);
                foreach (var action in events)
                {
                    switch (action)
                    {
                        case 0:
                            if (attached)
                            {
                                await test.Component.StopGuestAsync("tab-1", "stop");
                                invalidated = true;
                            }
                            break;
                        case 1:
                            await test.Component.StopAsync("stop");
                            permission = false;
                            invalidated = true;
                            break;
                        case 2:
                            await test.Component.GuestDisconnectedAsync("tab-1");
                            if (attached)
                                invalidated = workCancelled = true;
                            attached = false;
                            break;
                        case 3:
                            await test.Component.RefreshInvitationAsync("refresh");
                            admitted = permission = attached = false;
                            invalidated = workCancelled = true;
                            break;
                        case 4:
                            test.Clock.Advance(TimeSpan.FromHours(24));
                            await test.Component.ExpireGuestAsync();
                            admitted = permission = attached = false;
                            invalidated = workCancelled = true;
                            break;
                        case 5:
                            test.LoseDevice();
                            permission = false;
                            invalidated = workCancelled = true;
                            break;
                        case 6:
                            await test.ReconnectAsync();
                            break;
                    }
                    var state = await test.Component.GetUserStateAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(permission, state.GuestControlAllowed);
                    Assert.Equal(admitted, state.GuestJoined);
                    Assert.Empty(settings);
                    Assert.Null(state.Device!.TransmittedSettings);
                }
                if (workCancelled)
                {
                    // Cancellation is delivered asynchronously. Await the actual reply
                    // checkpoint before settling Reset, rather than racing the two.
                    var cancelled = await rearm.WaitAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(SettingsResponseStatus.Cancelled, cancelled.Status);
                    Assert.False(reset.Task.IsCompleted);
                }
                reset.TrySetResult();
                var result = await rearm;
                Assert.Equal(!invalidated ? SettingsResponseStatus.Completed : workCancelled ? SettingsResponseStatus.Cancelled : SettingsResponseStatus.Failed, result.Status);
                Assert.Equal(invalidated ? 0 : 5, result.CommandsWritten);
                Assert.Equal(invalidated && !workCancelled ? DeviceNotReady : null, result.ErrorCode);
                Assert.Equal("rearm", result.Correlation);
                Assert.Equal(invalidated ? 0 : 1, settings.Count);
                if (!invalidated)
                {
                    var sent = Assert.Single(settings);
                    Assert.Equal((0, 53, 10, 17, 29), ((int)sent.Pattern, sent.Speed, sent.Stroke, sent.Depth, sent.Sensation));
                }
                var final = await test.Component.GetUserStateAsync(TestContext.Current.CancellationToken);
                Assert.Equal(invalidated ? null : new DeviceSettingsSnapshot(0, 53, 10, 17, 29), final.Device!.TransmittedSettings);
                Assert.Equal(50.0, final.Device.Speed);
                Assert.Equal(25, final.Device.Stroke);
            }
            finally
            {
                reset.TrySetResult();
                await rearm;
            }
        }
    }

    [Fact]
    public Task GeneratedPendingAllowAndRearmCannotRestoreRevokedPermission() =>
        Gen.Int[0, 9].Array[0, 20].SampleAsync(async events =>
        {
            await using var test = CreateCase();
            test.Redeem(true);
            Assert.True(test.Component.JoinGuest("tab-1", test.Identity, "join").Accepted);
            var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resetIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resets = 0;
            test.Device.SetupGet(device => device.IsArmed).Returns(false);
            test.Device.Setup(device => device.ResetAsync(It.IsAny<CancellationToken>()))
                .Callback(() => { resets++; resetIssued.TrySetResult(); }).Returns(reset.Task);
            var allow = test.Component.AllowGuestAsync("pending-allow", TestContext.Current.CancellationToken);
            var admitted = true;
            var connected = true;
            string? attached = "tab-1";
            var invalidated = false;
            var allowed = false;
            var finished = false;
            try
            {
                await resetIssued.Task.WaitAsync(TestContext.Current.CancellationToken);
                foreach (var raw in events)
                {
                    switch ((AllowEvent)raw)
                    {
                        case AllowEvent.FinishReset:
                            reset.TrySetResult();
                            var result = await allow;
                            if (!finished)
                            {
                                allowed = !invalidated && admitted && connected;
                                Assert.Equal(allowed, result.Accepted);
                                Assert.Equal(allowed ? ControlAllowed : DeviceNotReady, result.Code);
                                Assert.Equal("pending-allow", result.Correlation);
                                finished = true;
                            }
                            break;
                        case AllowEvent.GuestStop:
                            if (attached == "tab-1")
                            {
                                await test.Component.StopGuestAsync("tab-1", "stop");
                                invalidated = true;
                            }
                            break;
                        case AllowEvent.UserStop:
                        case AllowEvent.FailedUserStop:
                            if ((AllowEvent)raw == AllowEvent.FailedUserStop)
                                test.Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Throws(new IOException("Stop failed."));
                            var stopped = await test.Component.StopAsync("stop");
                            Assert.Equal(connected && (AllowEvent)raw != AllowEvent.FailedUserStop, stopped.Accepted);
                            allowed = false;
                            invalidated = true;
                            test.Device.Setup(device => device.EmergencyStopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
                            await test.Component.ExpireGuestAsync();
                            break;
                        case AllowEvent.Detach:
                            await test.Component.GuestDisconnectedAsync("tab-1");
                            if (attached == "tab-1")
                            {
                                attached = null;
                                invalidated = true;
                            }
                            break;
                        case AllowEvent.SecondTab:
                            var joined = test.Component.JoinGuest("tab-2", test.Identity, "second");
                            Assert.Equal(!admitted ? InvalidGuestSession : attached is not null ? GuestAlreadyJoined : Joined, joined.Code);
                            if (admitted && attached is null)
                                attached = "tab-2";
                            break;
                        case AllowEvent.Refresh:
                            await test.Component.RefreshInvitationAsync("refresh");
                            admitted = allowed = false;
                            attached = null;
                            invalidated = true;
                            break;
                        case AllowEvent.Expire:
                            test.Clock.Advance(TimeSpan.FromHours(24));
                            await test.Component.ExpireGuestAsync();
                            admitted = allowed = false;
                            attached = null;
                            invalidated = true;
                            break;
                        case AllowEvent.DeviceLoss:
                            test.LoseDevice();
                            connected = allowed = false;
                            invalidated = true;
                            break;
                        case AllowEvent.Reconnect:
                            await test.ReconnectAsync();
                            connected = true;
                            break;
                    }
                    var state = await test.Component.GetUserStateAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(admitted, state.GuestJoined);
                    Assert.Equal(attached is not null, state.GuestConnected);
                    Assert.Equal(allowed, state.GuestControlAllowed);
                    Assert.Null(state.Device!.TransmittedSettings);
                    Assert.Equal(50.0, state.Device.Speed);
                    Assert.Equal(1, resets);
                }
                if (!finished)
                {
                    reset.TrySetResult();
                    var completion = await allow;
                    var expectedAllowed = !invalidated && admitted && connected;
                    Assert.Equal(expectedAllowed, completion.Accepted);
                    Assert.Equal(expectedAllowed ? ControlAllowed : DeviceNotReady, completion.Code);
                    Assert.Equal("pending-allow", completion.Correlation);
                    Assert.Equal(expectedAllowed, (await test.Component.GetUserStateAsync(TestContext.Current.CancellationToken)).GuestControlAllowed);
                }
                Assert.Equal(1, resets);
            }
            finally
            {
                reset.TrySetResult();
                await allow;
            }
        }, threads: 1, writeLine: output.WriteLine);
}
