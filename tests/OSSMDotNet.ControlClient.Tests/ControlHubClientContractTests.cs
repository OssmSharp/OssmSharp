namespace OSSMDotNet.ControlClient.Tests;

/// <summary>
/// Checks hub callback ordering, acknowledgement handling, and ambiguous command outcomes.
/// </summary>
public sealed class ControlHubClientContractTests
{
    [Fact]
    public async Task OperatorCallbacksAreTypedSerializedAndObservedInHubSendOrder()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateOperatorClient();
        var callbacksCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();

        await client.SubscribeAsync(new(
            status =>
            {
                events.Add($"state:{status.Authority.Snapshot.Mode}:{status.Authority.SnapshotRevision}");
                return Task.CompletedTask;
            },
            batch =>
            {
                events.Add($"activity:{batch.FirstSequence}:{batch.Entries.Single().Message}");
                return Task.CompletedTask;
            },
            resync =>
            {
                events.Add($"resync:{resync.Code}");
                callbacksCompleted.TrySetResult();
                return Task.CompletedTask;
            }),
            41,
            cancellationSource.Token);
        await fixture.SendOperatorCallbacksAsync(cancellationSource.Token);
        await callbacksCompleted.Task.WaitAsync(cancellationSource.Token);

        Assert.Equal(["state:paused:1", "activity:1:fixture", "resync:fixture-resync"], events);
        var subscription = Assert.Single(fixture.Invocations);
        Assert.Equal("SubscribeOperator", subscription.Method);
        Assert.Matches("^[0-9a-f]{32}$", Assert.IsType<string>(subscription.Arguments[0]));
        Assert.Equal(41L, Assert.IsType<long>(subscription.Arguments[1]));
    }

    [Fact]
    public async Task OnlyOneCallbackRegistrationAndStartAreAllowedForEachOperatorClient()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateOperatorClient();
        var callbacks = new OperatorControlCallbacks(
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask);

        await client.SubscribeAsync(callbacks, 0, cancellationSource.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SubscribeAsync(callbacks, 1, cancellationSource.Token));

        Assert.Equal("Operator callbacks may only be registered once before connecting.", exception.Message);
        var subscription = Assert.Single(fixture.Invocations);
        Assert.Equal("SubscribeOperator", subscription.Method);
        Assert.Equal(0L, Assert.IsType<long>(subscription.Arguments[1]));
        Assert.Equal(1, fixture.ConnectionCount);
    }

    [Fact]
    public async Task NormalRejectedAcknowledgementPreservesItsStableCodeAndSendsNoAdditionalCommand()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        var logger = new RecordedLogger<ControlHubConnectionFactory>();
        await using var client = fixture.CreateOperatorClient(logger: logger);
        await client.SubscribeAsync(EmptyOperatorCallbacks, 0, cancellationSource.Token);
        fixture.Reject("Stop", "stale-authority");

        var exception = await Assert.ThrowsAsync<ControlClientException>(() => client.StopAsync(cancellationSource.Token));

        Assert.Equal("stale-authority", exception.Code);
        Assert.Equal("Control service rejected the operation: stale-authority.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty(logger.Entries);
        Assert.Equal(["SubscribeOperator", "Stop"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task InternalDeadlineAfterDispatchProducesUnknownOutcomeWithoutRetrying()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        var logger = new RecordedLogger<ControlHubConnectionFactory>();
        await using var client = fixture.CreateOperatorClient(commandTimeoutSeconds: 1, logger: logger);
        await client.SubscribeAsync(EmptyOperatorCallbacks, 0, cancellationSource.Token);
        fixture.Hold("Stop");
        try
        {
            var operation = client.StopAsync(cancellationSource.Token);
            await fixture.WaitForInvocationAsync("Stop", cancellationSource.Token);

            var exception = await Assert.ThrowsAsync<ControlClientUnknownOutcomeException>(() => operation);

            Assert.Equal("The control command outcome is unknown. Wait for the next service state update; do not resend it.", exception.Message);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Stop"));
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal(1901, entry.EventId);
            Assert.Contains("Stop", entry.Message, StringComparison.Ordinal);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(LocalControlHubFixture.ServiceKeyValue, entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Release("Stop");
        }
    }

    [Fact]
    public async Task CallerCancellationAfterDispatchRemainsCancellationInsteadOfUnknownOutcome()
    {
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        testCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(testCancellation.Token);
        var logger = new RecordedLogger<ControlHubConnectionFactory>();
        await using var client = fixture.CreateOperatorClient(logger: logger);
        await client.SubscribeAsync(EmptyOperatorCallbacks, 0, testCancellation.Token);
        fixture.Hold("Stop");
        using var callerCancellation = new CancellationTokenSource();
        try
        {
            var operation = client.StopAsync(callerCancellation.Token);
            await fixture.WaitForInvocationAsync("Stop", testCancellation.Token);
            await callerCancellation.CancelAsync();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

            Assert.True(callerCancellation.IsCancellationRequested);
            Assert.IsNotType<ControlClientUnknownOutcomeException>(exception);
            Assert.Empty(logger.Entries);
            Assert.Equal(1, fixture.Invocations.Count(invocation => invocation.Method == "Stop"));
        }
        finally
        {
            fixture.Release("Stop");
        }
    }

    [Fact]
    public async Task DisposePreventsFurtherCommandsAndSuppressesLaterOrdinaryCallbacks()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateOperatorClient();
        var stateCallbacks = 0;
        await client.SubscribeAsync(new(
            _ =>
            {
                Interlocked.Increment(ref stateCallbacks);
                return Task.CompletedTask;
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask),
            0,
            cancellationSource.Token);

        await client.DisposeAsync();
        await fixture.SendOperatorCallbacksAsync(cancellationSource.Token);
        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StopAsync(cancellationSource.Token));

        Assert.Contains("SemaphoreSlim", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stateCallbacks);
        Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
        Assert.Equal(1, fixture.DisconnectionCount);
    }

    [Fact]
    public async Task FailedInitialOperatorSubscriptionDisposesConnectionAndPreventsFurtherCommands()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateOperatorClient();
        fixture.Reject("SubscribeOperator", "subscription-rejected");

        var subscriptionException = await Assert.ThrowsAsync<ControlClientException>(() =>
            client.SubscribeAsync(EmptyOperatorCallbacks, 0, cancellationSource.Token));
        var commandException = await Assert.ThrowsAsync<ControlClientException>(() => client.StopAsync(cancellationSource.Token));

        Assert.Equal("subscription-rejected", subscriptionException.Code);
        Assert.Equal("connection-unavailable", commandException.Code);
        Assert.Equal(["SubscribeOperator"], fixture.Invocations.Select(invocation => invocation.Method));
    }

    [Fact]
    public async Task FailedGuestJoinDisposesTheConnectionAndLeavesGuestCommandsLocallyGuarded()
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationSource.Token);
        await using var client = fixture.CreateGuestClient();
        fixture.Reject("Join", "invitation-revoked");

        var joinException = await Assert.ThrowsAsync<ControlClientException>(() =>
            client.JoinAsync(EmptyGuestCallbacks, new("join-1", "session-1", "proof-1"), cancellationSource.Token));
        await fixture.WaitForDisconnectionAsync(cancellationSource.Token);
        var commandException = await Assert.ThrowsAsync<ControlClientException>(() => client.StopAsync(cancellationSource.Token));

        Assert.Equal("invitation-revoked", joinException.Code);
        Assert.Equal("guest-not-joined", commandException.Code);
        Assert.Equal(["SubscribeGuest", "Join"], fixture.Invocations.Select(invocation => invocation.Method));
        Assert.Equal(1, fixture.DisconnectionCount);
    }

    private static OperatorControlCallbacks EmptyOperatorCallbacks { get; } = new(
        static _ => Task.CompletedTask,
        static _ => Task.CompletedTask,
        static _ => Task.CompletedTask);

    private static GuestControlCallbacks EmptyGuestCallbacks { get; } = new(
        static _ => Task.CompletedTask,
        static _ => Task.CompletedTask);
}
