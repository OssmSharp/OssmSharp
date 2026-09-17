using Microsoft.AspNetCore.SignalR.Client;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient;

/// <summary>Provides one guest-authorized, circuit-scoped control-service connection.</summary>
public sealed class GuestControlClient(ControlHubConnectionFactory factory, Microsoft.Extensions.Options.IOptions<ControlClientOptions> options)
    : IAsyncDisposable
{
    private readonly ControlHubClient _connection = new(factory, options);
    private GuestControlCallbacks? _callbacks;
    private int _joined;

    /// <summary>Connects, stages a guest subscription, and atomically redeems one invitation.</summary>
    public async Task<GuestJoinReceipt> JoinAsync(GuestControlCallbacks callbacks, GuestJoinRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _callbacks, callbacks, null) is not null)
            throw new InvalidOperationException("Guest callbacks may only be registered once before connecting.");

        await _connection.StartAsync(RegisterCallbacks, ClosedAsync, cancellationToken);
        await InvokeAsync("SubscribeGuest", [request.Correlation], cancellationToken);
        try
        {
            var receipt = await _connection.InvokeAsync<GuestJoinReceipt>("Join", [request], cancellationToken);
            Interlocked.Exchange(ref _joined, 1);
            return receipt;
        }
        catch
        {
            await _connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Submits one bounded motion input for the joined guest.</summary>
    public Task<ControlAcknowledgement> SubmitAsync(GuestInputRequest request, CancellationToken cancellationToken = default) =>
        RequireJoinedAsync("Submit", [request], cancellationToken);

    /// <summary>Requests Stop for the joined guest without exposing its proof.</summary>
    public Task<ControlAcknowledgement> StopAsync(CancellationToken cancellationToken = default) =>
        RequireJoinedAsync("GuestStop", [NewCorrelation()], cancellationToken);

    /// <summary>Ends the joined guest session without cycling the invitation.</summary>
    public async Task<ControlAcknowledgement> LeaveAsync(CancellationToken cancellationToken = default)
    {
        var acknowledgement = await RequireJoinedAsync("Leave", [NewCorrelation()], cancellationToken);
        Interlocked.Exchange(ref _joined, 0);
        return acknowledgement;
    }

    private void RegisterCallbacks(HubConnection connection)
    {
        connection.On<GuestStatus>("guestState", status =>
            _connection.DispatchAsync(() => _callbacks!.StateReceived(status)));
        connection.On<ControlResyncRequired>("resyncRequired", resync =>
            _connection.DispatchAsync(() => _callbacks!.ResyncRequired(resync)));
    }

    private Task<ControlAcknowledgement> RequireJoinedAsync(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _joined) == 0)
            return Task.FromException<ControlAcknowledgement>(new ControlClientException("guest-not-joined"));
        return _connection.InvokeAsync<ControlAcknowledgement>(method, arguments, cancellationToken);
    }

    private Task<ControlAcknowledgement> InvokeAsync(string method, object?[] arguments, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<ControlAcknowledgement>(method, arguments, cancellationToken);

    private Task ClosedAsync()
    {
        Interlocked.Exchange(ref _joined, 0);
        return _callbacks!.ResyncRequired(new("connection-closed"));
    }

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _joined, 0);
        return _connection.DisposeAsync();
    }
}
