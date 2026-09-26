namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Verifies publication ordering, coalescing, and guest subscription ownership in the hub registry.
/// </summary>
public sealed class ControlHubConnectionRegistryTests
{
    [Fact]
    public void InitialNewerCoalescedAndStaleOrEqualOperatorStatesFollowPublicationRevision()
    {
        var registry = new ControlHubConnectionRegistry();
        registry.SubscribeOperator("operator", 7, -1);

        Assert.Equal(QueueState.Queued, registry.QueueOperatorState("operator", OperatorState(1)));
        Assert.Equal(QueueState.Queued, registry.QueueOperatorState("operator", OperatorState(2)));
        Assert.True(registry.TryTakePending("operator", out var pending));
        var coalesced = Assert.IsType<HubPendingPush>(pending);
        registry.MarkStateDelivered("operator", coalesced.Version);

        Assert.Equal("operatorState", coalesced.Method);
        Assert.Equal(2, coalesced.Version);
        Assert.Equal(QueueState.Ignored, registry.QueueOperatorState("operator", OperatorState(2)));
        Assert.Equal(QueueState.Ignored, registry.QueueOperatorState("operator", OperatorState(1)));
        Assert.False(registry.TryTakePending("operator", out _));
        var subscription = Assert.Single(registry.Subscriptions());
        Assert.Equal((7, 2), (subscription.ActivityCursor, subscription.LastDeliveredStateVersion));
    }

    [Fact]
    public void TerminalPublicationRetainsOneVersionAndSaturatesOnlyForANewerTerminalVersion()
    {
        var registry = new ControlHubConnectionRegistry();
        registry.SubscribeOperator("operator", 0, -1);

        Assert.Equal(QueueState.Queued, registry.QueueOperatorState("operator", OperatorState(5, sessionMode: "Ended")));
        Assert.Equal(QueueState.Queued, registry.QueueOperatorState("operator", OperatorState(5, sessionMode: "Ended")));
        Assert.Equal(QueueState.Saturated, registry.QueueOperatorState("operator", OperatorState(6, sessionMode: "Ended")));
        registry.RequireResync("operator");

        Assert.Equal(["operator"], registry.TakeResyncConnections());
        Assert.False(registry.TryTakePending("operator", out _));
        var subscription = Assert.Single(registry.Subscriptions());
        Assert.True(subscription.ResyncRequired);
        Assert.Equal(5, subscription.Terminal!.Version);
    }

    [Fact]
    public void TerminalPublicationDiscardsAnOlderQueuedNormalState()
    {
        var registry = new ControlHubConnectionRegistry();
        registry.SubscribeOperator("operator", 0, -1);

        Assert.Equal(QueueState.Queued, registry.QueueOperatorState("operator", OperatorState(5)));
        Assert.Equal(QueueState.Queued, registry.QueueOperatorState("operator", OperatorState(6, sessionMode: "Ended")));
        Assert.True(registry.TryTakePending("operator", out var terminal));
        var pendingTerminal = Assert.IsType<HubPendingPush>(terminal);
        registry.MarkStateDelivered("operator", pendingTerminal.Version);

        Assert.Equal(6, pendingTerminal.Version);
        Assert.Equal("Ended", Assert.IsType<OperatorLiveControlStatus>(pendingTerminal.Payload).Authority.Snapshot.SessionMode);
        Assert.False(registry.TryTakePending("operator", out _));
    }

    [Fact]
    public void UnchangedGuestStateIsNotQueuedAgainForANewerPublicationRevision()
    {
        var registry = new ControlHubConnectionRegistry();
        registry.SubscribeGuest("guest", -1);

        Assert.Equal(QueueState.Queued, registry.QueueGuestState("guest", GuestState(1)));
        Assert.True(registry.TryTakePending("guest", out var pending));
        registry.MarkStateDelivered("guest", Assert.IsType<HubPendingPush>(pending).Version);

        Assert.Equal(QueueState.Ignored, registry.QueueGuestState("guest", GuestState(2)));
        Assert.False(registry.TryTakePending("guest", out _));
        Assert.Equal(QueueState.Queued, registry.QueueGuestState("guest", GuestState(3, deviceMoving: true)));
    }

    [Fact]
    public void RemovingOneGuestClearsOnlyItsStageSubscriptionAndBoundSession()
    {
        var registry = new ControlHubConnectionRegistry();
        registry.StageGuest("first");
        registry.StageGuest("second");
        registry.SubscribeGuest("first", -1);
        registry.SubscribeGuest("second", -1);
        Assert.True(registry.TryBindGuest("first", new("logical-first", "session-first", "proof-first")));

        var removed = registry.Remove("first");

        Assert.Equal(("logical-first", "session-first"), (removed!.LogicalConnectionId, removed.SessionId));
        Assert.False(registry.IsGuestStaged("first"));
        Assert.True(registry.IsGuestStaged("second"));
        Assert.False(registry.TryGetGuest("first", out _));
        Assert.Equal("second", Assert.Single(registry.Subscriptions()).ConnectionId);
    }

    [Fact]
    public void ActivityCursorNeverMovesBackwardAndDoesNotCrossRoleSubscriptions()
    {
        var registry = new ControlHubConnectionRegistry();
        registry.SubscribeOperator("operator", 3, -1);
        registry.SubscribeGuest("guest", -1);

        registry.AdvanceActivityCursor("operator", 10);
        registry.AdvanceActivityCursor("operator", 4);

        var subscriptions = registry.Subscriptions().OrderBy(subscription => subscription.ConnectionId).ToArray();
        Assert.Equal(("guest", HubSubscriptionRole.Guest, 0), (subscriptions[0].ConnectionId, subscriptions[0].Role, subscriptions[0].ActivityCursor));
        Assert.Equal(("operator", HubSubscriptionRole.Operator, 10), (subscriptions[1].ConnectionId, subscriptions[1].Role, subscriptions[1].ActivityCursor));
    }

    private static OperatorLiveControlStatus OperatorState(long snapshotRevision, string sessionMode = "None") =>
        new(
            new(
                new ControlSnapshot(
                    "Paused",
                    "fixture",
                    1,
                    0,
                    true,
                    false,
                    TestMotion.Settings(),
                    null,
                    new ControlLimits(100, 100),
                    1,
                    sessionMode,
                    null,
                    null,
                    0,
                    null),
                new AuthorityPrecondition("fixture", 0, new(null, null, sessionMode)),
                snapshotRevision),
            new Invitation("fixture", "http://127.0.0.1:5182/join/fixture", null, true));

    private static GuestStatus GuestState(long snapshotRevision, bool deviceMoving = false) =>
        new(
            "Paused",
            "fixture",
            1,
            0,
            true,
            deviceMoving,
            new(0, 0, 10, 20, 50),
            0,
            0,
            new(100, 100),
            1,
            "Approved",
            null,
            0,
            null)
        {
            SnapshotRevision = snapshotRevision
        };
}
