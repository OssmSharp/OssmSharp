using OSSMDotNet.Models;

namespace OSSMDotNet.ControlService.IntegrationTests;

public sealed class SignalRControlHubPushSinkIntegrationTests
{
    [Fact]
    public async Task StateActivityAndResyncCallbacksReachOnlyTheirSubscribedRoleAndConnectionInOrder()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(timeout.Token);
        await using var operatorClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Admin, timeout.Token);
        await using var guestClient = await fixture.ConnectAsync(LocalControlServiceHubFixture.HubRole.Guest, timeout.Token);

        await operatorClient.InvokeAsync<ControlAcknowledgement>("SubscribeOperator", "operator", 0);
        await fixture.DispatchAsync(timeout.Token);
        var initialOperatorState = await operatorClient.Callbacks.NextOperatorStateAsync(timeout.Token);
        await guestClient.InvokeAsync<ControlAcknowledgement>("SubscribeGuest", "guest");
        await guestClient.InvokeAsync<GuestJoinReceipt>("Join", new GuestJoinRequest(
            "join",
            initialOperatorState.Invitation.SessionId,
            LocalControlServiceHubFixture.InvitationProof(initialOperatorState.Invitation)));
        await fixture.DispatchAsync(timeout.Token);
        var joinedOperatorState = await operatorClient.Callbacks.NextOperatorStateAsync(timeout.Token);
        var joinedGuestState = await guestClient.Callbacks.NextGuestStateAsync(timeout.Token);
        fixture.AppendOperatorActivity("Operator completed a safe local action.");
        await fixture.DispatchAsync(timeout.Token);
        var activity = await operatorClient.Callbacks.NextOperatorLogAsync(timeout.Token);
        fixture.Registry.RequireResync(guestClient.ConnectionId);
        await fixture.DispatchAsync(timeout.Token);
        var resync = await guestClient.Callbacks.NextResyncAsync(timeout.Token);

        Assert.Equal("PendingApproval", joinedOperatorState.Authority.Snapshot.SessionMode);
        Assert.Equal("PendingApproval", joinedGuestState.SessionMode);
        Assert.Equal("Operator completed a safe local action.", Assert.Single(activity.Entries).Message);
        Assert.Equal("delivery-resync-required", resync.Code);
        Assert.Equal(["operatorState", "operatorState", "operatorLog"], operatorClient.Callbacks.Events);
        Assert.Equal(["guestState", "resyncRequired"], guestClient.Callbacks.Events);
        Assert.Equal(0, operatorClient.Callbacks.GuestStateCount);
        Assert.Equal(0, guestClient.Callbacks.OperatorStateCount);
        Assert.Equal(0, guestClient.Callbacks.OperatorLogCount);
    }

    [Fact]
    public async Task MissingConnectionIsBestEffortAndDoesNotBlockTheCurrentDispatcherLoop()
    {
        using var timeout = CreateTimeout();
        await using var fixture = await LocalControlServiceHubFixture.StartAsync(timeout.Token);
        fixture.Registry.SubscribeOperator("missing-connection", 0, -1);

        await fixture.DispatchAsync(timeout.Token);

        Assert.Contains("missing-connection", fixture.Registry.OperatorConnections());
        Assert.Empty(fixture.Registry.GuestConnections());
        Assert.Empty(fixture.Activity.ReadAfter(0));
    }

    private static CancellationTokenSource CreateTimeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }
}
