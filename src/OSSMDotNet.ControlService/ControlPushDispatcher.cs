using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Publishes coalesced live state from its own hosted loop, never from authority or device-bridge locks.</summary>
public sealed class ControlPushDispatcher(
    IControlOperations operations,
    GuestControlOperations guests,
    ControlHubConnectionRegistry connections,
    ControlActivityProvider activity,
    IControlHubPushSink sink,
    ILogger<ControlPushDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Requests a later dispatch without waiting for serialization, authority, or network I/O.</summary>
    public void Trigger()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Runs one projection and delivery pass. This is public to permit deterministic host-free tests.</summary>
    public async Task DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        StageFreshProjections();

        foreach (var connectionId in connections.TakeResyncConnections())
            await SendResyncAndRemoveAsync(connectionId, cancellationToken);

        foreach (var subscription in connections.Subscriptions())
        {
            if (connections.TryTakePending(subscription.ConnectionId, out var pending) && pending is not null)
            {
                if (!await TrySendAsync(subscription.ConnectionId, pending.Method, pending.Payload, reportFailure: true, cancellationToken))
                    await SendResyncAndRemoveAsync(subscription.ConnectionId, cancellationToken);
                else
                    connections.MarkStateDelivered(subscription.ConnectionId, pending.Version);
                continue;
            }

            if (subscription.Role != HubSubscriptionRole.Operator)
                continue;

            var batch = activity.ReadBatchAfter(subscription.ActivityCursor);
            if (batch.Entries.Count == 0 && !batch.Gap)
                continue;

            if (!await TrySendAsync(subscription.ConnectionId, "operatorLog", batch, reportFailure: true, cancellationToken))
                await SendResyncAndRemoveAsync(subscription.ConnectionId, cancellationToken);
            else
                connections.AdvanceActivityCursor(subscription.ConnectionId, batch.LastSequence);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(DispatchInterval);
            try
            {
                await _wake.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The interval elapsed; take a fresh projection even if nobody explicitly triggered it.
            }
            await DispatchOnceAsync(stoppingToken);
        }
    }

    private void StageFreshProjections()
    {
        var operatorStatus = operations.ReadOperatorStatus();
        foreach (var connectionId in connections.OperatorConnections())
        {
            if (connections.QueueOperatorState(connectionId, operatorStatus) == QueueState.Saturated)
                connections.RequireResync(connectionId);
        }

        foreach (var (connectionId, _) in connections.GuestConnections())
        {
            try
            {
                var guestStatus = guests.Status(connectionId);
                if (connections.QueueGuestState(connectionId, guestStatus) == QueueState.Saturated)
                    connections.RequireResync(connectionId);
            }
            catch (ControlRejectedException)
            {
                connections.RequireResync(connectionId);
            }
        }
    }

    private async Task<bool> TrySendAsync(
        string connectionId,
        string method,
        object payload,
        bool reportFailure,
        CancellationToken stoppingToken)
    {
        try
        {
            ControlHubPayload.EnsureFits(method, payload);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(SendTimeout);
            await sink.SendAsync(connectionId, method, payload, timeout.Token).WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            ReportDeliveryFailure(reportFailure);
            return false;
        }
        catch (HubException) when (!stoppingToken.IsCancellationRequested)
        {
            ReportDeliveryFailure(reportFailure);
            return false;
        }
        catch (IOException) when (!stoppingToken.IsCancellationRequested)
        {
            ReportDeliveryFailure(reportFailure);
            return false;
        }
        catch (InvalidOperationException) when (!stoppingToken.IsCancellationRequested)
        {
            ReportDeliveryFailure(reportFailure);
            return false;
        }
        catch (ArgumentException) when (!stoppingToken.IsCancellationRequested)
        {
            ReportDeliveryFailure(reportFailure);
            return false;
        }
    }

    private async Task SendResyncAndRemoveAsync(string connectionId, CancellationToken cancellationToken)
    {
        guests.TransportLost(connectionId);
        await TrySendAsync(connectionId, "resyncRequired", new ControlResyncRequired("delivery-resync-required"),
            reportFailure: false, stoppingToken: cancellationToken);
    }

    private void ReportDeliveryFailure(bool reportFailure)
    {
        if (reportFailure)
            ControlServiceLog.ClientDeliveryFailed(logger);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
