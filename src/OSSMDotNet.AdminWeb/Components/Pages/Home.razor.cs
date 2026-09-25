namespace OSSMDotNet.AdminWeb.Components.Pages;

/// <summary>
/// Renders the loopback-only operator control page.
/// </summary>
public partial class Home
{
    private const string PendingOperationStatusText = "Waiting for the device to settle.";

    [Inject] private OperatorControlClient Control { get; set; } = null!;
    [Inject] private IJSRuntime Js { get; set; } = null!;
    [Inject] private TimeProvider Time { get; set; } = null!;
    [Inject] private ILogger<Home> Logger { get; set; } = null!;
    private OperatorNoticeState _notice = null!;
    private bool _loaded;
    private ControlSnapshot? _status;
    private AuthorityPrecondition? _precondition;
    private Invitation? _invitation;
    private int _speedCap = 100;
    private int _maximumDepth = 100;
    private bool _busy;
    private IJSObjectReference? _module;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SnapshotRevisionTracker _statusRevisions = new();
    private readonly List<ControlActivityEntry> _logs = [];
    private long _lastLogSequence;
    private ElementReference _logElement;
    private bool _logAttached;
    private bool _logScrollFailed;

    protected override void OnInitialized() => _notice = new(Time);

    private bool CanReset => _status is { BridgeReady: true, DeviceEnabled: false, DeviceMoving: false, OperationPending: false };
    private bool CanAllow => CanReset && _status is
    {
        Mode: "Initializing" or "Paused", SessionMode: "PendingApproval" or "Approved",
        Limits.SpeedCap: > 0, Limits.MaximumDepth: > 0
    };
    private string SessionLabel => (_status?.SessionMode, _status?.Mode) switch
    {
        ("PendingApproval", _) => "Guest waiting",
        ("Approved", "Ready") => "Guest connected",
        ("Approved", _) => "Controls disabled",
        _ => "No guest"
    };
    private bool GuestWaiting => _status?.SessionMode == "PendingApproval";
    private bool GuestApprovalActionAvailable => GuestWaiting && CanAllow;
    private bool OperationPending => _status?.OperationPending == true;
    private string SessionVariant => (_status?.SessionMode, _status?.Mode) switch
    {
        ("PendingApproval", _) => "warning",
        ("Approved", "Ready") => "success",
        ("Approved", _) => "warning",
        _ => "neutral"
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_module is null || _logScrollFailed)
                return;
            try
            {
                if (_status is not null && !_logAttached)
                {
                    await _module.InvokeVoidAsync("attachLog", _logElement);
                    _logAttached = true;
                }
                else if (_status is null && _logAttached)
                {
                    await _module.InvokeVoidAsync("detachLog");
                    _logAttached = false;
                }
            }
            catch (JSDisconnectedException) { }
            catch (JSException)
            {
                _logScrollFailed = true;
                LogActivityLogScrollingUnavailable(Logger);
            }
            return;
        }

        try
        {
            _module = await Js.InvokeAsync<IJSObjectReference>("import", "./admin.js");
            await Control.SubscribeAsync(new(ReceiveStateAsync, ReceiveActivityAsync, ReceiveResyncAsync),
                _lastLogSequence, _lifetime.Token);
        }
        catch (ControlClientException)
        {
            LogControlServiceConnectionFailed(Logger);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        _loaded = true;
        StateHasChanged();
    }

    private async Task ApplyLimits()
    {
        var expected = RequirePrecondition();
        if (expected is null)
            return;
        await RunAsync(token => Control.SetLimitsAsync(new(_speedCap, _maximumDepth), expected, token), "Limits applied.");
    }

    private async Task AllowControl()
    {
        var expected = RequirePrecondition();
        if (expected is null)
            return;
        await RunAsync(token => Control.AllowControlAsync(expected, token));
    }

    private async Task Unlock()
    {
        var expected = RequirePrecondition();
        if (expected is null || _status is null)
            return;
        await RunAsync(token => Control.UnlockAsync(new(_status.LockRevision), expected, token),
            "Unlocked. Allow control separately when ready.");
    }

    private async Task Reset()
    {
        var expected = RequirePrecondition();
        if (expected is null)
            return;
        await RunAsync(token => Control.RecoverAsync(expected, token));
    }

    private async Task End()
    {
        var expected = RequirePrecondition();
        if (expected is null)
            return;
        await RunAsync(token => Control.EndAsync(expected, token), "Session ended. Invitation link replaced.");
    }

    private async Task Cycle()
    {
        var expected = RequirePrecondition();
        if (expected is null)
            return;
        await RunAsync(token => Control.CycleAsync(expected, token),
            "Invitation link replaced. The previous guest's access was revoked and Stop was requested. Allow control separately when ready.");
    }

    private async Task Copy()
    {
        if (_module is null || _invitation is null)
            return;
        _notice.CopyCompleted(await _module.InvokeAsync<bool>("copy", _invitation.Url));
        if (_notice.IsCopied)
            _ = RefreshCopyAfterExpiryAsync();
    }

    private async Task RefreshCopyAfterExpiryAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Time, _lifetime.Token);
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task RunAsync(Func<CancellationToken, Task<ControlAcknowledgement>> action, string? message = null)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            await action(_lifetime.Token);
            _notice.Show(message);
        }
        catch (ControlClientUnknownOutcomeException)
        {
            _notice.Show("The command outcome is unknown. Wait for the next control-service update before trying again.", true);
        }
        catch (ControlClientException error)
        {
            LogOperatorActionRejected(Logger);
            _notice.Show(error.Code switch
            {
                "authority-revision-mismatch" or "guest-admission-mismatch" => "The control state changed. Wait for the latest update, then try again.",
                "invalid-limits" => "Use a speed cap of 0-100 and a depth of 1-100.",
                "operation-pending" or "device-not-stopped" => "Wait for the device to stop, then try again.",
                "limits-not-configured" => "Control is disabled by the current limits.",
                "limits-conflict" => "The current or pending settings exceed these limits. Lower the guest settings or stop the session first.",
                "external-lock" => "Unlock the controller before continuing.",
                _ => "That action is unavailable. Check the connection and session state."
            }, true);
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private AuthorityPrecondition? RequirePrecondition()
    {
        if (_precondition is not null)
            return _precondition;
        _notice.Show("The current control state is unavailable. Wait for the connection to recover.", true);
        return null;
    }

    private Task ReceiveStateAsync(OperatorLiveControlStatus status) => InvokeAsync(() =>
    {
        if (!_statusRevisions.TryAccept(status.Authority.SnapshotRevision))
            return;

        var next = status.Authority.Snapshot;
        if (_status is null || _speedCap == _status.Limits.SpeedCap)
            _speedCap = next.Limits.SpeedCap;
        if (_status is null || _maximumDepth == _status.Limits.MaximumDepth)
            _maximumDepth = next.Limits.MaximumDepth;
        _status = next;
        _precondition = status.Authority.Precondition;
        _invitation = status.Invitation;
        StateHasChanged();
    });

    private Task ReceiveActivityAsync(ControlActivityBatch batch) => InvokeAsync(() =>
    {
        if (batch.Gap)
            _logs.Clear();
        _logs.AddRange(batch.Entries);
        if (_logs.Count > 200)
            _logs.RemoveRange(0, _logs.Count - 200);
        _lastLogSequence = Math.Max(_lastLogSequence, batch.LastSequence);
        StateHasChanged();
    });

    private Task ReceiveResyncAsync(ControlResyncRequired resync) => InvokeAsync(() =>
    {
        _status = null;
        _precondition = null;
        _notice.Show("The control-service connection closed. Reload the operator page.", true);
        StateHasChanged();
    });

    private static string LogClass(string level) => level switch
    {
        "Error" or "Critical" => "error",
        "Warning" => "warning",
        _ => ""
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic activity-log scrolling is unavailable.")]
    private static partial void LogActivityLogScrollingUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The operator page could not connect to the control service.")]
    private static partial void LogControlServiceConnectionFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operator action was not accepted.")]
    private static partial void LogOperatorActionRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Activity log cleanup did not complete.")]
    private static partial void LogActivityLogCleanupIncomplete(ILogger logger);

    /// <summary>
    /// Releases the control connection and interactive resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lifetime.CancelAsync();
            await Control.DisposeAsync();
            if (_module is not null)
            {
                try
                {
                    await _module.InvokeVoidAsync("detachLog");
                    await _module.DisposeAsync();
                }
                catch (JSDisconnectedException) { }
                catch (JSException)
                {
                    LogActivityLogCleanupIncomplete(Logger);
                }
            }
        }
        finally
        {
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
