using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace OssmSharp.Web.GuestWeb.Components.Pages;

/// <summary>
/// Renders one controller attached to an invitation-bound saved browser session.
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
    private string _sessionCode = "no-session";
    private GuestDisplayState _display = new(new(0, 0, 10, 20, 50), null, string.Empty, null, PreviewStopped: true);

    [Inject]
    private IGuestApiClient Client { get; set; } = null!;
    [Inject]
    private IJSRuntime JavaScript { get; set; } = null!;
    [Inject]
    private TimeProvider Time { get; set; } = null!;
    [Inject]
    private ILogger<Join> Logger { get; set; } = null!;
    [Inject]
    private AuthenticationStateProvider Authentication { get; set; } = null!;

    private bool Ready => !_disposed && !_leaving && !_connectionLost && _joined && _status is
    {
        Joined: true,
        ControlAllowed: true,
        Device: { IsConnected: true, IsConnecting: false, ConnectionFailed: false } device
    } && (device.IsArmed || _status.CanResumeAfterStop);
    private double? DeviceSpeed => _status?.Device?.Speed;
    private string SessionMessage => _status switch
    {
        { Joined: false } => "Session stopped.",
        { Joined: true } => "Connected. Waiting for the other person to allow control.",
        _ => "Loading ...",
    };
    private string SessionMessageVariant => _status switch
    {
        { Joined: true } => "warning",
        _ => "danger",
    };
    private string EndedMessage => _sessionCode switch
    {
        "invalid-invitation" or "guest-already-joined" => "This invitation is invalid or has already been used. Ask for a new invitation.",
        "cookie-unavailable" => "The invitation was used, but this browser could not save the session. Allow cookies and ask for a new invitation.",
        "redemption-unknown" => "Joining could not be confirmed. Ask for a new invitation if refreshing does not restore the session.",
        "invalid-response" => "The guest page received an unusable response. Ask the other person to check OssmSharp.",
        "initialization-failed" => "The guest page could not start. Refresh the page to try again.",
        "session-read-failed" => "The guest session could not be checked. Ask the other person to check OssmSharp, then refresh this page.",
        "session-expired" => "This session expired after 24 hours. Ask for a new invitation.",
        "leave-failed" => "Leaving could not be confirmed. Ask the other person to stop the device and end the session.",
        "guest-left" => "You left the session. Ask for a new invitation to rejoin.",
        "session-in-use" => "This session is open in another tab. Waiting for that tab to close.",
        _ => _everJoined ? "Session ended. Ask for a new invitation to rejoin."
            : "Open a new invitation to join. A saved session may have ended, expired, or been replaced.",
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
                _sessionCode = await _module.InvokeAsync<string>("getSessionCode", _lifetime.Token);
                _initialized = true;
                if (_sessionCode is "connection-unavailable" or "session-in-use" or "stop-pending")
                {
                    _connectionLost = true;
                    await BrowserCallAsync("disconnect", _sessionCode);
                    return;
                }
                if (_sessionCode != "ready")
                    return;
                var authentication = await Authentication.GetAuthenticationStateAsync();
                _lifetime.Token.ThrowIfCancellationRequested();
                var credential = authentication.User.FindFirstValue(GuestSessionEndpoints.CredentialClaim);
                if (credential is null)
                    return;

                _joining = true;
                await Client.ResumeAsync(credential, _lifetime.Token);
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
        catch (ApiClientException error) when (error.Code == "invalid-guest-session")
        {
            _joined = false;
            _sessionCode = error.Code;
            await Client.DisposeAsync();
            await BrowserCallAsync("endSession", error.Code);
        }
        catch (ApiClientException error) when (error.Code == "guest-already-joined")
        {
            _joined = false;
            _connectionLost = true;
            _sessionCode = "session-in-use";
            await Client.DisposeAsync();
            await BrowserCallAsync("disconnect", _sessionCode);
        }
        catch (Exception exception) when (exception is RpcClientRequestTimeoutException
            or ApiClientException { Code: "connection-unavailable" or "device-not-ready" })
        {
            Logger.InitializationFailed();
            _connectionLost = true;
            _joined = false;
            DisarmDisplay();
            await Client.DisposeAsync();
            await BrowserCallAsync("disconnect");
        }
        catch (Exception exception) when (exception is ApiClientException or JSException or InvalidDataException)
        {
            Logger.SessionReadFailed("initialization");
            _initialized = true;
            _joined = false;
            _sessionCode = "initialization-failed";
            DisarmDisplay();
            await Client.DisposeAsync();
            await BrowserCallAsync("endSession", _sessionCode);
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
                _lifetime.Token.ThrowIfCancellationRequested();
                if (!state.Joined)
                {
                    _joined = false;
                    _sessionCode = _status?.SessionExpiresAt is { } expiry && Time.GetUtcNow() >= expiry
                        ? "session-expired" : "session-ended";
                    _status = state;
                    DisarmDisplay();
                    await Client.DisposeAsync();
                    await InvokeAsync(async () =>
                    {
                        await PushStateAsync();
                        await BrowserCallAsync("endSession", _sessionCode);
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
        catch (Exception exception) when (exception is RpcClientRequestTimeoutException
            or ApiClientException { Code: "connection-unavailable" })
        {
            Logger.ApiConnectionLost();
            await InvokeAsync(async () =>
            {
                _connectionLost = true;
                _joined = false;
                DisarmDisplay();
                await Client.DisposeAsync();
                await BrowserCallAsync("disconnect");
                StateHasChanged();
            });
        }
        catch (Exception exception) when (exception is ApiClientException or InvalidDataException)
        {
            var code = exception is ApiClientException { Code: "invalid-guest-session" }
                ? "invalid-guest-session" : "session-read-failed";
            if (code == "session-read-failed")
                Logger.SessionReadFailed("poll");
            await InvokeAsync(async () =>
            {
                _joined = false;
                _sessionCode = code;
                DisarmDisplay();
                await Client.DisposeAsync();
                await BrowserCallAsync("endSession", code);
                StateHasChanged();
            });
        }
    }

    private async Task LeaveAsync()
    {
        if (_leaving || _disposed || !_joined || _module is null)
            return;
        _leaving = true;
        await PushStateAsync();
        BrowserReply reply;
        try
        {
            var result = await Client.LeaveAsync(_lifetime.Token);
            reply = new(result.Accepted, result.Code);
        }
        catch (Exception error) when (error is ApiClientException or RpcClientRequestTimeoutException)
        {
            reply = new(false, error is ApiClientException failure ? failure.Code : "outcome-unknown");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        try
        {
            reply = await _module.InvokeAsync<BrowserReply>("completeLeave", _lifetime.Token, reply);
            _sessionCode = reply.Ok ? "guest-left" : "leave-failed";
            if (!reply.Ok)
                Logger.LeaveRejected(reply.Code);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSException)
        {
            _sessionCode = "leave-failed";
            Logger.LeaveRejected("browser-unavailable");
        }
        await _lifetime.CancelAsync();
        await Client.DisposeAsync();
        _joined = false;
        _status = null;
        DisarmDisplay();
    }

    private Task PushStateAsync() => !_attached ? Task.CompletedTask : BrowserCallAsync("updateState", new
    {
        joined = _joined,
        ready = Ready,
        resettingPosition = _status?.Device?.IsResettingPosition == true,
        stopped = _status?.CanResumeAfterStop == true,
        current = _status?.Device is { } device ? new { speed = DeviceSpeed, stroke = device.Stroke, depth = device.Depth, sensation = device.Sensation } : null,
    });

    private async Task BrowserCallAsync(string methodIdentifier, params object?[] arguments)
    {
        if (_module is null || _disposed)
            return;
        try { await _module.InvokeVoidAsync(methodIdentifier, _lifetime.Token, arguments); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) when (_disposed || _lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is JSException or OperationCanceledException)
        {
            Logger.BrowserCallFailed(methodIdentifier, exception);
            _connectionLost = true;
            _joined = false;
            _status = null;
            DisarmDisplay();
            await _lifetime.CancelAsync();
            await Client.DisposeAsync();
        }
    }

    private void DisarmDisplay() => _display = _display with
    {
        Selected = _display.Selected with { Speed = 0 },
        Sent = null,
        Status = string.Empty,
        Error = null,
        PreviewStopped = true,
        CanSend = false,
    };

}
