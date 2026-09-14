using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OSSMDotNet.Web.ControlCore;

namespace OSSMDotNet.Web.AdminService.Components.Pages;

public partial class Home
{
    [Inject] private AdminOperations Admin { get; set; } = null!;
    [Inject] private IJSRuntime Js { get; set; } = null!;
    [Inject] private TimeProvider Time { get; set; } = null!;
    [Inject] private ILogger<Home> Logger { get; set; } = null!;
    private string _token = "";
    private string? _message;
    private bool _error;
    private bool _loaded;
    private ControlSnapshot? _status;
    private Invitation? _invitation;
    private Guid _attempt = Guid.NewGuid();
    private int _cap = 20;
    private int _depth = 60;
    private bool _busy;
    private IJSObjectReference? _module;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _poll;
    private readonly List<OperatorLogEntry> _logs = [];
    private long _lastLogSequence;
    private ElementReference _logElement;
    private bool _logAttached;
    private bool _logScrollFailed;

    private bool CanReset => _status is { BridgeReady: true, DeviceMoving: false, OperationPending: false };
    private bool CanAllow => CanReset && _status is
    {
        Mode: "Paused", SessionMode: "PendingApproval" or "Approved",
        Limits.SpeedCap: > 0, Limits.MaximumDepth: > 0
    };
    private string ModeLabel => _status?.Mode switch
    {
        "Ready" => "Control enabled",
        "Locked" => "Controls locked",
        "Faulted" => "Connection needs attention",
        "Initializing" => "Apply limits to prepare",
        _ => "Paused"
    };
    private string SessionLabel => _status?.SessionMode switch
    {
        "Invited" => "Invite ready",
        "PendingApproval" => "Guest waiting",
        "Approved" => "Guest connected",
        _ => "No guest"
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_module is null || _logScrollFailed) return;
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
                Logger.LogWarning("Automatic activity scrolling is unavailable.");
            }
            return;
        }
        try
        {
            _module = await Js.InvokeAsync<IJSObjectReference>("import", "./admin.js");
            _token = await _module.InvokeAsync<string>("takeOperatorToken");
            _status = Admin.Read(_token);
            RefreshLog();
            if (_status.Limits.SpeedCap > 0)
            {
                _cap = _status.Limits.SpeedCap;
                _depth = _status.Limits.MaximumDepth;
            }
            _poll = PollAsync();
        }
        catch (ControlRejectedException) { _status = null; }
        catch (JSException)
        {
            Logger.LogWarning("Operator browser initialization failed.");
            _status = null;
        }
        _loaded = true;
        StateHasChanged();
    }

    private void Run(Action action, string? message = null)
    {
        try
        {
            action();
            _status = Admin.Read(_token);
            _message = message;
            _error = false;
        }
        catch (ControlRejectedException error)
        {
            Logger.LogWarning(new EventId(200, "OperatorActivity"), "Operator action was not accepted.");
            _error = true;
            _message = error.Code switch
            {
                "invalid-limits" => "Use a speed cap of 0-100 and a depth of 1-100.",
                "operation-pending" or "device-not-stopped" => "Wait for the device to stop, then try again.",
                "limits-not-configured" => "Apply nonzero session limits first.",
                "external-lock" => "Unlock the controller before continuing.",
                _ => "That action is unavailable. Check the connection and session state."
            };
        }
    }

    private void ApplyLimits() => Run(() =>
    {
        if (_status?.Mode == "Initializing") Admin.Recover(_token);
        Admin.Limits(_token, _cap, _depth);
    }, "Limits applied. Control is paused.");

    private void AllowControl() => Run(() =>
    {
        if (_status?.SessionMode == "PendingApproval")
            Admin.Approve(_token, _status.ConnectionId!);
        Admin.Arm(_token);
    });
    private void Stop() => Run(() => Admin.Stop(_token));
    private void Unlock() => Run(() => Admin.Unlock(_token, _status!.LockRevision), "Unlocked. Allow control separately when ready.");
    private void Reset() => Run(() => Admin.Recover(_token));
    private void End() => Run(() =>
    {
        Admin.End(_token);
        _invitation = null;
        _attempt = Guid.NewGuid();
    });
    private void Generate()
    {
        if (_busy) return;
        _busy = true;
        Run(() => _invitation = Admin.Generate(_token, _attempt));
        _busy = false;
    }

    private async Task Copy()
    {
        if (_module is null || _invitation is null) return;
        _message = await _module.InvokeAsync<bool>("copy", _invitation.Url)
            ? "Copied." : "Select the link and copy it manually.";
        _error = false;
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150), Time);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await InvokeAsync(() =>
                {
                    try
                    {
                        _status = Admin.Read(_token);
                        RefreshLog();
                        if (_invitation?.ExpiresAt <= Time.GetUtcNow()) _invitation = null;
                    }
                    catch (ControlRejectedException) { _status = null; _logs.Clear(); }
                    StateHasChanged();
                });
        }

        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void RefreshLog()
    {
        var added = Admin.ReadLog(_token, _lastLogSequence);
        if (added.Count == 0) return;
        _lastLogSequence = added[^1].Sequence;
        _logs.AddRange(added);
        if (_logs.Count > 200) _logs.RemoveRange(0, _logs.Count - 200);
    }

    private static string LogClass(LogLevel level) => level switch
    {
        >= LogLevel.Error => "error",
        LogLevel.Warning => "warning",
        _ => ""
    };

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_poll is not null) await _poll;
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
                Logger.LogWarning("Operator activity view cleanup did not complete.");
            }
        }
        _token = "";
        _lifetime.Dispose();
    }
}
