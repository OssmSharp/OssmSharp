namespace OSSMDotNet.Models;

/// <summary>Defines transport-neutral operations over the control authority.</summary>
public interface IControlOperations
{
    /// <summary>Returns the current trusted controller snapshot.</summary>
    ControlAuthoritySnapshot Read();

    /// <summary>Returns bounded, redacted activity entries after a sequence number.</summary>
    IReadOnlyList<ControlActivityEntry> ReadActivity(long afterSequence);

    /// <summary>Returns the current reusable invitation.</summary>
    Invitation ReadInvitation();

    /// <summary>Returns one atomically captured operator state and invitation projection.</summary>
    OperatorLiveControlStatus ReadOperatorStatus();

    /// <summary>Replaces the invitation and revokes active guest authority.</summary>
    ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected);

    /// <summary>Approves a pending guest connection.</summary>
    ControlAuthoritySnapshot Approve(ApproveGuestRequest? request, AuthorityPrecondition expected);

    /// <summary>Applies compatible motion limits without rearming control.</summary>
    ControlAuthoritySnapshot ApplySessionLimits(ControlLimits limits, AuthorityPrecondition expected);

    /// <summary>Recovers the controller into Paused after device checks.</summary>
    ControlAuthoritySnapshot Recover(AuthorityPrecondition expected);

    /// <summary>Arms an approved session after controller safety checks.</summary>
    ControlAuthoritySnapshot Arm(AuthorityPrecondition expected);

    /// <summary>Recovers, approves, and arms the expected guest authority atomically when safety checks permit.</summary>
    ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected);

    /// <summary>Stops motion and pauses control.</summary>
    ControlAuthoritySnapshot Stop();

    /// <summary>Ends the session and atomically replaces the invitation.</summary>
    ControlAuthoritySnapshot End(AuthorityPrecondition expected);

    /// <summary>Clears a matching external lock without rearming control.</summary>
    ControlAuthoritySnapshot Unlock(UnlockControlRequest? request, AuthorityPrecondition expected);

    /// <summary>Injects a simulator-only test operation.</summary>
    Task InjectSimulatorFaultAsync(SimulatorFaultRequest? request);
}
