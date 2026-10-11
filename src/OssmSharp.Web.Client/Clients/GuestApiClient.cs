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
    private string? _credential;
    private bool _sessionAuthentication;

    /// <inheritdoc />
    public async Task<GuestSessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await WaitForStartAsync();
        return await _connection.InvokeAsync<GuestSessionState>("GetGuestState", [], cancellationToken)
            ?? throw new InvalidDataException("The API returned a missing guest state.");
    }

    /// <inheritdoc />
    public async Task<GuestRedemptionResponse> RedeemAsync(string invitationToken, CancellationToken cancellationToken = default)
    {
        try
        {
            await StartOnceAsync(invitationToken, session: false, cancellationToken);
            var response = await _connection.InvokeAsync<GuestRedemptionResponse>(
                "Redeem", [new GuestJoinRequest(NewCorrelation(), invitationToken)], cancellationToken)
                ?? throw new InvalidDataException("The API returned a missing invitation redemption result.");
            if (!response.Accepted)
                throw new ApiClientException(response.Code);
            if (response.SessionToken is not { Length: >= 32 } || !ValidToken(response.SessionToken) || response.ExpiresAt is null)
                throw new InvalidDataException("The API returned an unusable saved-session credential.");
            return response;
        }
        catch
        {
            await _connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ControlResponse> ResumeAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        try
        {
            await StartOnceAsync(sessionToken, session: true, cancellationToken);
            return await InvokeRequestAsync("Join", [NewCorrelation()], cancellationToken);
        }
        catch
        {
            await _connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SavedGuestSession> GetSavedSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        await StartOnceAsync(sessionToken, session: true, cancellationToken);
        var session = await _connection.InvokeAsync<SavedGuestSession?>("GetSavedSession", [], cancellationToken)
            ?? throw new ApiClientException("invalid-guest-session");
        if (session.ExpiresAt == default || (session.Connected && session.CanAttach))
            throw new InvalidDataException("The API returned incomplete or contradictory saved-session metadata.");
        return session;
    }

    /// <inheritdoc />
    public async Task<ControlResponse> LeaveSavedSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        await StartOnceAsync(sessionToken, session: true, cancellationToken);
        return await InvokeRequestAsync("LeaveSavedSession", [NewCorrelation()], cancellationToken);
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
    public Task<ControlResponse> ResetPositionAsync(CancellationToken cancellationToken = default) =>
        InvokeRequestAsync("GuestResetPosition", [NewCorrelation()], cancellationToken);

    /// <inheritdoc />
    public Task<ControlResponse> LeaveAsync(CancellationToken cancellationToken = default) =>
        InvokeRequestAsync("Leave", [NewCorrelation()], cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    private static bool ValidToken(string token) =>
        token.Length is > 0 and <= 128
        && !token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'));

    private Task StartOnceAsync(string token, bool session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!ValidToken(token))
            throw new ApiClientException(session ? "invalid-guest-session" : "invalid-invitation");
        lock (_startSync)
        {
            if (_startTask is not null && (_sessionAuthentication != session || !string.Equals(_credential, token, StringComparison.Ordinal)))
                throw new InvalidOperationException("Use a new client for a different authenticated identity.");
            _credential = token;
            _sessionAuthentication = session;
            return _startTask ??= session
                ? _connection.StartSessionAsync(token, cancellationToken)
                : _connection.StartAsync(cancellationToken, token);
        }
    }

    private async Task<ControlResponse> InvokeRequestAsync(
        string method,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        await WaitForStartAsync();
        var response = await _connection.InvokeAsync<ControlResponse>(method, arguments, cancellationToken)
            ?? throw new ApiClientException("invalid-response");
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
