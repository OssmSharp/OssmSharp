namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Renders and manages one guest control session for an invitation.
/// </summary>
public partial class Join
{
    [Inject] private GuestControlClient Client { get; set; } = null!;
    [Inject] private IJSRuntime JavaScript { get; set; } = null!;
    [Inject] private ILogger<Join> Logger { get; set; } = null!;
    /// <summary>
    /// Gets or sets the session identifier from the invitation route.
    /// </summary>
    [Parameter] public string SessionId { get; set; } = "";

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SnapshotRevisionTracker _statusRevisions = new();
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<Join>? _reference;
    private string? _boundSessionId;
    private GuestStatus? _status;
    private bool _initialized;
    private bool _joining;
    private bool _joined;
    private bool _everJoined;
    private bool _connectionLost;
    private bool _attached;
    private bool _leaving;
    private bool _disposed;
    private GuestDisplayState _display = new(new(0, 0, 10, 20, 50), null, false, "Choose settings, then Send.", null);

    private bool Ready => !_disposed && !_leaving && _joined &&
        _status is { BridgeReady: true, Grant.ValidForMs: > 0, Mode: "Ready" };
    private int SpeedCap => Math.Clamp(_status?.Limits.SpeedCap ?? 0, 0, 100);
    private int MaximumDepth => Math.Clamp(_status?.Limits.MaximumDepth ?? 1, 1, 100);
    private string SessionMessage => _status switch
    {
        { Mode: "Locked" } => "Locked by the operator. Sending is disabled.",
        { BridgeReady: false } => "Device feedback lost. Sending is disabled.",
        { SessionMode: "PendingApproval" } => "Waiting for the operator.",
        { Mode: "Ready" } => "Control enabled.",
        _ => "Paused. Waiting for the operator."
    };
    private string SessionMessageVariant => _status switch
    {
        { Mode: "Locked" } or { BridgeReady: false } => "danger",
        { SessionMode: "PendingApproval" } => "warning",
        { Mode: "Ready" } => "success",
        _ => "warning"
    };

