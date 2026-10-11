using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Clients;

/// <summary>
/// Provides saved-session-authenticated API requests for one guest, or redeems an invitation without attaching control.
/// </summary>
public interface IGuestApiClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the current session, control permission, and device state visible to the joined guest.
    /// </summary>
    /// <returns>The current guest-visible session state.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires before a response is received.</exception>
    /// <exception cref="InvalidDataException">The API returns no guest state.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<GuestSessionState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes an invitation and creates a fixed-lifetime saved session without attaching a controller.
    /// </summary>
    /// <param name="invitationToken">The invitation token: up to 128 ASCII letters, digits, hyphens, or underscores.</param>
    /// <returns>The private saved-session credential and absolute expiry. Device control still requires user approval.</returns>
    /// <remarks>
    /// Keep the credential in a protected HttpOnly cookie, never in JavaScript, URLs, or logs.
    /// Use a new client to attach it. Failures close this client; never replay redemption after an unknown result.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="invitationToken"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="invitationToken"/> is empty or whitespace.</exception>
    /// <exception cref="ApiClientException">The invitation is invalid, the API rejects redemption, or the connection is unavailable.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; redemption acceptance is unknown.</exception>
    /// <exception cref="InvalidDataException">The API returns a missing result, unusable credential, or missing expiry.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<GuestRedemptionResponse> RedeemAsync(string invitationToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches a controller using a saved credential without replacing an active controller.
    /// Failures close this client; recovery requires a new one.
    /// </summary>
    /// <param name="sessionToken">The private credential from <see cref="RedeemAsync"/>, revoked by expiry, invitation changes, Leave, End guest, or API restart.</param>
    /// <returns>The accepted attachment result, retaining existing permission without restarting motion.</returns>
    /// <exception cref="ApiClientException">The credential is invalid, another controller is active, Stop is pending, or the API is unavailable.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The attachment result is unknown; close this client before retrying.</exception>
    /// <exception cref="OperationCanceledException">Startup or the response wait is cancelled.</exception>
    Task<ControlResponse> ResumeAsync(string sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a saved session without attaching or stopping its controller.
    /// Expiry revokes the session and requests Stop.
    /// </summary>
    /// <param name="sessionToken">The private saved-session credential.</param>
    /// <returns>The connection occupancy and original absolute expiry.</returns>
    /// <exception cref="ApiClientException">The session is invalid or the API is unavailable.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires.</exception>
    /// <exception cref="InvalidDataException">The API returns a missing expiry or contradictory connection availability.</exception>
    Task<SavedGuestSession> GetSavedSessionAsync(string sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes an offline saved session and requests Stop. Attached controllers must use <see cref="LeaveAsync"/>.
    /// </summary>
    /// <param name="sessionToken">The private saved-session credential.</param>
    /// <returns>Acceptance after any required Stop completes. Revocation does not depend on Stop succeeding.</returns>
    /// <exception cref="ApiClientException">The session is invalid, a controller is attached, or required Stop fails.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; revocation may already have occurred.</exception>
    Task<ControlResponse> LeaveSavedSessionAsync(string sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits one complete settings request for the joined guest without the ordinary response deadline.
    /// </summary>
    /// <param name="request">The request identifier, pattern, and complete settings to send to the API.</param>
    /// <returns>Settings rejection and runtime failure details in <see cref="SettingsResponse.Status"/> and <see cref="SettingsResponse.ErrorCode"/>, rather than client exceptions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API invocation is rejected.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<SettingsResponse> SubmitAsync(SettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests renewal of the joined guest's existing control permission.
    /// </summary>
    /// <returns>The acknowledgement that existing control permission is active.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, control is not allowed, or the API connection or request fails.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> RenewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests runtime Stop for the joined guest without ending the session or revoking existing control permission.
    /// </summary>
    /// <returns>The accepted stop response.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retracts once and stops for the joined, permitted guest without resuming settings.
    /// Use <see cref="StopAsync"/> to interrupt it.
    /// </summary>
    /// <returns>The completed position-reset response, without the ordinary response deadline.</returns>
    /// <exception cref="ApiClientException">The guest lacks permission, the service rejects the request, or the operation fails.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the response wait; a dispatched server operation may continue.</exception>
    Task<ControlResponse> ResetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the joined guest session and requests runtime Stop without closing the RPC connection.
    /// </summary>
    /// <returns>The accepted session-ending response.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> LeaveAsync(CancellationToken cancellationToken = default);
}
