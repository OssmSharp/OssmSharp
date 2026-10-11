using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Moq;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using OssmSharp.Web.GuestWeb.Components.Pages;
using OssmSharp.Web.GuestWeb.Services;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class JoinComponentTests : MockerTestBase<Join>, IAsyncDisposable
{
    private Mock<IGuestApiClient> _client = null!;
    private RecordingJsModule _module = null!;
    private FakeTimeProvider _time = null!;
    private StateOnlyRenderer _renderer = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _client = mocks.GetOrCreateMock<IGuestApiClient>().AsMoq();
        _module = new();
        _time = new();
        mocks.AddType<TimeProvider>(_time);
        mocks.GetOrCreateMock<AuthenticationStateProvider>().AsMoq()
            .Setup(provider => provider.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(GuestSessionEndpoints.CredentialClaim, "saved-credential")], GuestSessionEndpoints.Scheme))));
        mocks.GetOrCreateMock<IJSRuntime>().AsMoq()
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(_module));
        _client.Setup(client => client.DisposeAsync()).Returns(ValueTask.CompletedTask);
    };

    protected override Action<Join> CreatedComponentAction => component =>
    {
        ComponentTestAccess.Inject(component, "Client", Mocks.GetRequiredObject<IGuestApiClient>());
        ComponentTestAccess.Inject(component, "Time", _time);
        ComponentTestAccess.Inject(component, "JavaScript", Mocks.GetRequiredObject<IJSRuntime>());
        ComponentTestAccess.Inject(component, "Logger", NullLogger<Join>.Instance);
        ComponentTestAccess.Inject(component, "Authentication", Mocks.GetRequiredObject<AuthenticationStateProvider>());
        _renderer = new(component);
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _renderer.DisposeAsync();
            await _module.DisposeAsync();
        }
        finally { base.Dispose(); }
    }

    [Fact]
    public async Task ReadyRequiresPermissionAndAUsableDevice()
    {
        Assert.False(Read<bool>("Ready"));
        Assert.Equal(new BrowserReply(false, "not-ready"), await Component.SubmitSettings(new(0, 50, 10, 20, 75)));
        var usable = Device();
        foreach (var (device, allowed, resume, expected) in new (DeviceStateSnapshot?, bool, bool, bool)[]
        {
            (null, true, false, false), (null, true, true, false),
            (usable with { IsConnected = false }, true, false, false),
            (usable with { IsConnecting = true }, true, false, false),
            (usable with { IsArmed = false }, true, false, false),
            (usable with { ConnectionFailed = true }, true, false, false),
            (usable with { IsArmed = false, ConnectionFailed = true }, true, true, false),
            (usable, false, false, false), (usable, true, false, true),
            (usable with { IsArmed = false }, true, true, true),
        })
        {
            SetState(new(true, allowed, null, device, resume));
            Assert.Equal(expected, Read<bool>("Ready"));
            if (!expected)
                Assert.Equal(new BrowserReply(false, "not-ready"), await Component.SubmitSettings(new(0, 50, 10, 20, 75)));
        }
        Mocks.Verify<IGuestApiClient>(client => client.SubmitAsync(FastArg.Any<SettingsRequest>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IGuestApiClient>(client => client.RenewAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        foreach (var field in new[] { "_leaving", "_connectionLost", "_disposed" })
        {
            ComponentTestAccess.Set(Component, field, true);
            Assert.False(Read<bool>("Ready"));
            ComponentTestAccess.Set(Component, field, false);
        }
        ComponentTestAccess.Set(Component, "_joined", false);
        Assert.False(Read<bool>("Ready"));
        SetState(new(false, true, null, usable));
        Assert.False(Read<bool>("Ready"));
    }

    [Fact]
    public async Task SubmitSettingsRejectsUnsupportedOrInvalidValuesBeforeRpc()
    {
        SetState(new(true, true, null, Device()));
        var valid = new BrowserSettings(0, 50, 10, 20, 75);
        foreach (var settings in new[]
        {
            valid with { Pattern = 7 }, valid with { Speed = -1 }, valid with { Speed = 101 },
            valid with { Sensation = -1 }, valid with { Sensation = 101 },
            valid with { Depth = -1 }, valid with { Depth = 101 },
            valid with { Stroke = -1 }, valid with { Stroke = 101 }, valid with { Stroke = 21 },
        })
            Assert.Equal(new BrowserReply(false, "invalid-input"), await Component.SubmitSettings(settings));
        Mocks.Verify<IGuestApiClient>(client => client.SubmitAsync(FastArg.Any<SettingsRequest>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        var requests = new List<SettingsRequest>();
        _client.Setup(client => client.SubmitAsync(It.IsAny<SettingsRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SettingsRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return Task.FromResult(new SettingsResponse(request.Correlation, SettingsResponseStatus.Completed, 5));
            });
        for (var pattern = 0; pattern <= 6; pattern++)
        {
            var settings = new BrowserSettings(pattern, pattern == 0 ? 0 : 100, 100, 100, pattern == 0 ? 0 : 100);
            Assert.Equal(new BrowserReply(true, "Completed"), await Component.SubmitSettings(settings));
            var request = requests[^1];
            Assert.Equal(
                (settings.Pattern, settings.Speed, settings.Stroke, settings.Depth, settings.Sensation),
                (request.Pattern, request.Speed, request.Stroke, request.Depth, request.Sensation));
            Assert.True(Guid.TryParseExact(request.Correlation, "N", out _));
        }
        Assert.Equal(7, requests.Count);
        Assert.Equal(7, requests.Select(request => request.Correlation).Distinct().Count());
        Assert.Equal(new BrowserReply(true, "Completed"), await Component.SubmitSettings(new(0, 0, 0, 0, 0)));
        var zero = requests[^1];
        Assert.Equal((0, 0, 0, 0, 0), (zero.Pattern, zero.Speed, zero.Stroke, zero.Depth, zero.Sensation));
        Assert.Equal(8, requests.Count);
        _client.Setup(client => client.SubmitAsync(It.IsAny<SettingsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SettingsResponse("rejected", SettingsResponseStatus.Rejected, 0, "invalid-settings"));
        Assert.Equal(new BrowserReply(false, "invalid-settings"), await Component.SubmitSettings(valid));
        _client.Setup(client => client.SubmitAsync(It.IsAny<SettingsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcClientRequestTimeoutException(new TimeoutException()));
        Assert.Equal(new BrowserReply(false, "outcome-unknown"), await Component.SubmitSettings(valid));
        _client.Setup(client => client.SubmitAsync(It.IsAny<SettingsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException("control-not-allowed"));
        Assert.Equal(new BrowserReply(false, "control-not-allowed"), await Component.SubmitSettings(valid));
        _client.Setup(client => client.SubmitAsync(It.IsAny<SettingsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (SettingsRequest _, CancellationToken token) =>
            {
                await Read<CancellationTokenSource>("_lifetime").CancelAsync();
                token.ThrowIfCancellationRequested();
                return new SettingsResponse("unused", SettingsResponseStatus.Completed, 5);
            });
        Assert.Equal(new BrowserReply(false, "outcome-unknown"), await Component.SubmitSettings(valid));
    }

    [Theory]
    [InlineData("invalid-guest-session", false, false)]
    [InlineData("connection-unavailable", true, false)]
    [InlineData("outcome-unknown", true, false)]
    [InlineData("connection-unavailable", true, true)]
    [InlineData("outcome-unknown", true, true)]
    public async Task InitializationDistinguishesInvalidSessionAndRecoverableLoss(string code, bool lost, bool afterJoin)
    {
        Exception failure = code == "outcome-unknown"
            ? new RpcClientRequestTimeoutException(new TimeoutException())
            : new ApiClientException(code);
        if (afterJoin)
        {
            _client.Setup(client => client.ResumeAsync("saved-credential", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ControlResponse("join", "joined", true));
            _client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        }
        else
            _client.Setup(client => client.ResumeAsync("saved-credential", It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "OnAfterRenderAsync", true));
        Assert.True(Read<bool>("_initialized"));
        Assert.False(Read<bool>("_joined"));
        Assert.Equal(afterJoin, Read<bool>("_everJoined"));
        Assert.False(Read<bool>("_joining"));
        Assert.Equal(lost, Read<bool>("_connectionLost"));
        Assert.Equal(lost ? 1 : 0, _module.Calls.Count(call => call.Method == "disconnect"));
        Mocks.Verify<IGuestApiClient>(client => client.ResumeAsync("saved-credential", FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IGuestApiClient>(client => client.GetStateAsync(FastArg.Any<CancellationToken>()), TimesSpec.Exactly(afterJoin ? 1 : 0));
        AssertNoMotion();
    }

    [Fact]
    public async Task InitializationRejectionCannotCreateAnAutomaticReloadLoop()
    {
        _client.Setup(client => client.ResumeAsync("saved-credential", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException("request-failed"));

        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "OnAfterRenderAsync", true));

        Assert.True(Read<bool>("_initialized"));
        Assert.False(Read<bool>("_joined"));
        Assert.False(Read<bool>("_connectionLost"));
        Assert.Equal("initialization-failed", Read<string>("_sessionCode"));
        Assert.Single(_module.Calls, call => call.Method == "endSession");
        Assert.DoesNotContain(_module.Calls, call => call.Method == "disconnect");
        AssertNoMotion();
    }

    [Fact]
    public async Task BrowserImportFailureRendersATerminalErrorWithoutAttachingControl()
    {
        Mocks.GetOrCreateMock<IJSRuntime>().AsMoq()
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Throws(new JSException("Import failed"));

        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "OnAfterRenderAsync", true));

        Assert.True(Read<bool>("_initialized"));
        Assert.False(Read<bool>("_joined"));
        Assert.False(Read<bool>("_connectionLost"));
        Assert.Equal("initialization-failed", Read<string>("_sessionCode"));
        Assert.Contains("could not start", Read<string>("EndedMessage"));
        Mocks.Verify<IGuestApiClient>(client => client.ResumeAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        AssertNoMotion();
    }

    [Fact]
    public async Task InitializationUsesSavedServerPrincipalWithoutExposingCredentialToBrowser()
    {
        _client.Setup(client => client.ResumeAsync("saved-credential", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ControlResponse("join", "joined", true));
        _client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestSessionState(true, true, null, Device()));

        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "OnAfterRenderAsync", true));

        Assert.True(Read<bool>("_joined"));
        var display = Read<GuestDisplayState>("_display");
        Assert.Equal(0, display.Selected.Speed);
        Assert.Null(display.Sent);
        Assert.True(display.PreviewStopped);
        Assert.DoesNotContain(_module.Calls, call => call.Method == "takeInvitation");
        Assert.DoesNotContain(_module.Calls.SelectMany(call => call.Arguments), value => Equals(value, "saved-credential"));
        Mocks.Verify<IGuestApiClient>(client => client.RedeemAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        AssertNoMotion();
    }

    [Fact]
    public async Task MissingSavedPrincipalCannotStartAnApiConnection()
    {
        Mocks.GetOrCreateMock<AuthenticationStateProvider>().AsMoq()
            .Setup(provider => provider.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        _module.SessionCode = "no-session";

        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "OnAfterRenderAsync", true));

        Assert.True(Read<bool>("_initialized"));
        Assert.False(Read<bool>("_joined"));
        Mocks.Verify<IGuestApiClient>(client => client.ResumeAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        AssertNoMotion();
    }

    [Fact]
    public async Task SecondTabWaitsWithoutStoppingTheConnectedTab()
    {
        _client.Setup(client => client.ResumeAsync("saved-credential", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiClientException(ControlResponseCodes.GuestAlreadyJoined));

        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "OnAfterRenderAsync", true));

        Assert.True(Read<bool>("_connectionLost"));
        Assert.False(Read<bool>("_joined"));
        var disconnected = Assert.Single(_module.Calls, call => call.Method == "disconnect");
        Assert.Equal("session-in-use", Assert.Single(disconnected.Arguments));
        Mocks.Verify<IGuestApiClient>(client => client.StopAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IGuestApiClient>(client => client.GetStateAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        AssertNoMotion();
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("unexpected-rejection")]
    [InlineData("connection-unavailable")]
    [InlineData("outcome-unknown")]
    public async Task PollingDistinguishesEndedAndRecoverableLoss(string code)
    {
        var last = new GuestSessionState(true, true, null, Device());
        SetState(last);
        ComponentTestAccess.Set(Component, "_everJoined", true);
        ComponentTestAccess.Set(Component, "_attached", true);
        ComponentTestAccess.Set(Component, "_module", _module);
        ComponentTestAccess.Set(Component, "_display", new GuestDisplayState(new(6, 35, 10, 20, 75), new(6, 35, 10, 20, 75), "sent", null));
        if (code == "ended")
            _client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(last with { Joined = false });
        else
            _client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(code == "outcome-unknown"
                    ? new RpcClientRequestTimeoutException(new TimeoutException())
                    : new ApiClientException(code));
        Task poll = Task.CompletedTask;
        await _renderer.RunAsync(() =>
        {
            poll = ComponentTestAccess.InvokeTask(Component, "PollAsync");
            return Task.CompletedTask;
        });
        Mocks.Verify<IGuestApiClient>(client => client.GetStateAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        _time.Advance(TimeSpan.FromSeconds(1));
        await poll.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(Read<bool>("_joined"));
        Assert.True(Read<bool>("_everJoined"));
        var recoverable = code is "connection-unavailable" or "outcome-unknown";
        Assert.Equal(recoverable, Read<bool>("_connectionLost"));
        Assert.False(Read<bool>("Ready"));
        if (code == "ended")
        {
            Assert.False(Read<GuestSessionState>("_status").Joined);
            Assert.DoesNotContain(_module.Calls, call => call.Method == "disconnect");
            Assert.Single(_module.Calls, call => call.Method == "endSession");
            Assert.Contains(_module.Calls, call => call.Method == "updateState");
        }
        else if (recoverable)
        {
            Assert.Same(last, Read<GuestSessionState>("_status"));
            Assert.Single(_module.Calls, call => call.Method == "disconnect");
            var display = Read<GuestDisplayState>("_display");
            Assert.Equal(new BrowserSettings(6, 0, 10, 20, 75), display.Selected);
            Assert.Null(display.Sent);
        }
        else
        {
            Assert.Same(last, Read<GuestSessionState>("_status"));
            Assert.Equal("session-read-failed", Read<string>("_sessionCode"));
            Assert.Single(_module.Calls, call => call.Method == "endSession");
            Assert.DoesNotContain(_module.Calls, call => call.Method == "disconnect");
            Assert.Equal(0, Read<GuestDisplayState>("_display").Selected.Speed);
            Assert.Null(Read<GuestDisplayState>("_display").Sent);
        }
        AssertNoMotion();
    }

    [Theory]
    [InlineData(12.5, 20)]
    [InlineData(33.333333333333336, 30)]
    [InlineData(null, 0)]
    [InlineData(50.0, null)]
    [InlineData(400.0, 10)]
    public async Task DisplayPreservesCurrentSpeedAndRecoveryState(double? speed, int? stroke)
    {
        SetState(new(true, true, null, Device() with { Speed = speed, Stroke = stroke, IsResettingPosition = true }, true));
        ComponentTestAccess.Set(Component, "_attached", true);
        ComponentTestAccess.Set(Component, "_module", _module);
        var display = new GuestDisplayState(new(6, 35, 1, 20, 75), null, "Choose settings, then Send.", null, true);
        await _renderer.RunAsync(() => Component.UpdateDisplay(display));
        await ComponentTestAccess.InvokeTask(Component, "PushStateAsync");
        var pushed = Assert.Single(_module.Calls, call => call.Method == "updateState").Arguments[0]!;
        var current = ComponentTestAccess.Get<object>(pushed, "current");
        Assert.Equal(speed, Read<double?>("DeviceSpeed"));
        Assert.Equal(speed, ComponentTestAccess.Get<double?>(current, "speed"));
        Assert.Equal(stroke, ComponentTestAccess.Get<int?>(current, "stroke"));
        Assert.Equal(50, ComponentTestAccess.Get<int?>(current, "sensation"));
        Assert.True(ComponentTestAccess.Get<bool>(pushed, "stopped"));
        Assert.True(ComponentTestAccess.Get<bool>(pushed, "resettingPosition"));
        Assert.True(ComponentTestAccess.Get<bool>(pushed, "ready"));
        Assert.Same(display, Read<GuestDisplayState>("_display"));
        AssertNoMotion();
    }

    [Fact]
    public void GuestPreviewUsesSharedDimensionsWithoutChangingSelectionOrCallingTheDevice()
    {
        var dimensions = new PreviewDimensions(48.5, 27.25);
        SetState(new(true, true, null, Device()) { Preview = dimensions });
        var preview = Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"));
        Assert.Equal(48.5, (double)preview["RailLength"]!);
        Assert.Equal(27.25, (double)preview["AttachmentLength"]!);
        Assert.Equal(0.0, (double)preview["Speed"]!);
        AssertNoMotion();
    }

    [Fact]
    public async Task BrowserCallUsesLifetimeCancellationWithoutFailingDisposal()
    {
        ComponentTestAccess.Set(Component, "_module", _module);
        _module.BeforeInvoke = async token =>
        {
            Assert.Equal(Read<CancellationTokenSource>("_lifetime").Token, token);
            await Read<CancellationTokenSource>("_lifetime").CancelAsync();
            token.ThrowIfCancellationRequested();
        };
        await ComponentTestAccess.InvokeTask(Component, "BrowserCallAsync", "updateState", Array.Empty<object?>());
        Assert.False(Read<bool>("_connectionLost"));
        _module.BeforeInvoke = null;
        AssertNoMotion();
    }

    [Fact]
    public async Task UnexpectedBrowserFailureClosesControlAndDiscardsLateDisplay()
    {
        SetState(new(true, true, null, Device()));
        ComponentTestAccess.Set(Component, "_module", _module);
        var selection = new BrowserSettings(6, 35, 10, 20, 75);
        var previousDisplay = new GuestDisplayState(selection, selection, "Settings sent.", null, CanSend: true);
        ComponentTestAccess.Set(Component, "_display", previousDisplay);
        _module.BeforeInvoke = _ => Task.FromException(new JSException("Browser call failed."));
        try
        {
            await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "BrowserCallAsync", "updateState", Array.Empty<object?>()));
            Assert.True(Read<bool>("_connectionLost"));
            _client.Verify<ValueTask>(client => client.DisposeAsync(), Times.Once);
            Assert.True(Read<CancellationTokenSource>("_lifetime").IsCancellationRequested);
            Assert.False(Read<bool>("_joined"));
            Assert.False(Read<bool>("Ready"));
            Assert.Null(Read<GuestSessionState?>("_status"));
            var display = Read<GuestDisplayState>("_display");
            Assert.Equal(0, display.Selected.Speed);
            Assert.Null(display.Sent);
            Assert.True(display.PreviewStopped);
            Assert.False(display.CanSend);
            await _renderer.RunAsync(() => Component.UpdateDisplay(previousDisplay));
            Assert.Same(display, Read<GuestDisplayState>("_display"));
            Assert.Equal(new BrowserReply(false, "not-ready"), await Component.SubmitSettings(selection));
            AssertNoMotion();
        }
        finally
        {
            _module.BeforeInvoke = null;
        }
    }

    [Fact]
    public async Task LeavingDisarmsBrowserBeforeWaitingForService()
    {
        SetState(new(true, true, null, Device()));
        ComponentTestAccess.Set(Component, "_attached", true);
        ComponentTestAccess.Set(Component, "_module", _module);
        var completed = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(client => client.LeaveAsync(It.IsAny<CancellationToken>())).Returns(completed.Task);
        Task leaving = Task.CompletedTask;
        await _renderer.RunAsync(() =>
        {
            leaving = ComponentTestAccess.InvokeTask(Component, "LeaveAsync");
            return Task.CompletedTask;
        });
        try
        {
            var pushed = Assert.Single(_module.Calls, call => call.Method == "updateState").Arguments[0]!;
            Assert.False(ComponentTestAccess.Get<bool>(pushed, "ready"));
            Assert.False(Read<bool>("Ready"));
            Assert.False(leaving.IsCompleted);
            Assert.DoesNotContain(_module.Calls, call => call.Method == "completeLeave");
            Assert.Equal(new BrowserReply(false, "not-ready"), await Component.SubmitSettings(new(0, 50, 10, 20, 75)));
            AssertNoMotion();
        }
        finally
        {
            completed.TrySetResult(new("leave", "stopped", true));
            await leaving;
        }
        Assert.False(Read<bool>("_joined"));
        Assert.Single(_module.Calls, call => call.Method == "completeLeave");
        Mocks.Verify<IGuestApiClient>(client => client.LeaveAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IGuestApiClient>(client => client.LeaveSavedSessionAsync(FastArg.Any<string>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }

    [Theory]
    [InlineData("device-io-failed")]
    [InlineData("connection-unavailable")]
    [InlineData("invalid-response")]
    public async Task ConnectedLeaveFailureClearsBrowserWithoutClaimingSuccess(string code)
    {
        SetState(new(true, true, null, Device()));
        ComponentTestAccess.Set(Component, "_module", _module);
        _client.Setup(client => client.LeaveAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ApiClientException(code));

        await _renderer.RunAsync(() => ComponentTestAccess.InvokeTask(Component, "LeaveAsync"));

        var cleared = Assert.Single(_module.Calls, call => call.Method == "completeLeave");
        Assert.Equal(new BrowserReply(false, code), Assert.Single(cleared.Arguments));
        Assert.Equal("leave-failed", Read<string>("_sessionCode"));
        Assert.False(Read<bool>("_joined"));
        Mocks.Verify<IGuestApiClient>(client => client.LeaveAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        AssertNoMotion();
    }

    [Fact]
    public async Task ResetAndStopUseDistinctRpcAndPreserveResultCodes()
    {
        SetState(new(true, true, null, Device()));
        _client.Setup(client => client.ResetPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlResponse("reset", "position-reset", true));
        _client.Setup(client => client.StopAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlResponse("stop", "stopped", true));
        Assert.Equal(new BrowserReply(true, "position-reset"), await Component.ResetPosition());
        SetState(new(true, false, null, Device()));
        Assert.Equal(new BrowserReply(false, "not-ready"), await Component.ResetPosition());
        Assert.Equal(new BrowserReply(true, "stopped"), await Component.StopRun());
        Mocks.Verify<IGuestApiClient>(client => client.ResetPositionAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IGuestApiClient>(client => client.StopAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        AssertNoMotion();
    }

    private static DeviceStateSnapshot Device() => new(0, 50, 10, 20, 50, true, false, true);

    private T Read<T>(string name) => ComponentTestAccess.Get<T>(Component, name);

    private void SetState(GuestSessionState state)
    {
        ComponentTestAccess.Set(Component, "_joined", state.Joined);
        ComponentTestAccess.Set(Component, "_status", state);
    }

    private void AssertNoMotion()
    {
        Mocks.Verify<IGuestApiClient>(client => client.SubmitAsync(FastArg.Any<SettingsRequest>(), FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Mocks.Verify<IGuestApiClient>(client => client.RenewAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
    }
}
