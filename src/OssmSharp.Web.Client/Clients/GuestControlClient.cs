using OssmSharp.Web.Client.Connections;
using OssmSharp.Web.Client.Models;
using OssmSharp.Web.Contracts;

namespace OssmSharp.Web.Client.Clients;

/// <summary>Provides one guest-authorized, circuit-scoped control-service connection.</summary>
/// <param name="factory">The factory for authenticated hub connections.</param>
/// <param name="options">The validated control client settings.</param>
public sealed class GuestControlClient(ControlHubConnectionFactory factory, Microsoft.Extensions.Options.IOptions<ControlClientOptions> options)
    : IAsyncDisposable
{
    private readonly ControlHubClient _connection = new(factory, options);
    private readonly object _startSync = new();
    private Task? _startTask;

    /// <summary>Gets the current guest-visible session state.</summary>
    public async Task<GuestSessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _connection.InvokeAsync<GuestSessionState>("GetGuestState", [], cancellationToken);
    }

    /// <summary>Redeems one invitation for this connection.</summary>
    public async Task<ControlResponse> JoinAsync(string invitationToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationToken);
        await EnsureStartedAsync(cancellationToken);
        return await InvokeControlAsync("Join", [new GuestJoinRequest(NewCorrelation(), invitationToken)], cancellationToken);
    }

    /// <summary>Submits one complete settings request.</summary>
    public async Task<SettingsResponse> SubmitAsync(SettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStartedAsync(cancellationToken);
        return await _connection.InvokeAsync<SettingsResponse>("Submit", [request], cancellationToken);
    }

    /// <summary>Renews the current guest permission.</summary>
    public Task<ControlResponse> RenewAsync(CancellationToken cancellationToken = default) =>
        InvokeControlAsync("Renew", [NewCorrelation()], cancellationToken);

    /// <summary>Requests Stop for the joined guest without exposing its proof.</summary>
    /// <returns>The acknowledgement for stopping guest motion.</returns>
    public Task<ControlResponse> StopAsync(CancellationToken cancellationToken = default) =>
        InvokeControlAsync("GuestStop", [NewCorrelation()], cancellationToken);

    /// <summary>Ends the joined guest session without cycling the invitation.</summary>
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
        await EnsureStartedAsync(cancellationToken);
        var response = await _connection.InvokeAsync<ControlResponse>(method, arguments, cancellationToken);
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
