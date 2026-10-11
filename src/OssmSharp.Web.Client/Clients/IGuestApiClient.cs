using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Clients;

/// <summary>
/// Provides invitation-authenticated API requests for one guest session.
/// </summary>
/// <remarks>
/// Complete <see cref="JoinAsync"/> successfully before making other requests. Other methods wait for
/// connection startup to finish; their cancellation tokens do not cancel that wait. Cancellation of a response
/// wait does not roll back a dispatched request. Dispose the client when it is no longer needed; disposal
/// cancels pending client waits, drains them, and releases the RPC connection.
/// </remarks>
public interface IGuestApiClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the current session, control permission, and device state visible to the joined guest.
    /// </summary>
    /// <returns>The current guest-visible session state.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires before a response is received.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<GuestSessionState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an invitation-authenticated connection and joins the guest session.
    /// </summary>
    /// <param name="invitationToken">The invitation token: up to 128 ASCII letters, digits, hyphens, or underscores.</param>
    /// <returns>The accepted join response. Device control still requires administrator approval.</returns>
    /// <remarks>
    /// Connection-start or API join failures close the connection, including cancellation. To retry after such
    /// a failure, use a new client and invitation.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="invitationToken"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="invitationToken"/> is empty or whitespace.</exception>
    /// <exception cref="ApiClientException">The invitation is invalid, the API rejects the join, or the connection is unavailable.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; join acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> JoinAsync(string invitationToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits one complete settings request for the joined guest.
    /// </summary>
    /// <param name="request">The request identifier, pattern, and complete settings to send to the API.</param>
    /// <returns>The settings result, including API rejection or runtime failure details.</returns>
    /// <remarks>
    /// Inspect <see cref="SettingsResponse.Status"/> and <see cref="SettingsResponse.ErrorCode"/>;
    /// settings rejection and runtime failure are returned as results rather than thrown as client exceptions.
    /// Settings use caller cancellation and the connection lifetime, not the short ordinary response deadline.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API invocation is rejected.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<SettingsResponse> SubmitAsync(SettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests renewal of the joined guest's existing control permission.
    /// </summary>
    /// <remarks>
    /// This request does not grant control permission that has not been approved or has been revoked.
    /// </remarks>
    /// <returns>The acknowledgement that existing control permission is active.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, control is not allowed, or the API connection or request fails.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> RenewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests runtime Stop for the joined guest without ending the session.
    /// </summary>
    /// <remarks>
    /// Guest Stop does not itself revoke existing control permission.
    /// </remarks>
    /// <returns>The accepted stop response.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retracts once and stops for the joined, permitted guest without resuming settings.
    /// </summary>
    /// <remarks>
    /// Waits without the short response deadline. Use <see cref="StopAsync"/> to interrupt it;
    /// cancelling the response wait does not cancel a dispatched server operation.
    /// </remarks>
    /// <returns>The completed position-reset response.</returns>
    /// <exception cref="ApiClientException">The guest lacks permission, the service rejects the request, or the operation fails.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the response wait.</exception>
    Task<ControlResponse> ResetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the joined guest session and requests runtime Stop.
    /// </summary>
    /// <remarks>
    /// Leaving does not release the RPC connection; dispose the client when it is no longer needed.
    /// </remarks>
    /// <returns>The accepted session-ending response.</returns>
    /// <exception cref="ApiClientException">The guest has not joined, the connection is unavailable, or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">Connection startup or the request is cancelled.</exception>
    Task<ControlResponse> LeaveAsync(CancellationToken cancellationToken = default);
}
