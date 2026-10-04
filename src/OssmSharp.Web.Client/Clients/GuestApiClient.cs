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

    /// <inheritdoc />
    public async Task<GuestSessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await WaitForStartAsync();
        return await _connection.InvokeAsync<GuestSessionState>("GetGuestState", [], cancellationToken);
    }

    /// <inheritdoc />
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
            return await InvokeRequestAsync("Join", [new GuestJoinRequest(NewCorrelation(), invitationToken)], cancellationToken);
        }
        catch
        {
            await _connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SettingsResponse> SubmitAsync(SettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await WaitForStartAsync();
        return await _connection.InvokeAsync<SettingsResponse>("Submit", [request], cancellationToken);
    }

    /// <inheritdoc />
    public Task<ControlResponse> RenewAsync(CancellationToken cancellationToken = default) =>
        InvokeRequestAsync("Renew", [NewCorrelation()], cancellationToken);

    /// <inheritdoc />
    public Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default) =>
        InvokeRequestAsync("GuestStop", [NewCorrelation()], cancellationToken);

    /// <inheritdoc />
    public Task<ControlResponse> LeaveAsync(CancellationToken cancellationToken = default) =>
        InvokeRequestAsync("Leave", [NewCorrelation()], cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    private async Task<ControlResponse> InvokeRequestAsync(
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
