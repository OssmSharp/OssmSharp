using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Clients;

/// <summary>Provides one user-authorized v2 control-service connection.</summary>
/// <param name="factory">The factory for authenticated hub connections.</param>
/// <param name="options">The validated control client settings.</param>
public sealed class UserControlClient(ControlHubConnectionFactory factory, Microsoft.Extensions.Options.IOptions<ControlClientOptions> options)
    : IAsyncDisposable
{
    private readonly ControlHubClient _connection = new(factory, options);
    private readonly object _startSync = new();
    private Task? _startTask;

    /// <summary>Gets the current user-visible session state.</summary>
    public async Task<UserSessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _connection.InvokeAsync<UserSessionState>("GetUserState", [], cancellationToken);
    }

    /// <summary>Re-arms if needed, then allows the admitted guest to submit settings until revoked.</summary>
    public Task<ControlResponse> AllowGuestAsync(CancellationToken cancellationToken = default) =>
        InvokeCommandAsync("AllowGuest", cancellationToken);

    /// <summary>Stops motion without requiring stale user state.</summary>
    /// <returns>The acknowledgement for stopping motion.</returns>
    public Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default) =>
        InvokeCommandAsync("Stop", cancellationToken);

    /// <summary>Ends the current guest session and requests Stop.</summary>
    public Task<ControlResponse> EndGuestAsync(CancellationToken cancellationToken = default) =>
        InvokeCommandAsync("EndGuest", cancellationToken);

    /// <summary>Connects the configured runtime device.</summary>
    /// <remarks>
    /// Waits for device setup without the short command response deadline. Cancelling the wait does not
    /// cancel server-side setup; use <see cref="StopAsync"/> to interrupt a pending connection.
    /// </remarks>
    public Task<ControlResponse> ConnectAsync(CancellationToken cancellationToken = default) =>
        InvokeCommandAsync("Connect", cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    private async Task<ControlResponse> InvokeCommandAsync(string method, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        var response = await _connection.InvokeAsync<ControlResponse>(method, [NewCorrelation()], cancellationToken);
        if (!response.Accepted)
            throw new ControlClientException(response.Code);
        return response;
    }

    private Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_startSync)
        {
            return _startTask ??= _connection.StartAsync(cancellationToken);
        }
    }
}
