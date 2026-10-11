namespace OssmSharp.Web.Client.Connections;

/// <summary>
/// Owns one authenticated RPC connection and its requests.
/// </summary>
/// <remarks>
/// Start the connection before invoking requests. Disposal cancels startup, queued requests, and response waits,
/// then drains active operations and closes the connection. It does not roll back dispatched server work.
/// </remarks>
public interface IRpcClient : IAsyncDisposable
{
    /// <summary>
    /// Starts the connection using service credentials or a guest invitation.
    /// </summary>
    /// <param name="invitationToken">The guest invitation, or <see langword="null"/> to use service credentials.</param>
    /// <returns>Completion of connection startup.</returns>
    Task StartAsync(CancellationToken cancellationToken, string? invitationToken = null);

    /// <summary>
    /// Invokes an RPC method on the started connection.
    /// </summary>
    /// <typeparam name="T">The response type.</typeparam>
    /// <param name="method">The server method name.</param>
    /// <param name="arguments">The method arguments.</param>
    /// <returns>The server response.</returns>
    Task<T> InvokeAsync<T>(string method, object?[] arguments, CancellationToken cancellationToken);
}
