namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Renders and manages one guest control session for an invitation.
/// </summary>
public partial class Join
{
    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<Join>? _reference;
    private Task? _pollingTask;
    private GuestSessionState? _status;
    private bool _initialized;
    private bool _joining;
    private bool _joined;
    private bool _everJoined;
    private bool _connectionLost;
    private bool _attached;
    private bool _leaving;
    private bool _disposed;
    private GuestDisplayState _display = new(new(0, 0, 10, 20, 50), null, "Choose settings, then Send.", null);

    [Inject] private GuestControlClient Client { get; set; } = null!;
    [Inject] private IJSRuntime JavaScript { get; set; } = null!;
    [Inject] private TimeProvider Time { get; set; } = null!;
    [Inject] private ILogger<Join> Logger { get; set; } = null!;

    private bool Ready => !_disposed && !_leaving && !_connectionLost && _joined && _status is
    {
        Joined: true,
        ControlAllowed: true,
        Device: { ConnectionFailed: false } device
    } && (device.IsArmed || _status.CanResumeAfterStop);
    private int? DeviceSpeed => _status?.Device is { Speed: { } speed, Stroke: { } stroke } && stroke > 0
        ? (int)Math.Clamp(Math.Round(speed * 100 / stroke, MidpointRounding.AwayFromZero), 0, 100)
        : null;
    private string SessionMessage => _status switch
    {
        { Joined: false } => "Session ended.",
        { ControlAllowed: true } => "Control enabled.",
        { Joined: true } => "Waiting for the user to allow control.",
        _ => "Opening invite..."
    };
    private string SessionMessageVariant => _status switch
    {
        { ControlAllowed: true } => "success",
        { Joined: true } => "warning",
        _ => "danger"
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _connectionLost || (!firstRender && (!_joined || _attached || _module is null)))
            return;
        try
        {
            if (firstRender)
            {
                _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "/" + Assets["guest-session.js"]);
                var invitation = await _module.InvokeAsync<string?>("takeInvitation", _lifetime.Token);
                _initialized = true;
                if (string.IsNullOrWhiteSpace(invitation) || invitation.Length > 128)
                    return;

                _joining = true;
                await Client.JoinAsync(invitation, _lifetime.Token);
                _joined = true;
                _everJoined = true;
                _status = await Client.GetStateAsync(_lifetime.Token);
                _pollingTask = PollAsync();
            }
            else if (_joined && !_attached && _module is not null)
            {
                _attached = true;
                _reference = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("attach", _lifetime.Token, _root, _reference);
                await PushStateAsync();
            }
        }
        catch (ControlClientException error) when (error.Code.Contains("invitation", StringComparison.Ordinal))
        {
            _joined = false;
        }
        catch (Exception exception) when (exception is ControlClientException or ControlClientUnknownOutcomeException or JSException)
        {
            LogGuestControlInitializationFailed(Logger);
            _connectionLost = true;
            _joined = false;
            await BrowserCallAsync("disconnect");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _joining = false;
            if (!_disposed)
                StateHasChanged();
        }
    }

    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), Time);
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                var state = await Client.GetStateAsync(_lifetime.Token);
                if (!state.Joined)
                {
                    _status = state;
                    _joined = false;
                    await InvokeAsync(async () =>
                    {
                        await PushStateAsync();
                        StateHasChanged();
                    });
                    return;
                }
                _status = state;
                await InvokeAsync(async () =>
                {
                    await PushStateAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ControlClientException or ControlClientUnknownOutcomeException)
        {
            await InvokeAsync(async () =>
            {
                _connectionLost = true;
                _joined = false;
                await BrowserCallAsync("disconnect");
                StateHasChanged();
            });
        }
    }

    private async Task LeaveAsync()
    {
        if (_leaving || _disposed || !_joined)
            return;
        _leaving = true;
        await BrowserCallAsync("disarm");
        try
        {
            await Client.LeaveAsync(_lifetime.Token);
        }
        catch (ControlClientUnknownOutcomeException) { }
        catch (ControlClientException error)
        {
            LogGuestLeaveRejected(Logger, error.Code);
        }
        _joined = false;
        _status = null;
    }

    private Task PushStateAsync() => !_attached ? Task.CompletedTask : BrowserCallAsync("updateState", new
    {
        joined = _joined,
        ready = Ready,
        current = _status?.Device is { } device ? new { speed = DeviceSpeed, stroke = device.Stroke, depth = device.Depth } : null
    });

    private async Task BrowserCallAsync(string methodIdentifier, params object?[] arguments)
    {
        if (_module is null || _disposed)
            return;
        try { await _module.InvokeVoidAsync(methodIdentifier, arguments); }
        catch (JSDisconnectedException) { }
        catch (JSException) { _connectionLost = true; }
    }

}
