using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OSSMDotNet.ControlService.Activity;
using OSSMDotNet.ControlService.Authority;
using OSSMDotNet.ControlService.Models;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.Operations;

/// <summary>Coordinates transport-neutral operator operations.</summary>
/// <param name="authority">The owner of operator and guest control state.</param>
/// <param name="options">The validated control-service settings.</param>
/// <param name="activity">The operator-visible activity store.</param>
/// <param name="logger">The logger for rejected or completed operator actions.</param>
public sealed class ControlOperations(
    ControlAuthority authority,
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

    private void LogOperatorActivity(string action) => ControlServiceLog.OperatorActivity(logger, action);
}
