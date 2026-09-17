using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.DeviceBridge;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Coordinates transport-neutral operator operations through the authority and device bridge.</summary>
public sealed class ControlOperations(
    ControlAuthority authority,
    SimulatorProcessBridge bridge,
    IOptions<ControlServiceOptions> options,
    ControlActivityProvider activity,
    ILogger<ControlOperations> logger) : IControlOperations
{
    /// <inheritdoc />
    public ControlAuthoritySnapshot Read() => authority.Read();

    /// <inheritdoc />
    public IReadOnlyList<ControlActivityEntry> ReadActivity(long afterSequence) => activity.ReadAfter(afterSequence);

    /// <inheritdoc />
    public Invitation ReadInvitation() => authority.ReadInvitation(new Uri(options.Value.GuestOrigin));

    /// <inheritdoc />
    public OperatorLiveControlStatus ReadOperatorStatus() =>
        authority.ReadOperatorStatus(new Uri(options.Value.GuestOrigin));

    /// <inheritdoc />
    public ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected)
    {
        var snapshot = authority.CycleInvitation(expected, new Uri(options.Value.GuestOrigin));
        LogOperatorActivity("Invitation link replaced; guest access revoked and Stop requested");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot Approve(ApproveGuestRequest? request, AuthorityPrecondition expected)
    {
        if (request is null)
            throw new ControlRejectedException("invalid-request");
        var snapshot = authority.Approve(expected, request.ConnectionId);
        LogOperatorActivity("Guest approved");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot ApplySessionLimits(ControlLimits limits, AuthorityPrecondition expected)
    {
        var snapshot = authority.ApplySessionLimits(expected, limits);
        LogOperatorActivity("Session limits applied");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot Recover(AuthorityPrecondition expected)
    {
        var snapshot = authority.Recover(expected);
        LogOperatorActivity("Connection ready; control paused");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot Arm(AuthorityPrecondition expected)
    {
        var snapshot = authority.Arm(expected);
        LogOperatorActivity("Control enabled");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected)
    {
        var snapshot = authority.AllowControl(expected);
        LogOperatorActivity("Control enabled");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot Stop()
    {
        var snapshot = authority.Stop();
        LogOperatorActivity("Stop requested");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot End(AuthorityPrecondition expected)
    {
        var snapshot = authority.End(expected, new Uri(options.Value.GuestOrigin));
        LogOperatorActivity("Stop requested; session ended and invitation rotated");
        return snapshot;
    }

    /// <inheritdoc />
    public ControlAuthoritySnapshot Unlock(UnlockControlRequest? request, AuthorityPrecondition expected)
    {
        if (request is null)
            throw new ControlRejectedException("invalid-request");
        var snapshot = authority.Unlock(expected, request.LockRevision);
        LogOperatorActivity("Unlocked; control remains paused");
        return snapshot;
    }

    /// <inheritdoc />
    public Task InjectSimulatorFaultAsync(SimulatorFaultRequest? request)
    {
        if (request is null)
            throw new ControlRejectedException("invalid-request");
        var desired = authority.Read().Snapshot.Desired;
        return request.Kind switch
        {
            "external-zero" => bridge.InjectAsync("external", desired with { Speed = 0 }),
            "external-depth" => bridge.InjectAsync("external",
                desired with { Depth = Math.Min(100, desired.Depth + 1) }),
            "delay" => bridge.InjectAsync("delay", value: 1800),
            "stale" => bridge.InjectAsync("stale", value: 1),
            "fresh" => bridge.InjectAsync("stale", value: 0),
            "disconnect" => bridge.InjectAsync("disconnect"),
            "connect" => bridge.InjectAsync("connect"),
            _ => throw new ControlRejectedException("unknown-simulator-fault")
        };
    }

    private void LogOperatorActivity(string action) => ControlServiceLog.OperatorActivity(logger, action);
}
