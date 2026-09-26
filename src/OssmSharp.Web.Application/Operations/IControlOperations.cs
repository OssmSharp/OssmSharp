namespace OssmSharp.Web.Application.Operations;

/// <summary>
/// Defines transport-neutral operations over the control authority.
/// </summary>
public interface IControlOperations
{
    /// <summary>
    /// Returns the current trusted controller snapshot.
    /// </summary>
    /// <returns>The operator state and precondition for subsequent mutations.</returns>
    ControlAuthoritySnapshot Read();

    /// <summary>
    /// Returns bounded, redacted activity entries after a sequence number.
    /// </summary>
    /// <param name="afterSequence">The last sequence already seen by the caller.</param>
    /// <returns>The retained activity after that sequence.</returns>
    IReadOnlyList<ControlActivityEntry> ReadActivity(long afterSequence);

    /// <summary>
    /// Returns the current reusable invitation.
    /// </summary>
    /// <returns>The invitation available to the operator.</returns>
    Invitation ReadInvitation();

    /// <summary>
    /// Returns one atomically captured operator state and invitation projection.
    /// </summary>
    /// <returns>Consistent operator authority and invitation state.</returns>
    OperatorLiveControlStatus ReadOperatorStatus();

    /// <summary>
    /// Replaces the invitation and revokes active guest authority.
    /// </summary>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after cycling the invitation.</returns>
    ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected);

    /// <summary>
    /// Approves a pending guest connection.
    /// </summary>
    /// <param name="request">The connection to approve.</param>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after approval.</returns>
    ControlAuthoritySnapshot Approve(ApproveGuestRequest? request, AuthorityPrecondition expected);

    /// <summary>
    /// Applies compatible motion limits without rearming control.
    /// </summary>
    /// <param name="limits">The proposed speed and depth caps.</param>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after applying compatible limits.</returns>
    ControlAuthoritySnapshot ApplySessionLimits(ControlLimits limits, AuthorityPrecondition expected);

    /// <summary>
    /// Recovers the controller into Paused after device checks.
    /// </summary>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The recovered authority state.</returns>
    ControlAuthoritySnapshot Recover(AuthorityPrecondition expected);

    /// <summary>
    /// Arms an approved session after controller safety checks.
    /// </summary>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after arming.</returns>
    ControlAuthoritySnapshot Arm(AuthorityPrecondition expected);

    /// <summary>
    /// Recovers, approves, and arms the expected guest authority atomically when safety checks permit.
    /// </summary>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after the combined operation.</returns>
    ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected);

    /// <summary>
    /// Stops motion and pauses control without requiring a precondition.
    /// </summary>
    /// <returns>The authority state after the stop request.</returns>
    ControlAuthoritySnapshot Stop();

    /// <summary>
    /// Ends the session and atomically replaces the invitation.
    /// </summary>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after ending the session.</returns>
    ControlAuthoritySnapshot End(AuthorityPrecondition expected);

    /// <summary>
    /// Clears a matching external lock without rearming control.
    /// </summary>
    /// <param name="request">The external-lock revision to clear.</param>
    /// <param name="expected">The operator authority state observed before the mutation.</param>
    /// <returns>The authority state after unlocking.</returns>
    ControlAuthoritySnapshot Unlock(UnlockControlRequest? request, AuthorityPrecondition expected);

}
