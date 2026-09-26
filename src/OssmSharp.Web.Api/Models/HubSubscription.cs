using OssmSharp.Web.Application.Models;

namespace OssmSharp.Web.Api.Models;

/// <summary>
/// Tracks one hub subscriber's delivery cursors and bounded pending callbacks.
/// </summary>
/// <param name="Role">The subscription's authorized audience.</param>
/// <param name="ActivityCursor">The last operator activity sequence delivered.</param>
/// <param name="LastDeliveredStateVersion">The latest state version delivered to the subscriber.</param>
internal sealed record HubSubscription(HubSubscriptionRole Role, long ActivityCursor, long LastDeliveredStateVersion)
{
    /// <summary>
    /// Inits the SignalR connection associated with a snapshot of the subscription.
    /// </summary>
    internal string ConnectionId { get; init; } = string.Empty;
    /// <summary>
    /// Inits the latest ordinary state update, replacing older updates.
    /// </summary>
    internal HubPendingPush? Latest { get; init; }
    /// <summary>
    /// Inits the terminal state update retained ahead of ordinary updates.
    /// </summary>
    internal HubPendingPush? Terminal { get; init; }
    /// <summary>
    /// Inits the guest projection used to suppress unchanged updates.
    /// </summary>
    internal GuestStatus? LastQueuedGuestState { get; init; }
    /// <summary>
    /// Inits whether the connection must resubscribe after a delivery failure or saturated queue.
    /// </summary>
    internal bool ResyncRequired { get; init; }
}
