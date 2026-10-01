using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Api.Control;

/// <summary>
/// Owns UI invitation and guest-permission state while delegating device behavior to the runtime.
/// </summary>
public interface IControlSession
{
    /// <summary>Returns state visible to the user.</summary>
    Task<UserSessionState> GetUserStateAsync(CancellationToken cancellationToken);

    /// <summary>Returns state visible to the specified guest connection.</summary>
    Task<GuestSessionState> GetGuestStateAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>Admits the specified connection using an invitation.</summary>
    ControlResponse JoinGuest(string connectionId, GuestJoinRequest request);

    /// <summary>Re-arms when needed, then allows the admitted guest to submit settings until revoked.</summary>
    Task<ControlResponse> AllowGuestAsync(string correlation, CancellationToken cancellationToken);

    /// <summary>Renews permission for the admitted guest connection.</summary>
    Task<ControlResponse> RenewGuestAsync(string connectionId, string correlation);

    /// <summary>Submits authorized settings to the runtime.</summary>
    Task<SettingsResponse> SubmitAsync(
        string connectionId,
        SettingsRequest request,
        CancellationToken cancellationToken);

    /// <summary>Requests runtime Stop for the admitted guest.</summary>
    Task<ControlResponse> StopGuestAsync(
        string connectionId,
        string correlation);

    /// <summary>Removes the admitted guest and requests runtime Stop.</summary>
    Task<ControlResponse> LeaveGuestAsync(
        string connectionId,
        string correlation);

    /// <summary>Removes a disconnected guest and requests runtime Stop.</summary>
    Task GuestDisconnectedAsync(string connectionId);

    /// <summary>Revokes guest permission and requests runtime Stop for the user.</summary>
    Task<ControlResponse> StopAsync(string correlation);

    /// <summary>Ends the current guest session and requests runtime Stop.</summary>
    Task<ControlResponse> EndGuestAsync(string correlation);

    /// <summary>Connects the selected device through the runtime.</summary>
    Task<ControlResponse> ConnectAsync(string correlation, CancellationToken cancellationToken);

    /// <summary>Retries a required Stop without expiring guest permission.</summary>
    Task ExpireGuestAsync();

    /// <summary>Disconnects the runtime device during host shutdown.</summary>
    Task ShutdownAsync();
}
