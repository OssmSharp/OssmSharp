namespace OSSMDotNet.ControlService.Tests.Helpers;

/// <summary>
/// Drives authority operations with the current precondition for policy tests.
/// </summary>
/// <param name="core">The engine used when no approved session can be armed through authority.</param>
/// <param name="authority">The authority under test.</param>
internal sealed class AuthorityTestEngine(
    PrototypeEngine core,
    ControlAuthority authority)
{
    internal ControlSnapshot Snapshot => authority.Read().Snapshot;

    internal Invitation GetInvitation(Uri origin) => authority.ReadInvitation(origin);

    internal Invitation CreateInvitation(Guid attemptId, Uri origin, string scope) =>
        authority.CreateInvitation(attemptId, origin, scope);

    internal Invitation CycleInvitation(Uri origin)
    {
        authority.CycleInvitation(authority.Read().Precondition, origin);
        return authority.ReadInvitation(origin);
    }

    internal JoinResult Join(string sessionId, string proof, string connectionId) =>
        authority.JoinGuest(sessionId, proof, connectionId);

    internal void Approve(string connectionId) => authority.Approve(authority.Read().Precondition, connectionId);

    internal void SetLimits(ControlLimits limits) =>
        authority.ApplySessionLimits(authority.Read().Precondition, limits);

    internal void Recover() => authority.Recover(authority.Read().Precondition);

    /// <summary>
    /// Arms through authority for an approved session, or directly through the core otherwise.
    /// </summary>
    internal void Arm()
    {
        if (Snapshot.SessionMode == "Approved")
            authority.Arm(authority.Read().Precondition);
        else
            core.Arm();
    }

    /// <summary>
    /// Stops through authority for the default operator reason, or pauses the core with another reason.
    /// </summary>
    /// <param name="reason">The stop or pause reason.</param>
    internal void Pause(string reason = "operator-stop")
    {
        if (reason == "operator-stop")
            authority.Stop();
        else
            core.Pause(reason);
    }

    internal void EndSession(string reason = "operator-ended") => authority.EndSession(reason);

    internal void Unlock(long expectedRevision) => authority.Unlock(authority.Read().Precondition, expectedRevision);

    internal ControlSnapshot GuestSnapshot(string sessionId, string proof, string connectionId) =>
        authority.ReadGuest(sessionId, proof, connectionId).Snapshot;

    internal void Submit(string sessionId, string proof, string connectionId, MotionInput input) =>
        authority.SubmitGuest(sessionId, proof, connectionId, input);

    internal void RecordRejectedInputAttempt(string sessionId, string proof, string connectionId) =>
        authority.RecordRejectedGuestInputAttempt(sessionId, proof, connectionId);

    internal void GuestStop(string sessionId, string proof, string connectionId) =>
        authority.StopGuest(sessionId, proof, connectionId);

    internal void Leave(string sessionId, string proof, string connectionId) =>
        authority.LeaveGuest(sessionId, proof, connectionId);

    internal void TransportLost(string connectionId) => authority.GuestTransportLost(connectionId);

    internal void Tick() => authority.Tick();

    internal ValueTask DisposeAsync() => authority.DisposeAsync();
}
