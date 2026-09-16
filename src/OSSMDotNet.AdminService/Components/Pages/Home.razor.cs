using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OSSMDotNet.Models;
using OSSMDotNet.ControlCore;

namespace OSSMDotNet.AdminService.Components.Pages;

/// <summary>Renders the local operator control page.</summary>
public partial class Home
{
    [Inject] private AdminOperations Admin { get; set; } = null!;
    [Inject] private IJSRuntime Js { get; set; } = null!;
    [Inject] private TimeProvider Time { get; set; } = null!;
    [Inject] private ILogger<Home> Logger { get; set; } = null!;
    private OperatorNoticeState _notice = null!;
    private bool _loaded;
    private ControlSnapshot? _status;
    private Invitation? _invitation;
    private int _speedCap = 100;
    private int _maximumDepth = 100;
    private bool _busy;
    private IJSObjectReference? _module;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _poll;
    private readonly List<OperatorLogEntry> _logs = [];
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
    private string SessionLabel => _status?.SessionMode switch
    {
        "PendingApproval" => "Guest waiting",
        "Approved" => "Guest connected",
        _ => "No guest"
    };
    private bool GuestWaiting => _status?.SessionMode == "PendingApproval";
    private string SessionVariant => _status?.SessionMode switch
    {
        "PendingApproval" => "warning",
        "Approved" => "success",
        _ => "neutral"
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
                Logger.LogWarning("Automatic activity-log scrolling is unavailable.");
            }
            return;
        }
        try
        {
            _module = await Js.InvokeAsync<IJSObjectReference>("import", "./admin.js");
            ReadStatus();
            _invitation = Admin.ReadInvitation();
            RefreshLog();
            _poll = PollAsync();
        }
        catch (JSException)
        {
            Logger.LogWarning("The operator page could not initialize.");
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
            ReadStatus();
            _notice.Show(message);
        }
        catch (ControlRejectedException error)
        {
            Logger.LogWarning(new EventId(200, "OperatorActivity"), "Operator action was not accepted.");
            _notice.Show(error.Code switch
            {
                "invalid-limits" => "Use a speed cap of 0-100 and a depth of 1-100.",
                "operation-pending" or "device-not-stopped" => "Wait for the device to stop, then try again.",
                "limits-not-configured" => "Control is disabled by the current limits.",
                "limits-conflict" => "The current or pending settings exceed these limits. Lower the guest settings or stop the session first.",
                "external-lock" => "Unlock the controller before continuing.",
                _ => "That action is unavailable. Check the connection and session state."
            }, true);
        }
    }

    private void ApplyLimits() => Run(() =>
    {
        Admin.ApplySessionLimits(_speedCap, _maximumDepth);
    }, "Limits applied.");

    private void AllowControl() => Run(() =>
    {
        if (_status?.Mode == "Initializing") Admin.Recover();
        if (_status?.SessionMode == "PendingApproval")
            Admin.Approve(_status.ConnectionId!);
        Admin.Arm();
    });
    private void Unlock() => Run(() => Admin.Unlock(_status!.LockRevision), "Unlocked. Allow control separately when ready.");
    private void Reset() => Run(() => Admin.Recover());
    private void End() => Run(() =>
    {
        Admin.End();
        _invitation = Admin.ReadInvitation();
    }, "Session ended. Invitation link replaced.");
    private void Cycle()
    {
        if (_busy) return;
        _busy = true;
        Run(() => _invitation = Admin.CycleInvitation(),
            "Invitation link replaced. The previous guest's access was revoked and Stop was requested. Allow control separately when ready.");
        _busy = false;
    }

    private async Task Copy()
    {
        if (_module is null || _invitation is null) return;
        _notice.CopyCompleted(await _module.InvokeAsync<bool>("copy", _invitation.Url));
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150), Time);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await InvokeAsync(() =>
                {
                    ReadStatus();
                    _invitation = Admin.ReadInvitation();
                    RefreshLog();
                    StateHasChanged();
                });
        }

        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void ReadStatus()
    {
        var next = Admin.Read();
        if (_status is null || _speedCap == _status.Limits.SpeedCap) _speedCap = next.Limits.SpeedCap;
        if (_status is null || _maximumDepth == _status.Limits.MaximumDepth) _maximumDepth = next.Limits.MaximumDepth;
        _status = next;
    }

    private void RefreshLog()
    {
        var added = Admin.ReadLog(_lastLogSequence);
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

    /// <summary>Stops page polling and releases interactive resources.</summary>
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
                Logger.LogWarning("Activity log cleanup did not complete.");
            }
        }
        _lifetime.Dispose();
    }
}
