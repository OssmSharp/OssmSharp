using Microsoft.AspNetCore.SignalR.Client;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient;

/// <summary>Provides one operator-authorized, callback-driven control-service connection.</summary>
public sealed class OperatorControlClient(ControlHubConnectionFactory factory, Microsoft.Extensions.Options.IOptions<ControlClientOptions> options)
    : IAsyncDisposable
{
    private readonly ControlHubClient _connection = new(factory, options);
    private OperatorControlCallbacks? _callbacks;
    private int _subscriptionFailed;

    /// <summary>Connects, registers the only callback set, and requests the initial operator state and activity cursor.</summary>
    public async Task SubscribeAsync(OperatorControlCallbacks callbacks, long afterSequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (Interlocked.CompareExchange(ref _callbacks, callbacks, null) is not null)
            throw new InvalidOperationException("Operator callbacks may only be registered once before connecting.");

        await _connection.StartAsync(RegisterCallbacks, ClosedAsync, cancellationToken);
        try
        {
            await InvokeAsync("SubscribeOperator", [NewCorrelation(), afterSequence], cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref _subscriptionFailed, 1);
            await _connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Cycles the invitation using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> CycleAsync(AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("Cycle", [expected, NewCorrelation()], cancellationToken);

    /// <summary>Approves the admitted guest using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> ApproveAsync(ApproveGuestRequest request, AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("Approve", [request, expected, NewCorrelation()], cancellationToken);

    /// <summary>Applies limits using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> SetLimitsAsync(ControlLimits limits, AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("SetLimits", [limits, expected, NewCorrelation()], cancellationToken);

    /// <summary>Recovers the controller using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> RecoverAsync(AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("Recover", [expected, NewCorrelation()], cancellationToken);

    /// <summary>Arms the controller using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> ArmAsync(AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("Arm", [expected, NewCorrelation()], cancellationToken);

    /// <summary>Recovers, approves, and arms the expected guest authority in one service operation.</summary>
    public Task<ControlAcknowledgement> AllowControlAsync(AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("AllowControl", [expected, NewCorrelation()], cancellationToken);

    /// <summary>Stops motion without requiring stale operator state.</summary>
    public Task<ControlAcknowledgement> StopAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync("Stop", [NewCorrelation()], cancellationToken);

    /// <summary>Ends the expected session using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> EndAsync(AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("End", [expected, NewCorrelation()], cancellationToken);

    /// <summary>Unlocks the expected lock revision using the current trusted operator precondition.</summary>
    public Task<ControlAcknowledgement> UnlockAsync(UnlockControlRequest request, AuthorityPrecondition expected, CancellationToken cancellationToken = default) =>
        InvokeAsync("Unlock", [request, expected, NewCorrelation()], cancellationToken);

    /// <summary>Requests an approved simulator fault operation.</summary>
    public Task<ControlAcknowledgement> InjectSimulatorFaultAsync(SimulatorFaultRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync("SimulatorFault", [request, NewCorrelation()], cancellationToken);

    private void RegisterCallbacks(HubConnection connection)
    {
        connection.On<OperatorLiveControlStatus>("operatorState", status =>
            _connection.DispatchAsync(() => _callbacks!.StateReceived(status)));
        connection.On<ControlActivityBatch>("operatorLog", batch =>
            _connection.DispatchAsync(() => _callbacks!.ActivityReceived(batch)));
        connection.On<ControlResyncRequired>("resyncRequired", resync =>
            _connection.DispatchAsync(() => _callbacks!.ResyncRequired(resync)));
    }

    private Task<ControlAcknowledgement> InvokeAsync(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _subscriptionFailed) != 0)
            return Task.FromException<ControlAcknowledgement>(new ControlClientException("connection-unavailable"));

        return _connection.InvokeAsync<ControlAcknowledgement>(method, arguments, cancellationToken);
    }

    private Task ClosedAsync() => _callbacks!.ResyncRequired(new("connection-closed"));

    private static string NewCorrelation() => Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
