using OssmSharp.Web.Api.Models;
using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Api.SignalR;

/// <summary>
/// Tracks staged hub subscriptions and server-only guest admissions by SignalR connection.
/// </summary>
public sealed class ControlHubConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HubGuestSession> _guests = [];
    private readonly HashSet<string> _guestStages = [];
    private readonly Dictionary<string, HubSubscription> _subscriptions = [];

    /// <summary>
    /// Marks a connection as eligible for a later guest-session binding.
    /// </summary>
    /// <param name="connectionId">The server-owned SignalR connection identifier.</param>
    internal void StageGuest(string connectionId) { lock (_gate) _guestStages.Add(connectionId); }
    /// <summary>
    /// Starts an operator subscription at the supplied activity and state cursors.
    /// </summary>
    /// <param name="connectionId">The authenticated operator connection.</param>
    /// <param name="activityCursor">The last activity sequence seen by the operator.</param>
    /// <param name="stateRevision">The state revision already seen by the operator.</param>
    internal void SubscribeOperator(string connectionId, long activityCursor, long stateRevision)
    {
        lock (_gate)
            _subscriptions[connectionId] = new(HubSubscriptionRole.Operator, activityCursor, stateRevision);
    }
    /// <summary>
    /// Starts a guest subscription after the supplied state version.
    /// </summary>
    /// <param name="connectionId">The staged guest connection.</param>
    /// <param name="stateVersion">The state version already seen by the guest.</param>
    internal void SubscribeGuest(string connectionId, long stateVersion)
    {
        lock (_gate)
            _subscriptions[connectionId] = new(HubSubscriptionRole.Guest, 0, stateVersion);
    }
    /// <summary>
    /// Checks whether a connection has completed guest staging.
    /// </summary>
    /// <param name="connectionId">The guest connection to inspect.</param>
    /// <returns>Whether the connection is staged for guest binding.</returns>
    internal bool IsGuestStaged(string connectionId) { lock (_gate) return _guestStages.Contains(connectionId); }
    /// <summary>
    /// Binds a guest session only when the connection was staged and is not already bound.
    /// </summary>
    /// <param name="connectionId">The staged guest connection.</param>
    /// <param name="session">The server-owned guest admission to bind.</param>
    /// <returns>Whether this connection accepted the guest binding.</returns>
    internal bool TryBindGuest(string connectionId, HubGuestSession session)
    {
        lock (_gate)
            return _guestStages.Contains(connectionId) && _guests.TryAdd(connectionId, session);
    }
    /// <summary>
    /// Retrieves a guest session bound to a SignalR connection.
    /// </summary>
    /// <param name="connectionId">The connection whose admission is requested.</param>
    /// <param name="session">The bound guest admission, if one exists.</param>
    /// <returns>Whether the connection has a bound guest session.</returns>
    internal bool TryGetGuest(string connectionId, out HubGuestSession? session)
    {
        lock (_gate) return _guests.TryGetValue(connectionId, out session);
    }
    /// <summary>
    /// Snapshots the currently subscribed operator connection identifiers.
    /// </summary>
    /// <returns>Connection identifiers, detached from subsequent registry changes.</returns>
    internal string[] OperatorConnections()
    {
        lock (_gate)
            return _subscriptions.Where(pair => pair.Value.Role == HubSubscriptionRole.Operator).Select(pair => pair.Key).ToArray();
    }
    /// <summary>
    /// Snapshots the server-authorized guest bindings for live status projection.
    /// </summary>
    /// <returns>Connection and guest-session pairs detached from subsequent registry changes.</returns>
    internal (string ConnectionId, HubGuestSession Session)[] GuestConnections() { lock (_gate) return _guests.Select(pair => (pair.Key, pair.Value)).ToArray(); }
    /// <summary>
    /// Snapshots subscriptions with their SignalR connection identifiers.
    /// </summary>
    /// <returns>Subscriptions detached from subsequent registry changes.</returns>
    internal HubSubscription[] Subscriptions() { lock (_gate) return _subscriptions.Select(pair => pair.Value with { ConnectionId = pair.Key }).ToArray(); }
    /// <summary>
    /// Queues a newer operator projection, retaining terminal transitions ahead of ordinary updates.
    /// </summary>
    /// <param name="connectionId">The operator connection to update.</param>
    /// <param name="state">The operator state to queue.</param>
    /// <returns>Whether the update was queued, ignored, or exceeded the terminal slot.</returns>
    internal QueueState QueueOperatorState(string connectionId, OperatorLiveControlStatus state) =>
        QueueStateCore(connectionId, "operatorState", state, state.Authority.SnapshotRevision,
            IsTerminal(state.Authority.Snapshot.Mode, state.Authority.Snapshot.SessionMode));
    /// <summary>
    /// Queues a changed guest projection, ignoring unchanged content even if its revision advances.
    /// </summary>
    /// <param name="connectionId">The admitted guest connection to update.</param>
    /// <param name="state">The guest-safe state to queue.</param>
    /// <returns>Whether the update was queued, ignored, or exceeded the terminal slot.</returns>
    internal QueueState QueueGuestState(string connectionId, GuestStatus state)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(connectionId, out var subscription) ||
                (subscription.LastQueuedGuestState is { } previous && SameGuestProjection(state, previous)))
                return QueueState.Ignored;

            var queued = QueueStateCore(connectionId, "guestState", state, state.SnapshotRevision,
                IsTerminal(state.Mode, state.SessionMode));
            if (queued == QueueState.Queued)
                _subscriptions[connectionId] = _subscriptions[connectionId] with { LastQueuedGuestState = state };
            return queued;
        }
    }
    /// <summary>
    /// Marks an active subscription for resynchronization before another delivery.
    /// </summary>
    /// <param name="connectionId">The subscription requiring a fresh snapshot.</param>
    internal void RequireResync(string connectionId) { lock (_gate) if (_subscriptions.TryGetValue(connectionId, out var subscription)) _subscriptions[connectionId] = subscription with { ResyncRequired = true }; }
    /// <summary>
    /// Snapshots the connections awaiting resynchronization.
    /// </summary>
    /// <returns>Connection identifiers requiring a resubscription notice.</returns>
    internal string[] TakeResyncConnections()
    {
        lock (_gate)
            return _subscriptions.Where(pair => pair.Value.ResyncRequired).Select(pair => pair.Key).ToArray();
    }
    /// <summary>
    /// Takes a terminal callback before a coalesced ordinary update unless resync is required.
    /// </summary>
    /// <param name="connectionId">The subscriber awaiting an update.</param>
    /// <param name="pending">The callback taken for delivery, if available.</param>
    /// <returns>Whether a pending callback was removed for delivery.</returns>
    internal bool TryTakePending(string connectionId, out HubPendingPush? pending)
    {
        lock (_gate)
        {
            pending = null;
            if (!_subscriptions.TryGetValue(connectionId, out var subscription) || subscription.ResyncRequired)
                return false;
            pending = subscription.Terminal ?? subscription.Latest;
            if (pending is null)
                return false;
            _subscriptions[connectionId] = subscription with
            {
                Terminal = subscription.Terminal is not null ? null : subscription.Terminal,
                Latest = subscription.Terminal is null ? null : subscription.Latest
            };
            return true;
        }
    }
    /// <summary>
    /// Advances the delivered state version after a callback succeeds, without moving it backward.
    /// </summary>
    /// <param name="connectionId">The subscriber whose delivery succeeded.</param>
    /// <param name="version">The state version delivered to that subscriber.</param>
    internal void MarkStateDelivered(string connectionId, long version)
    {
        lock (_gate)
            if (_subscriptions.TryGetValue(connectionId, out var subscription))
                _subscriptions[connectionId] = subscription with { LastDeliveredStateVersion = Math.Max(subscription.LastDeliveredStateVersion, version) };
    }
    /// <summary>
    /// Advances the operator activity cursor after a callback succeeds, without moving it backward.
    /// </summary>
    /// <param name="connectionId">The operator subscriber whose delivery succeeded.</param>
    /// <param name="sequence">The last activity sequence delivered.</param>
    internal void AdvanceActivityCursor(string connectionId, long sequence)
    {
        lock (_gate)
            if (_subscriptions.TryGetValue(connectionId, out var subscription))
                _subscriptions[connectionId] = subscription with { ActivityCursor = Math.Max(subscription.ActivityCursor, sequence) };
    }
    /// <summary>
    /// Removes a connection's admission and subscription state.
    /// </summary>
    /// <param name="connectionId">The departing SignalR connection.</param>
    /// <returns>The bound guest session, if one must be notified of transport loss.</returns>
    internal HubGuestSession? Remove(string connectionId)
    {
        lock (_gate)
        {
            _guestStages.Remove(connectionId);
            _subscriptions.Remove(connectionId);
            return _guests.Remove(connectionId, out var session) ? session : null;
        }
    }

    private QueueState QueueStateCore(string connectionId, string method, object payload, long version, bool terminal)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(connectionId, out var subscription) || version <= subscription.LastDeliveredStateVersion)
                return QueueState.Ignored;

            var pending = new HubPendingPush(method, payload, version);
            if (terminal)
            {
                if (subscription.Terminal is { } existing && existing.Version != version)
                    return QueueState.Saturated;
                _subscriptions[connectionId] = subscription with { Terminal = pending, Latest = null };
                return QueueState.Queued;
            }

            if (subscription.Terminal is { Version: var terminalVersion } && version <= terminalVersion)
                return QueueState.Ignored;
            if (subscription.Latest is { Version: var existingVersion } && existingVersion > version)
                return QueueState.Ignored;
            _subscriptions[connectionId] = subscription with { Latest = pending };
            return QueueState.Queued;
        }
    }

    private static bool IsTerminal(string mode, string sessionMode) =>
        mode is "Locked" or "Faulted" || sessionMode == "Ended";

    private static bool SameGuestProjection(GuestStatus state, GuestStatus previous) =>
        state with { SnapshotRevision = previous.SnapshotRevision } == previous;
}
