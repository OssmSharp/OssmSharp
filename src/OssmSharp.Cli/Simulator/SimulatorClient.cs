using System.IO.Pipes;

namespace OssmSharp.Cli.Simulator;

/// <summary>
/// Sends one bounded request to the current user's private simulator host pipe.
/// </summary>
internal static class SimulatorClient
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Sends a request and reads its single response.
    /// </summary>
    /// <param name="endpoint">The private host endpoint.</param>
    /// <param name="request">The request to send.</param>
    /// <returns>The host's response to the request.</returns>
    internal static async Task<SimulatorIpcResponse> SendAsync(
        SimulatorEndpoint endpoint,
        SimulatorIpcRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);

        await using var pipe = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ConnectionTimeout, cancellationToken).ConfigureAwait(false);
        await SimulatorIpcProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await SimulatorIpcProtocol.ReadAsync<SimulatorIpcResponse>(pipe, cancellationToken)
            .ConfigureAwait(false);
    }
}
