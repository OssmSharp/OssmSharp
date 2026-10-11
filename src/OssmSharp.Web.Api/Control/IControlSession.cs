namespace OssmSharp.Web.Api.Control;

/// <summary>
/// Owns invitation redemption, saved guest admission, and control permission while delegating device behavior to the runtime.
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
    /// Updates shared illustration lengths without issuing device commands.
    /// </summary>
    /// <param name="correlation">The non-empty request identifier, at most 64 characters without control characters.</param>
    /// <param name="dimensions">Rail length from 1 to 500 cm and attachment length from 0 to 500 cm.</param>
    /// <returns>An accepted preview update or an explicit validation or shutdown rejection.</returns>
    ControlResponse SetPreviewDimensions(string correlation, PreviewDimensions? dimensions);

    /// <summary>
    /// Checks an invitation token without redeeming it.
    /// </summary>
    /// <param name="token">The invitation token to check.</param>
    /// <returns>The invitation ID, not the token, or <see langword="null"/> if invalid or shutting down.</returns>
    string? AuthenticateInvitation(string? token);

    /// <summary>
    /// Consumes the current invitation once, without changing its code, attaching a controller, or granting control.
    /// </summary>
    /// <param name="invitationId">The invitation ID from the authenticated connection, not the request.</param>
    /// <param name="request">The invitation token and request correlation ID.</param>
    /// <returns>The redemption result and, on success, a private credential expiring 24 hours later.</returns>
    GuestRedemptionResponse RedeemInvitation(string? invitationId, GuestJoinRequest request);

    /// <summary>
    /// Validates a saved-session credential against the current invitation and fixed expiry.
    /// </summary>
    /// <param name="token">The private saved-session credential.</param>
    /// <returns>The session identity, or <see langword="null"/> when invalid, revoked, expired, or shutting down.</returns>
    string? AuthenticateGuestSession(string? token);

    /// <summary>
    /// Returns nonsensitive saved-session metadata for an authenticated identity, without attaching a controller.
    /// </summary>
    /// <param name="sessionId">The authenticated session identity.</param>
    /// <returns>The saved session, or <see langword="null"/> when it is no longer current.</returns>
    SavedGuestSession? GetSavedGuestSession(string? sessionId);

    /// <summary>
    /// Attaches one controller to the current saved session. A second controller cannot replace it.
    /// </summary>
    /// <param name="connectionId">The new connection ID.</param>
    /// <param name="sessionId">The saved-session identity from authentication.</param>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The attachment result. Pending Stop cleanup prevents attachment until it settles.</returns>
    ControlResponse JoinGuest(string connectionId, string? sessionId, string correlation);

    /// <summary>
    /// Revokes an offline saved session and requests Stop. Attached controllers must use <see cref="LeaveGuestAsync"/>.
    /// Revocation does not depend on Stop succeeding.
    /// </summary>
    /// <param name="sessionId">The authenticated saved-session identity.</param>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The Stop result; invalid identities or occupied controller slots are rejected without affecting the active session.</returns>
    Task<ControlResponse> LeaveSavedGuestAsync(string? sessionId, string correlation);

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
    /// Detaches a disconnected controller and requests Stop while preserving saved admission and existing permission.
    /// </summary>
    /// <param name="connectionId">The disconnected controller; stale connection callbacks have no effect.</param>
    /// <returns>The owned Stop attempt has settled. Attachment still requires a free controller slot and completed required Stop/reset cleanup.</returns>
    Task GuestDisconnectedAsync(string connectionId);

    /// <summary>
    /// Revokes guest permission and requests runtime Stop for the user.
    /// </summary>
    Task<ControlResponse> StopAsync(string correlation);

    /// <summary>
    /// Revokes the current saved guest session before requesting runtime Stop, including when the guest is offline.
    /// </summary>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The Stop result. Revocation persists even when cleanup fails.</returns>
    Task<ControlResponse> EndGuestAsync(string correlation);

    /// <summary>
    /// Replaces the invitation, immediately revoking the saved session, then requests any required Stop.
    /// </summary>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The refresh result; the new invitation remains current even when required Stop fails.</returns>
    Task<ControlResponse> RefreshInvitationAsync(string correlation);

    /// <summary>
    /// Connects the selected device through the runtime.
    /// </summary>
    Task<ControlResponse> ConnectAsync(string correlation, CancellationToken cancellationToken);

    /// <summary>
    /// Expires saved sessions at their fixed deadline and retries required Stop cleanup.
    /// </summary>
    Task ExpireGuestAsync();

    /// <summary>
    /// Disconnects the runtime device during host shutdown.
    /// </summary>
    Task ShutdownAsync();
}