    protected override async Task OnParametersSetAsync()
    {
        if (_boundSessionId is not null && _boundSessionId != SessionId)
            await EndUiSessionAsync();
        _boundSessionId = SessionId;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || (!firstRender && (!_joined || _attached || _module is null)))
            return;
        try
        {
            if (firstRender)
            {
                _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "/" + Assets["guest-session.js"]);
                var invitation = await _module.InvokeAsync<string?>("takeInvitation", _lifetime.Token);
                _initialized = true;
                if (!ControlProtocol.ValidText(SessionId, 64) || !ControlProtocol.ValidText(invitation, 128))
                    return;

                _joining = true;
                await Client.JoinAsync(new(ReceiveStatusAsync, ReceiveResyncAsync),
                    new(NewCorrelation(), SessionId, invitation!), _lifetime.Token);
                _joining = false;
                _joined = true;
                _everJoined = true;
            }
            else if (_joined && !_attached && _module is not null)
            {
                _attached = true;
                _reference = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("attach", _lifetime.Token, _root, _reference);
                await PushAuthorityAsync();
            }
        }
        catch (ControlClientException error) when (error.Code is "invalid-invitation" or "invitation-invalid" or "session-conflict")
        {
            await EndUiSessionAsync();
        }
        catch (ControlClientException)
        {
            await HandleConnectionLossAsync();
        }
        catch (ControlClientUnknownOutcomeException)
        {
            await HandleConnectionLossAsync();
        }
        catch (Exception exception) when (exception is JSException or OperationCanceledException)
        {
            LogGuestControlInitializationFailed(Logger);
            await HandleConnectionLossAsync();
        }
        finally
        {
            _joining = false;
            if (!_disposed)
                StateHasChanged();
        }
    }

    /// <summary>
    /// Accepts the latest browser-side display state without granting motion authority.
    /// </summary>
    /// <param name="display">The browser's current selection and bounded feedback text.</param>
    /// <exception cref="ArgumentException">The display has missing values or feedback longer than 200 characters.</exception>
    [JSInvokable]
    public Task UpdateDisplay(GuestDisplayState display)
    {
        if (_disposed)
            return Task.CompletedTask;
        if (display.Selected is null || display.Status is not { Length: <= 200 } || display.Error?.Length > 200)
            throw new ArgumentException("Invalid guest display state.", nameof(display));
        _display = display;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates and submits a guest motion request from the untrusted browser.
    /// </summary>
    /// <param name="input">The versioned request with current grant, policy, and bounded settings.</param>
    /// <returns>The accepted reply code, or a rejection or unknown-outcome code.</returns>
    [JSInvokable]
    public async Task<BrowserReply> SubmitRun(GuestInput input)
    {
        if (!Ready || input is not { V: 2, Run: true, Desired: { } desired } ||
            input.Epoch != _status!.Grant!.Epoch)
        {
            return new(false, "not-ready");
        }
        if (input.PolicyVersion != _status.PolicyVersion)
        {
            return new(false, "policy-invalid");
        }
        if (desired.Pattern != 0 || desired.Sensation != 50 || desired.Speed < 0 || desired.Speed > SpeedCap ||
            desired.Stroke < 1 || desired.Stroke > desired.Depth || desired.Depth < 1 || desired.Depth > MaximumDepth)
        {
            return new(false, "invalid-input");
        }

        try
        {
            var reply = await Client.SubmitAsync(new(NewCorrelation(), input), _lifetime.Token);
            return new(true, reply.Code);
        }
        catch (ControlClientUnknownOutcomeException)
        {
            return new(false, "outcome-unknown");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return new(false, "outcome-unknown");
        }
        catch (ControlClientException error)
        {
            return new(false, error.Code);
        }
    }

    /// <summary>
    /// Stops guest motion without treating command acceptance as device verification.
    /// </summary>
    /// <returns>The accepted reply code, or a rejection or unknown-outcome code.</returns>
    [JSInvokable]
    public async Task<BrowserReply> StopRun()
    {
        if (_disposed || !_joined)
            return new(false, "not-joined");
        try
        {
            var reply = await Client.StopAsync(_lifetime.Token);
            return new(true, reply.Code);
        }
        catch (ControlClientUnknownOutcomeException)
        {
            return new(false, "outcome-unknown");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return new(false, "outcome-unknown");
        }
        catch (ControlClientException error)
        {
            return new(false, error.Code);
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
        await EndUiSessionAsync();
    }

    private Task ReceiveStatusAsync(GuestStatus status) => InvokeAsync(async () =>
    {
        if (_disposed || !_statusRevisions.TryAccept(status.SnapshotRevision))
            return;

        _status = status;
        await PushAuthorityAsync();
        StateHasChanged();
    });

    private Task ReceiveResyncAsync(ControlResyncRequired resync) =>
        InvokeAsync(resync.Code == "delivery-resync-required" ? HandleEndedSessionAsync : HandleConnectionLossAsync);

    private async Task HandleEndedSessionAsync()
    {
        if (_disposed)
            return;
        _status = null;
        _joined = false;
        _leaving = true;
        await BrowserCallAsync("disarm");
        await EndUiSessionAsync();
        StateHasChanged();
    }

    private async Task HandleConnectionLossAsync()
    {
        if (_disposed)
            return;
        _status = null;
        _joined = false;
        _leaving = true;
        _connectionLost = true;
        await BrowserCallAsync("disconnect");
        StateHasChanged();
    }

    private Task PushAuthorityAsync() => !_attached ? Task.CompletedTask : BrowserCallAsync("updateAuthority", new
    {
        joined = _joined && !_disposed,
        ready = Ready,
        fresh = _status?.BridgeReady == true,
        grant = !_disposed && _joined ? _status?.Grant : null,
        policyVersion = _status?.PolicyVersion ?? 0,
        ackSeq = _status?.AckSeq ?? 0,
        current = _status?.Current,
        verifiedSeq = _status?.VerifiedSeq ?? 0,
        limits = new GuestLimits(SpeedCap, MaximumDepth)
    });

    private async Task BrowserCallAsync(string methodIdentifier, params object?[] arguments)
    {
        if (_module is null || _disposed)
            return;
        try { await _module.InvokeVoidAsync(methodIdentifier, arguments); }
        catch (Exception exception) when (exception is JSException or OperationCanceledException)
        {
            LogGuestBrowserInteropFailed(Logger);
            await EndUiSessionAsync();
        }
    }

    private async Task EndUiSessionAsync()
    {
        _initialized = true;
        _joining = false;
        _joined = false;
        _status = null;
        await _lifetime.CancelAsync();
        await Client.DisposeAsync();
    }

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guest control initialization failed; closing the connection.")]
    private static partial void LogGuestControlInitializationFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guest leave was not accepted: {Code}.")]
    private static partial void LogGuestLeaveRejected(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guest browser interop failed; closing the control session.")]
    private static partial void LogGuestBrowserInteropFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guest browser cleanup did not complete; its control connection is closed.")]
    private static partial void LogGuestBrowserCleanupIncomplete(ILogger logger);

    /// <summary>
    /// Releases the guest session and browser interop resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _lifetime.CancelAsync();
        await Client.DisposeAsync();
        try
        {
            if (_module is not null)
                await _module.InvokeVoidAsync("dispose");
        }
        catch (JSDisconnectedException) { }
        catch (Exception exception) when (exception is JSException or OperationCanceledException)
        {
            LogGuestBrowserCleanupIncomplete(Logger);
        }
        finally
        {
            if (_module is not null)
            {
                try { await _module.DisposeAsync(); }
                catch (JSDisconnectedException) { }
            }
            _reference?.Dispose();
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
