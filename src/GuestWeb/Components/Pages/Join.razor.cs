using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OSSMDotNet.Web.GuestWeb.Services;
using OSSMDotNet.Web.SessionContracts;

namespace OSSMDotNet.Web.GuestWeb.Components.Pages;

public partial class Join
{
    [Inject] private GuestSessionClient Client { get; set; } = null!;
    [Inject] private IJSRuntime JavaScript { get; set; } = null!;
    [Inject] private ILogger<Join> Logger { get; set; } = null!;
    [Parameter] public string SessionId { get; set; } = "";

    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<Join>? _reference;
    private Task? _pollTask;
    private string? _boundSessionId;
    private GuestStatus? _status;
    private long _lastRevision;
    private bool _initialized;
    private bool _joining;
    private bool _everJoined;
    private bool _attached;
    private bool _leaving;
    private bool _disposed;

    private bool Ready => !_disposed && !_leaving && Client.IsJoined &&
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

    protected override async Task OnParametersSetAsync()
    {
        if (_boundSessionId is not null && _boundSessionId != SessionId)
            await EndUiSessionAsync();
        _boundSessionId = SessionId;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed) return;
        try
        {
            if (firstRender)
            {
                _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "/guest-session.js");
                var invitation = await _module.InvokeAsync<string?>("takeInvitation", _lifetime.Token);
                _initialized = true;
                if (!GuestProtocol.ValidText(SessionId, 64) || !GuestProtocol.ValidText(invitation, 128))
                {
                    StateHasChanged();
                    return;
                }
                _joining = true;
                var reply = await Client.JoinAsync(SessionId, invitation!, _lifetime.Token);
                _joining = false;
                _everJoined = Client.IsJoined;
                await ApplyReplyAsync(reply);
                if (Client.IsJoined) _pollTask = PollStatusAsync();
            }
            else if (Client.IsJoined && !_attached && _module is not null)
            {
                _attached = true;
                _reference = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("attach", _lifetime.Token, _root, _reference);
                await PushAuthorityAsync();
            }
        }
        catch (Exception error) when (error is JSException or OperationCanceledException)
        {
            Logger.LogWarning("Guest controls could not initialize; closing the connection.");
            await EndUiSessionAsync();
            if (!_disposed) StateHasChanged();
        }
    }

    private async Task PollStatusAsync()
    {
        try
        {
            while (!_disposed && !_leaving && Client.IsJoined)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), _lifetime.Token);
                var reply = await Client.StatusAsync(_lifetime.Token);
                await InvokeAsync(() => ApplyReplyAsync(reply));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            Logger.LogWarning("Guest status rendering failed; closing the connection.");
            await EndUiSessionAsync();
        }
    }

    [JSInvokable]
    public async Task<BrowserReply> SubmitRun(GuestInput input)
    {
        if (!Ready || input is not { V: 2, Run: true, Desired: { } desired } ||
            input.Epoch != _status!.Grant!.Epoch || input.PolicyVersion != _status.PolicyVersion)
            return new(false, "not-ready");
        if (desired.Pattern != 0 || desired.Sensation != 50 || desired.Speed < 0 || desired.Speed > SpeedCap ||
            desired.Stroke < 1 || desired.Stroke > desired.Depth || desired.Depth < 1 || desired.Depth > MaximumDepth)
            return new(false, "invalid-input");
        var reply = await Client.SubmitInputAsync(input, _lifetime.Token);
        await ApplyReplyAsync(reply);
        return new(reply.Ok, reply.Code);
    }

    [JSInvokable]
    public async Task<BrowserReply> StopRun()
    {
        if (_disposed || !Client.IsJoined) return new(false, "not-joined");
        var reply = await Client.StopAsync(_lifetime.Token);
        await ApplyReplyAsync(reply);
        return new(reply.Ok, reply.Code);
    }

    private async Task LeaveAsync()
    {
        if (_leaving || _disposed || !Client.IsJoined) return;
        _leaving = true;
        await BrowserCallAsync("disarm");
        await Client.LeaveAsync(_lifetime.Token);
        await EndUiSessionAsync();
    }

    private async Task ApplyReplyAsync(GuestResult reply)
    {
        if (_disposed) return;
        if (reply.Revision > _lastRevision)
        {
            _lastRevision = reply.Revision;
            if (reply.State is not null) _status = reply.State;
        }
        if (Client.IsExpired) _status = null;
        await PushAuthorityAsync();
        StateHasChanged();
    }

    private Task PushAuthorityAsync() => BrowserCallAsync("updateAuthority", new
    {
        joined = Client.IsJoined && !_disposed,
        ready = Ready,
        fresh = _status?.BridgeReady == true,
        grant = Client.IsExpired ? null : _status?.Grant,
        policyVersion = _status?.PolicyVersion ?? 0,
        ackSeq = _status?.AckSeq ?? 0,
        current = _status?.Current,
        verifiedSeq = _status?.VerifiedSeq ?? 0,
        limits = new GuestLimits(SpeedCap, MaximumDepth)
    });

    private async Task BrowserCallAsync(string method, params object?[] arguments)
    {
        if (_module is null || _disposed) return;
        try { await _module.InvokeVoidAsync(method, arguments); }
        catch (Exception error) when (error is JSException or OperationCanceledException)
        {
            Logger.LogWarning("Guest browser connection failed; closing the control session.");
            await EndUiSessionAsync();
        }
    }

    private async Task EndUiSessionAsync()
    {
        _initialized = true;
        _joining = false;
        _status = null;
        await _lifetime.CancelAsync();
        await Client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        await Client.DisposeAsync();
        try
        {
            if (_pollTask is not null) await _pollTask;
            if (_module is not null) await _module.InvokeVoidAsync("dispose");
        }
        catch (JSDisconnectedException) { }
        catch (Exception error) when (error is JSException or OperationCanceledException)
        {
            Logger.LogWarning("Guest browser cleanup did not complete; its control connection is closed.");
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
        }
    }
}
