using FastMoq;
using FastMoq.Extensions;
using FastMoq.Providers;
using FastMoq.Providers.MoqProvider;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OssmSharp.Web.AdminWeb;
using OssmSharp.Web.AdminWeb.Components.Pages;
using OssmSharp.Web.Client.Clients;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;
using OssmSharp.Web.ServiceDefaults.Models;
using Xunit;

namespace OssmSharp.Web.Api.Tests;

public sealed class HomeComponentTests : MockerTestBase<Home>, IAsyncDisposable
{
    private readonly List<Task> _polls = [];
    private Mock<IAdminApiClient> _client = null!;
    private FakeTimeProvider _time = null!;
    private StateOnlyRenderer _renderer = null!;
    private UserSessionState _state = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        _client = mocks.GetOrCreateMock<IAdminApiClient>().AsMoq();
        _time = new();
        _state = State(null);
        mocks.AddType<TimeProvider>(_time);
        mocks.SetupOptions(new AdminOptions { GuestHostname = "https://guest.example" });
        _client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(_state));
        _client.Setup(client => client.DisposeAsync()).Returns(ValueTask.CompletedTask);
    };

    protected override Action<Home> CreatedComponentAction => component =>
    {
        ComponentTestAccess.Inject(component, "Control", Mocks.GetRequiredObject<IAdminApiClient>());
        ComponentTestAccess.Inject(component, "Time", _time);
        ComponentTestAccess.Inject(component, "Options", Mocks.GetRequiredObject<IOptions<AdminOptions>>());
        ComponentTestAccess.Set(component, "_state", _state);
        ComponentTestAccess.Invoke(component, "OnInitialized");
        _renderer = new(component);
    };

    private UserNoticeState Notice => Read<UserNoticeState>("_notice");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _renderer.DisposeAsync();
            await Task.WhenAll(_polls);
        }
        finally { base.Dispose(); }
    }

    [Theory]
    [InlineData("Connect", "Stop", false)]
    [InlineData("Connect", "EndGuest", false)]
    [InlineData("AllowGuest", "Stop", false)]
    [InlineData("AllowGuest", "EndGuest", false)]
    [InlineData("Connect", "Stop", true)]
    [InlineData("Connect", "EndGuest", true)]
    [InlineData("AllowGuest", "Stop", true)]
    [InlineData("AllowGuest", "EndGuest", true)]
    public async Task SafetyCommandsBypassNormalBusyAndBoundTheirOwnDuplicates(string normal, string safety, bool normalFirst)
    {
        var pending = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var urgent = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupCommand(normal, pending.Task);
        SetupCommand(safety, urgent.Task);
        Task normalTask = Task.CompletedTask;
        Task safetyTask = Task.CompletedTask;
        await _renderer.RunAsync(() =>
        {
            normalTask = Call(normal);
            var buttons = ComponentTestAccess.Buttons(Component);
            Assert.False(buttons["Stop"]);
            Assert.False(buttons["End guest"]);
            Assert.True(buttons[normal == "Connect" ? "Connect device" : "Allow control"]);
            Assert.True(buttons["Reset Position"]);
            safetyTask = Call(safety);
            return Task.CompletedTask;
        });
        Assert.True(Read<bool>("_busy"));
        Assert.True(Read<bool>("_safetyBusy"));
        Assert.Null(Read<UserSessionState>("_state").Device?.TransmittedSettings);
        var preview = Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"));
        foreach (var parameter in new[] { "Pattern", "Speed", "Stroke", "Depth", "Sensation" })
            Assert.Null(preview[parameter]);
        await _renderer.RunAsync(() => Call(normal));
        await _renderer.RunAsync(() => Call(safety));
        Assert.Equal(1, CommandCalls(normal));
        Assert.Equal(1, CommandCalls(safety));
        if (normalFirst)
        {
            pending.SetResult(new("normal", "accepted", true));
            await normalTask;
            Assert.False(Read<bool>("_busy"));
            Assert.True(Read<bool>("_safetyBusy"));
            urgent.SetResult(new("safety", "accepted", true));
            await safetyTask;
        }
        else
        {
            urgent.SetResult(new("safety", "accepted", true));
            await safetyTask;
            Assert.True(Read<bool>("_busy"));
            Assert.False(Read<bool>("_safetyBusy"));
            Mocks.Verify<IAdminApiClient>(client => client.GetStateAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
            pending.SetResult(new("normal", "accepted", true));
            await normalTask;
            if (normal == "Connect")
                Assert.Contains("Device connection is not confirmed.", Notice.Message);
        }
        Assert.False(Read<bool>("_busy"));
        Assert.False(Read<bool>("_safetyBusy"));
        Assert.False(Read<bool>("_connecting"));
        Assert.Null(Read<UserSessionState>("_state").Device?.TransmittedSettings);
        Assert.Equal(1, CommandCalls(normal));
        Assert.Equal(1, CommandCalls(safety));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedAndUnknownSafetyCommandsReleaseOnlyTheirOwnBusyGate(bool timeout)
    {
        var normal = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupCommand("Connect", normal.Task);
        SetupCommand("EndGuest", Task.FromException<ControlResponse>(timeout
            ? new RpcClientRequestTimeoutException(new TimeoutException())
            : new ApiClientException("cancelled")));
        Task pending = Task.CompletedTask;
        await _renderer.RunAsync(() =>
        {
            pending = Call("Connect");
            return Task.CompletedTask;
        });
        await _renderer.RunAsync(() => Call("EndGuest"));
        Assert.True(Read<bool>("_busy"));
        Assert.False(Read<bool>("_safetyBusy"));
        Assert.True(Notice.IsError);
        Assert.Equal(timeout ? "The action outcome is unknown. Check the latest state before trying again." : "The action was rejected (cancelled).", Notice.Message);
        normal.SetResult(new("connect", "accepted", true));
        await pending;
        Assert.False(Read<bool>("_busy"));
        Assert.Contains("Device connection is not confirmed.", Notice.Message);
    }

    [Fact]
    public async Task ConnectionStatePrecedencePreservesUnknownAndRetry()
    {
        foreach (var (device, status) in new (DeviceStateSnapshot?, string)[]
        {
            (null, "Unknown"),
            (Device() with { IsConnected = false }, "Disconnected"),
            (Device() with { IsConnected = false, IsConnecting = true, ConnectionFailed = true }, "Connecting"),
            (Device() with { IsConnected = false, ConnectionFailed = true }, "Failed"),
            (Device() with { IsArmed = false }, "Connected"),
        })
        {
            ComponentTestAccess.Set(Component, "_state", State(device));
            Assert.Equal(status, Read<string>("ConnectionStatus"));
            Assert.Null(Notice.Message);
            foreach (var testId in new[] { "device-name", "device-identifier", "device-duration" })
                Assert.Equal("Unknown", ComponentTestAccess.ElementText(Component, testId));
            var buttons = ComponentTestAccess.Buttons(Component);
            Assert.DoesNotContain("Reset", buttons.Keys);
            Assert.Equal(status is "Connected" or "Connecting", buttons["Connect device"]);
            Assert.Equal(status != "Connected", buttons["Reset Position"]);
        }
        ComponentTestAccess.Set(Component, "_state", State(Device() with { ConnectionFailed = true }));
        Assert.Equal("Connected", Read<string>("ConnectionStatus"));
        ComponentTestAccess.Set(Component, "_connecting", true);
        Assert.Equal("Connecting", Read<string>("ConnectionStatus"));
        ComponentTestAccess.Set(Component, "_connecting", false);
        ComponentTestAccess.Set(Component, "_state", State(Device() with { IsConnected = false }));
        SetupCommand("Connect", Task.FromException<ControlResponse>(new ApiClientException("connection-failed")));
        await _renderer.RunAsync(() => Call("Connect"));
        Assert.False(Read<bool>("_busy"));
        Assert.Equal("The action was rejected (connection-failed).", Notice.Message);
        Assert.True(Notice.IsError);
        Assert.Equal(1, CommandCalls("Connect"));
        Assert.False(ComponentTestAccess.Buttons(Component)["Connect device"]);
        _state = State(Device() with
        {
            IsArmed = false, Name = "Test device", Identifier = "isolated-device",
            ConnectedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TransmittedSettings = new(4, 27, 38, 49, 61),
        });
        SetupCommand("Connect", Task.FromResult(new ControlResponse("connect", "connected", true)));
        await _renderer.RunAsync(() => Call("Connect"));
        Assert.Equal("Connected", Read<string>("ConnectionStatus"));
        Assert.Null(Notice.Message);
        Assert.False(Notice.IsError);
        Assert.Equal("Test device", ComponentTestAccess.ElementText(Component, "device-name"));
        Assert.Equal("isolated-device", ComponentTestAccess.ElementText(Component, "device-identifier"));
        var preview = Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"));
        Assert.True((bool)preview["Animate"]!, "Connectivity, not arming, permits the illustrative preview.");
        Assert.Equal((4, 27.0, 38, 49, 61), ((int?)preview["Pattern"], (double?)preview["Speed"], (int?)preview["Stroke"], (int?)preview["Depth"], (int?)preview["Sensation"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiLossRetainsLastMetadataAndRequestedSelection(bool loaded)
    {
        var last = State(Device() with
        {
            Name = "Test device", Identifier = "isolated-device",
            ConnectedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TransmittedSettings = new(4, 27, 38, 49, 61),
        });
        ComponentTestAccess.Set(Component, "_state", loaded ? last : null);
        ComponentTestAccess.Set(Component, "_loaded", loaded);
        _client.Setup(client => client.GetStateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ApiClientException("connection-unavailable"));
        await _renderer.RunAsync(() => Call("PollAsync"));
        Assert.Equal(loaded ? last : null, Read<UserSessionState?>("_state"));
        Assert.True(Read<bool>("_connectionLost"));
        Assert.True(Read<bool>("_loaded"));
        Assert.True(Notice.IsError);
        Assert.Equal("Unable to connect to the user service. Reload the page.", Notice.Message);
        Assert.DoesNotContain(_client.Invocations, call => call.Method.Name is "SubmitAsync" or "ConnectAsync" or "AllowGuestAsync");
        if (loaded)
        {
            var preview = Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"));
            Assert.False((bool)preview["Animate"]!);
            Assert.Equal(27.0, (double?)preview["Speed"]);
            Assert.Equal("Test device", ComponentTestAccess.ElementText(Component, "device-name"));
            Assert.Equal("isolated-device", ComponentTestAccess.ElementText(Component, "device-identifier"));
        }
    }

    [Fact]
    public async Task StopAndResetRetainRequestedSelectionUntilGenuineChange()
    {
        var original = new DeviceSettingsSnapshot(0, 35, 10, 20, 50);
        _state = State(Device() with { TransmittedSettings = original });
        ComponentTestAccess.Set(Component, "_state", _state);
        var stop = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupCommand("Stop", stop.Task);
        Task stopping = Task.CompletedTask;
        await _renderer.RunAsync(() =>
        {
            stopping = Call("Stop");
            return Task.CompletedTask;
        });
        Assert.True(Read<bool>("_previewStopped"));
        Assert.Equal(original, Read<DeviceSettingsSnapshot>("_stoppedSettings"));
        Assert.Equal("Stopping ...", Notice.Message);
        var initialPreview = Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"));
        Assert.Equal(35.0, (double?)initialPreview["Speed"]);
        Assert.False((bool)initialPreview["Animate"]!);
        await StartPollingAsync();
        _state = State(Device() with { TransmittedSettings = original with { Speed = 36 } });
        await _renderer.ObserveChangeAsync(() => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(36, Read<UserSessionState>("_state").Device!.TransmittedSettings!.Speed);
        Assert.True(Read<bool>("_previewStopped"), "A snapshot during Stop cannot resume the preview.");
        Assert.False((bool)Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"))["Animate"]!);
        stop.SetResult(new("stop", "stopped", true));
        await stopping;
        Assert.Equal(_state.Device!.TransmittedSettings, Read<DeviceSettingsSnapshot>("_stoppedSettings"));
        await _renderer.RunAsync(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        });
        Assert.True(Read<bool>("_previewStopped"));
        _state = State(Device() with { TransmittedSettings = original with { Speed = 40 } });
        await _renderer.ObserveChangeAsync(() => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.False(Read<bool>("_previewStopped"));
        Assert.True((bool)Assert.Single(ComponentTestAccess.Parameters(Component, "PatternPreview"))["Animate"]!);
        var reset = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupCommand("ResetPosition", reset.Task);
        SetupCommand("Stop", Task.FromResult(new ControlResponse("stop", "stopped", true)));
        Task resetting = Task.CompletedTask;
        await _renderer.RunAsync(() =>
        {
            resetting = Call("ResetPosition");
            return Task.CompletedTask;
        });
        Assert.True(Read<bool>("_busy"));
        Assert.True(Read<bool>("_previewStopped"));
        Assert.Equal("Resetting position ...", Notice.Message);
        await _renderer.RunAsync(() => Call("Stop"));
        Assert.Equal(2, CommandCalls("Stop"));
        Assert.True(Read<bool>("_busy"));
        _state = State(Device() with { TransmittedSettings = null });
        await _renderer.ObserveChangeAsync(() => _time.Advance(TimeSpan.FromSeconds(1)));
        Assert.Null(Read<UserSessionState>("_state").Device!.TransmittedSettings);
        reset.SetException(new ApiClientException("cancelled"));
        await resetting;
        Assert.False(Read<bool>("_busy"));
        Assert.True(Read<bool>("_previewStopped"));
        Mocks.Verify<IAdminApiClient>(client => client.ResetPositionAsync(FastArg.Any<CancellationToken>()), TimesSpec.Once);
        Mocks.Verify<IAdminApiClient>(client => client.AllowGuestAsync(FastArg.Any<CancellationToken>()), TimesSpec.NeverCalled);
        Assert.Null(Read<UserSessionState>("_state").Device!.TransmittedSettings);
    }

    private static DeviceStateSnapshot Device() => new(0, 50, 10, 20, 50, true, false, true);

    private static UserSessionState State(DeviceStateSnapshot? device) => new(new("synthetic-invitation"), true, false, null, device);

    private T Read<T>(string name) => ComponentTestAccess.Get<T>(Component, name);

    private Task Call(string name) => ComponentTestAccess.InvokeTask(Component, name);

    private int CommandCalls(string name) => _client.Invocations.Count(call => call.Method.Name == name + "Async");

    private async Task StartPollingAsync()
    {
        await _renderer.RunAsync(() =>
        {
            var poll = Call("PollAsync");
            _polls.Add(poll);
            ComponentTestAccess.Set(Component, "_pollingTask", poll);
            return Task.CompletedTask;
        });
    }

    private void SetupCommand(string name, Task<ControlResponse> task)
    {
        switch (name)
        {
            case "Connect": _client.Setup(client => client.ConnectAsync(It.IsAny<CancellationToken>())).Returns(task); break;
            case "AllowGuest": _client.Setup(client => client.AllowGuestAsync(It.IsAny<CancellationToken>())).Returns(task); break;
            case "Stop": _client.Setup(client => client.StopAsync(It.IsAny<CancellationToken>())).Returns(task); break;
            case "EndGuest": _client.Setup(client => client.EndGuestAsync(It.IsAny<CancellationToken>())).Returns(task); break;
            case "ResetPosition": _client.Setup(client => client.ResetPositionAsync(It.IsAny<CancellationToken>())).Returns(task); break;
            default: throw new ArgumentException("Unknown command.", nameof(name));
        }
    }
}
