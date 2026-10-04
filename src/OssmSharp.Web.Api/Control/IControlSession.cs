namespace OssmSharp.Web.Api.Control;

/// <summary>
/// Owns UI invitation and guest-permission state while delegating device behavior to the runtime.
/// </summary>
public interface IControlSession
{
    /// <summary>
    /// Returns state visible to the user.
    /// </summary>
    Task<UserSessionState> GetUserStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns state visible to the specified guest connection.
    /// </summary>
    Task<GuestSessionState> GetGuestStateAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks an invitation token without redeeming it.
    /// </summary>
    /// <param name="token">The invitation token to check.</param>
    /// <returns>The invitation ID, not the token, or <see langword="null"/> if invalid or shutting down.</returns>
    string? AuthenticateInvitation(string? token);

    /// <summary>
    /// Joins a guest only if the authenticated invitation and token match the current invitation.
    /// </summary>
    /// <param name="connectionId">The guest connection ID.</param>
    /// <param name="invitationId">The invitation ID from the authenticated connection, not the request.</param>
    /// <param name="request">The invitation token and request correlation ID.</param>
    /// <returns>The join result. Success replaces the invitation but does not enable control.</returns>
    ControlResponse JoinGuest(string connectionId, string? invitationId, GuestJoinRequest request);

    /// <summary>
    /// Re-arms when needed, then allows the admitted guest to submit settings until revoked.
    /// </summary>
    Task<ControlResponse> AllowGuestAsync(string correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Renews permission for the admitted guest connection.
    /// </summary>
    Task<ControlResponse> RenewGuestAsync(string connectionId, string correlation);

    /// <summary>
    /// Submits authorized settings to the runtime.
    /// </summary>
    Task<SettingsResponse> SubmitAsync(
        string connectionId,
        SettingsRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Requests runtime Stop for the admitted guest.
    /// </summary>
    Task<ControlResponse> StopGuestAsync(
        string connectionId,
        string correlation);

    /// <summary>
    /// Retracts once and stops for the permitted guest, discarding pending settings.
    /// Stop, cancellation, or disconnect interrupts the operation; settings are never replayed.
    /// </summary>
    Task<ControlResponse> ResetPositionGuestAsync(string connectionId, string correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Retracts once and stops for the user without granting guest permission.
    /// Stop, cancellation, or disconnect interrupts the operation; settings are never replayed.
    /// </summary>
    Task<ControlResponse> ResetPositionAsync(string correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the admitted guest and requests runtime Stop.
    /// </summary>
    Task<ControlResponse> LeaveGuestAsync(
        string connectionId,
        string correlation);

    /// <summary>
    /// Removes a disconnected guest and requests runtime Stop.
    /// </summary>
    Task GuestDisconnectedAsync(string connectionId);

    /// <summary>
    /// Revokes guest permission and requests runtime Stop for the user.
    /// </summary>
    Task<ControlResponse> StopAsync(string correlation);

    /// <summary>
    /// Ends the current guest session and requests runtime Stop.
    /// </summary>
    Task<ControlResponse> EndGuestAsync(string correlation);

    /// <summary>
    /// Connects the selected device through the runtime.
    /// </summary>
    Task<ControlResponse> ConnectAsync(string correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Retries a required Stop without expiring guest permission.
    /// </summary>
    Task ExpireGuestAsync();

    /// <summary>
    /// Disconnects the runtime device during host shutdown.
    /// </summary>
    Task ShutdownAsync();
}
