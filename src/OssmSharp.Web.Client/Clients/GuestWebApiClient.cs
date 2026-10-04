using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Clients;

/// <summary>
/// Provides one guest-authorized, circuit-scoped control-service connection.
/// </summary>
/// <param name="connection">The RPC connection used for this client's requests.</param>
public sealed class GuestApiClient(IRpcClient connection)
    : IGuestApiClient
{
    private readonly IRpcClient _connection = connection;
    private readonly object _startSync = new();
    private Task? _startTask;

    /// <summary>
    /// Gets the current guest-visible session state.
    /// </summary>
    public async Task<GuestSessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await WaitForStartAsync();
        return await _connection.InvokeAsync<GuestSessionState>("GetGuestState", [], cancellationToken);
    }

    /// <summary>
    /// Joins this connection using a guest invitation.
    /// </summary>
    /// <param name="invitationToken">The invitation token: up to 128 ASCII letters, digits, hyphens, or underscores.</param>
    /// <returns>The join result. Control still needs local approval.</returns>
    /// <exception cref="ApiClientException">The invitation is invalid, the API rejects the join, or the connection fails.</exception>
    /// <exception cref="ArgumentException">The invitation is null, empty, or whitespace.</exception>
    /// <remarks>
    /// This call starts the connection. Connection or API join failures close it, including cancellation.
    /// To rejoin, use a new client and invitation.
    /// </remarks>
    public async Task<ControlResponse> JoinAsync(string invitationToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationToken);
        if (invitationToken.Length > 128
            || invitationToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ApiClientException("invalid-invitation");
        lock (_startSync)
            _startTask ??= _connection.StartAsync(cancellationToken, invitationToken);
        try
        {
            return await InvokeControlAsync("Join", [new GuestJoinRequest(NewCorrelation(), invitationToken)], cancellationToken);
        }
        catch
        {
            await _connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Submits one complete settings request.
    /// </summary>
    public async Task<SettingsResponse> SubmitAsync(SettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await WaitForStartAsync();
        return await _connection.InvokeAsync<SettingsResponse>("Submit", [request], cancellationToken);
    }

    /// <summary>
    /// Renews the current guest permission.
    /// </summary>
    public Task<ControlResponse> RenewAsync(CancellationToken cancellationToken = default) =>
        InvokeControlAsync("Renew", [NewCorrelation()], cancellationToken);

    /// <summary>
    /// Requests Stop for the joined guest without exposing its proof.
    /// </summary>
    /// <returns>The acknowledgement for stopping guest motion.</returns>
    public Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default) =>
        InvokeControlAsync("GuestStop", [NewCorrelation()], cancellationToken);

    /// <summary>
    /// Ends the joined guest session without cycling the invitation.
    /// </summary>
    /// <returns>The acknowledgement for leaving the session.</returns>
    public Task<ControlResponse> LeaveAsync(CancellationToken cancellationToken = default) =>
        InvokeControlAsync("Leave", [NewCorrelation()], cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    private async Task<ControlResponse> InvokeControlAsync(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        await WaitForStartAsync();
        var response = await _connection.InvokeAsync<ControlResponse>(method, arguments, cancellationToken);
        if (!response.Accepted)
            throw new ApiClientException(response.Code);
        return response;
    }

    private Task WaitForStartAsync()
    {
        lock (_startSync)
        {
            return _startTask ?? throw new ApiClientException("guest-not-joined");
        }
    }
}
