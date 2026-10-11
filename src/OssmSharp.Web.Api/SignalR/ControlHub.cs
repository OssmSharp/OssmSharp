using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OssmSharp.Web.Api.Control;

namespace OssmSharp.Web.Api.SignalR;

/// <summary>
/// Provides role-scoped UI operations over the control session.
/// </summary>
public sealed class ControlHub(IControlSession session) : Hub
{
    /// <summary>
    /// Consumes an invitation once and returns a private saved-session credential without attaching a controller.
    /// </summary>
    /// <param name="request">The invitation token and request identifier.</param>
    /// <returns>The redemption result and, on acceptance, a private credential with its fixed 24-hour expiry.</returns>
    [Authorize(Policy = ControlServicePolicies.Invitation)]
    public GuestRedemptionResponse Redeem(GuestJoinRequest request) =>
        session.RedeemInvitation(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), request);

    /// <summary>
    /// Reads saved-session availability without attaching a controller; expired sessions are revoked and request Stop.
    /// </summary>
    /// <returns>The current session's availability and original expiry, or <see langword="null"/> if revoked or expired.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public SavedGuestSession? GetSavedSession() =>
        session.GetSavedGuestSession(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier));

    /// <summary>
    /// Revokes the authenticated saved session only while no controller is attached, and requests Stop.
    /// </summary>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The Stop result. Revocation persists even if device cleanup fails.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<ControlResponse> LeaveSavedSession(string correlation) =>
        session.LeaveSavedGuestAsync(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), correlation);

    /// <summary>
    /// Returns invitation, guest permission, and runtime state visible to the user.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<UserSessionState> GetUserState() => session.GetUserStateAsync(Context.ConnectionAborted);

    /// <summary>
    /// Updates shared preview-only lengths using user authority, without controlling motion.
    /// </summary>
    /// <param name="correlation">The request identifier.</param>
    /// <param name="dimensions">Rail and attachment lengths in centimetres.</param>
    /// <returns>The accepted update or an explicit validation rejection.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public ControlResponse SetPreviewDimensions(string correlation, PreviewDimensions? dimensions) =>
        session.SetPreviewDimensions(correlation, dimensions);

    /// <summary>
    /// Returns state visible to the calling guest connection.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<GuestSessionState> GetGuestState() =>
        session.GetGuestStateAsync(Context.ConnectionId, Context.ConnectionAborted);

    /// <summary>
    /// Attaches the calling controller to its authenticated saved guest session without restarting motion.
    /// </summary>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The attachment result; an occupied controller slot or pending cleanup prevents attachment.</returns>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public ControlResponse Join(string correlation) =>
        session.JoinGuest(Context.ConnectionId, Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), correlation);

    /// <summary>
    /// Submits complete settings for the calling guest.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<SettingsResponse> Submit(SettingsRequest request) =>
        session.SubmitAsync(Context.ConnectionId, request, Context.ConnectionAborted);

    /// <summary>
    /// Renews the calling guest's permission.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<ControlResponse> Renew(string correlation) =>
        session.RenewGuestAsync(Context.ConnectionId, correlation);

    /// <summary>
    /// Requests runtime Stop for the calling guest.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<ControlResponse> GuestStop(string correlation) =>
        session.StopGuestAsync(Context.ConnectionId, correlation);

    /// <summary>
    /// Retracts once and stops for the calling guest with active permission.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<ControlResponse> GuestResetPosition(string correlation) =>
        session.ResetPositionGuestAsync(Context.ConnectionId, correlation, Context.ConnectionAborted);

    /// <summary>
    /// Retracts once and stops without granting guest permission.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlResponse> ResetPosition(string correlation) =>
        session.ResetPositionAsync(correlation, Context.ConnectionAborted);

    /// <summary>
    /// Leaves the current guest session and requests runtime Stop.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public Task<ControlResponse> Leave(string correlation) =>
        session.LeaveGuestAsync(Context.ConnectionId, correlation);

    /// <summary>
    /// Allows the admitted guest to submit settings.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlResponse> AllowGuest(string correlation) =>
        session.AllowGuestAsync(correlation, Context.ConnectionAborted);

    /// <summary>
    /// Revokes guest permission and requests runtime Stop.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlResponse> Stop(string correlation) =>
        session.StopAsync(correlation);

    /// <summary>
    /// Ends the current guest session and requests runtime Stop.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlResponse> EndGuest(string correlation) =>
        session.EndGuestAsync(correlation);

    /// <summary>
    /// Replaces the invitation and revokes the saved guest session before awaiting Stop.
    /// </summary>
    /// <param name="correlation">The request identifier.</param>
    /// <returns>The refresh result. The new invitation remains current even if required Stop cleanup fails.</returns>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlResponse> RefreshInvitation(string correlation) =>
        session.RefreshInvitationAsync(correlation);

    /// <summary>
    /// Connects the selected device through the runtime.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.AdminWeb)]
    public Task<ControlResponse> Connect(string correlation) =>
        session.ConnectAsync(correlation, Context.ConnectionAborted);

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await session.GuestDisconnectedAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
