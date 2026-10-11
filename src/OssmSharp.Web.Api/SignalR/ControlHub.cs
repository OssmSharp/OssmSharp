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
    /// Admits the calling guest using an invitation.
    /// </summary>
    [Authorize(Policy = ControlServicePolicies.GuestWeb)]
    public ControlResponse Join(GuestJoinRequest request) =>
        session.JoinGuest(Context.ConnectionId, Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), request);

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
