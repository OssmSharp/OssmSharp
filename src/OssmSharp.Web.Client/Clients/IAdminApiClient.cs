using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Clients;

/// <summary>
/// Provides administrator-authorized API requests for device and guest-session control.
/// </summary>
/// <remarks>
/// The first request starts the RPC connection. Cancellation can interrupt connection startup or the caller's
/// wait for a response; it does not roll back a dispatched request. Dispose the client when it is no longer
/// needed. Disposal cancels pending client waits, drains them, and releases the RPC connection.
/// </remarks>
public interface IAdminApiClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the current invitation, guest permissions, and device state.
    /// </summary>
    /// <returns>The current user-visible session state.</returns>
    /// <exception cref="ApiClientException">The API connection is unavailable or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires before a response is received.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels connection startup or the wait for a response.</exception>
    Task<UserSessionState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shares preview-only rail and attachment lengths with the guest without changing device settings.
    /// </summary>
    /// <param name="dimensions">Lengths in centimetres: rail from 1 to 500 and attachment from 0 to 500.</param>
    /// <param name="cancellationToken" />
    /// <returns>The accepted preview update.</returns>
    /// <remarks>
    /// Cancellation ends startup or the response wait; it does not roll back a dispatched update.
    /// </remarks>
    /// <exception cref="ApiClientException">The API is unavailable or rejects the dimensions.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The update response deadline expires.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels startup or the response wait.</exception>
    Task<ControlResponse> SetPreviewDimensionsAsync(PreviewDimensions dimensions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-arms the device if needed and allows the admitted guest to submit settings until permission is revoked.
    /// </summary>
    /// <returns>The acknowledgement that guest control was allowed.</returns>
    /// <exception cref="ApiClientException">The API connection is unavailable or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels connection startup or the wait for a response.</exception>
    Task<ControlResponse> AllowGuestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes guest control permission and requests runtime Stop.
    /// </summary>
    /// <remarks>
    /// This request can run while <see cref="ConnectAsync"/> is pending.
    /// </remarks>
    /// <returns>The accepted stop response.</returns>
    /// <exception cref="ApiClientException">The API connection is unavailable or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels connection startup or the wait for a response.</exception>
    Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retracts once and stops without resuming settings or granting guest permission.
    /// </summary>
    /// <remarks>
    /// Waits without the short response deadline. Use <see cref="StopAsync"/> to interrupt it;
    /// cancelling the response wait does not cancel a dispatched server operation.
    /// </remarks>
    /// <returns>The completed position-reset response.</returns>
    /// <exception cref="ApiClientException">The service rejects the request or the operation fails.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the response wait.</exception>
    Task<ControlResponse> ResetPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the current guest session and requests runtime Stop.
    /// </summary>
    /// <returns>The accepted session-ending response.</returns>
    /// <exception cref="ApiClientException">The API connection is unavailable or the API rejects the request.</exception>
    /// <exception cref="RpcClientRequestTimeoutException">The response deadline expires; request acceptance is unknown.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels connection startup or the wait for a response.</exception>
    Task<ControlResponse> EndGuestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects the configured runtime device.
    /// </summary>
    /// <remarks>
    /// Waits for device setup without the short request response deadline. Cancelling the wait does not
    /// cancel server-side setup; use <see cref="StopAsync"/> to interrupt a pending device connection.
    /// </remarks>
    /// <returns>The accepted device-connection response.</returns>
    /// <exception cref="ApiClientException">The API connection is unavailable or the API rejects the request.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels connection startup or the wait for a response.</exception>
    Task<ControlResponse> ConnectAsync(CancellationToken cancellationToken = default);
}
