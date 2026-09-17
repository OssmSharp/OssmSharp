using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService;

/// <summary>Tracks staged hub subscriptions and server-only guest admissions by SignalR connection.</summary>
public sealed class ControlHubConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HubGuestSession> _guests = [];
    private readonly HashSet<string> _guestStages = [];
    private readonly Dictionary<string, HubSubscription> _subscriptions = [];

    internal void StageGuest(string connectionId) { lock (_gate) _guestStages.Add(connectionId); }
    internal void SubscribeOperator(string connectionId, long activityCursor, long stateRevision)
    {
        lock (_gate)
            _subscriptions[connectionId] = new(HubSubscriptionRole.Operator, activityCursor, stateRevision);
    }
    internal void SubscribeGuest(string connectionId, long stateVersion)
    {
        lock (_gate)
            _subscriptions[connectionId] = new(HubSubscriptionRole.Guest, 0, stateVersion);
    }
    internal bool IsGuestStaged(string connectionId) { lock (_gate) return _guestStages.Contains(connectionId); }
    internal bool TryBindGuest(string connectionId, HubGuestSession session)
    {
        lock (_gate)
            return _guestStages.Contains(connectionId) && _guests.TryAdd(connectionId, session);
    }
    internal bool TryGetGuest(string connectionId, out HubGuestSession? session)
    {
        lock (_gate) return _guests.TryGetValue(connectionId, out session);
    }
    internal string[] OperatorConnections()
    {
        lock (_gate)
            return _subscriptions.Where(pair => pair.Value.Role == HubSubscriptionRole.Operator).Select(pair => pair.Key).ToArray();
    }
    internal (string ConnectionId, HubGuestSession Session)[] GuestConnections() { lock (_gate) return _guests.Select(pair => (pair.Key, pair.Value)).ToArray(); }
    internal HubSubscription[] Subscriptions() { lock (_gate) return _subscriptions.Select(pair => pair.Value with { ConnectionId = pair.Key }).ToArray(); }
    internal QueueState QueueOperatorState(string connectionId, OperatorLiveControlStatus state) =>
        QueueStateCore(connectionId, "operatorState", state, state.Authority.SnapshotRevision,
            IsTerminal(state.Authority.Snapshot.Mode, state.Authority.Snapshot.SessionMode));
    internal QueueState QueueGuestState(string connectionId, GuestStatus state) =>
        QueueStateCore(connectionId, "guestState", state, state.SnapshotRevision, IsTerminal(state.Mode, state.SessionMode));
    internal void RequireResync(string connectionId) { lock (_gate) if (_subscriptions.TryGetValue(connectionId, out var subscription)) _subscriptions[connectionId] = subscription with { ResyncRequired = true }; }
    internal string[] TakeResyncConnections()
    {
        lock (_gate)
            return _subscriptions.Where(pair => pair.Value.ResyncRequired).Select(pair => pair.Key).ToArray();
    }
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
    internal void MarkStateDelivered(string connectionId, long version)
    {
        lock (_gate)
            if (_subscriptions.TryGetValue(connectionId, out var subscription))
                _subscriptions[connectionId] = subscription with { LastDeliveredStateVersion = Math.Max(subscription.LastDeliveredStateVersion, version) };
    }
    internal void AdvanceActivityCursor(string connectionId, long sequence)
    {
        lock (_gate)
            if (_subscriptions.TryGetValue(connectionId, out var subscription))
                _subscriptions[connectionId] = subscription with { ActivityCursor = Math.Max(subscription.ActivityCursor, sequence) };
    }
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
}

internal enum HubSubscriptionRole { Operator, Guest }
internal enum QueueState { Ignored, Queued, Saturated }
internal sealed record HubPendingPush(string Method, object Payload, long Version);
internal sealed record HubSubscription(HubSubscriptionRole Role, long ActivityCursor, long LastDeliveredStateVersion)
{
    internal string ConnectionId { get; init; } = string.Empty;
    internal HubPendingPush? Latest { get; init; }
    internal HubPendingPush? Terminal { get; init; }
    internal bool ResyncRequired { get; init; }
}
