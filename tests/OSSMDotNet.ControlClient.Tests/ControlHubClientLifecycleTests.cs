using OSSMDotNet.Models;

namespace OSSMDotNet.ControlClient.Tests;

/// <summary>
/// Checks terminal resync and callback serialization when a hub connection closes.
/// </summary>
public sealed class ControlHubClientLifecycleTests
{
    [Theory]
    [InlineData("operator")]
    [InlineData("guest")]
    public async Task ConnectionAbortDeliversExactlyOneTerminalResyncAndSuppressesOrdinaryCallbacks(string role)
    {
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellationSource.CancelAfter(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellationSource.Token;
        await using var fixture = await LocalControlHubFixture.StartAsync(cancellationToken);
        var logger = new RecordedLogger<ControlHubConnectionFactory>();
        var callbacks = new CallbackRecorder(role);

        if (role == "operator")
        {
            await using var client = fixture.CreateOperatorClient(logger: logger);
            await client.SubscribeAsync(
                new(callbacks.OperatorStateReceivedAsync, callbacks.OperatorActivityReceivedAsync, callbacks.ResyncRequiredAsync),
                0,
                cancellationToken);
            await AssertClosedConnectionAsync(fixture, callbacks, client.DisposeAsync, cancellationToken);
        }
        else
        {
            await using var client = fixture.CreateGuestClient(logger: logger);
            await client.JoinAsync(
                new(callbacks.GuestStateReceivedAsync, callbacks.ResyncRequiredAsync),
                new GuestJoinRequest("fixture", "session", "proof"),
                cancellationToken);
            await AssertClosedConnectionAsync(fixture, callbacks, client.DisposeAsync, cancellationToken);
        }

        Assert.Equal(1, callbacks.ResyncCount);
        Assert.Equal(["resync:connection-closed"], callbacks.Events);
        Assert.Equal(1, callbacks.MaximumConcurrentCallbacks);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1900, entry.EventId);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(LocalControlHubFixture.ServiceKeyValue, entry.Message, StringComparison.Ordinal);
    }

    private static async Task AssertClosedConnectionAsync(
        LocalControlHubFixture fixture,
        CallbackRecorder callbacks,
        Func<ValueTask> dispose,
        CancellationToken cancellationToken)
    {
        await fixture.WaitForConnectionAsync(cancellationToken);
        Assert.Equal("ServiceKey aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA", fixture.AuthorizationHeader);
        Assert.True(fixture.UsedWebSockets);

        await fixture.AbortAndAttemptOrdinaryCallbacksAsync(
            callbacks.ExpectedRole,
            cancellationToken);
        await fixture.WaitForOrdinaryCallbackAttemptAsync(cancellationToken);
        var resync = await callbacks.WaitForResyncAsync(cancellationToken);

        Assert.Equal("connection-closed", resync.Code);
        Assert.Equal(1, callbacks.ResyncCount);
        Assert.Equal(0, callbacks.StateCount);
        Assert.Equal(0, callbacks.ActivityCount);

        await dispose();

        Assert.Equal(1, callbacks.ResyncCount);
        Assert.Equal(0, callbacks.StateCount);
        Assert.Equal(0, callbacks.ActivityCount);
    }

    /// <summary>
    /// Records callback order and concurrent delivery across operator and guest subscriptions.
    /// </summary>
    private sealed class CallbackRecorder
    {
        private readonly TaskCompletionSource<ControlResyncRequired> _resync = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _events = [];
        private readonly object _eventsLock = new();
        private int _activeCallbacks;
        private int _activityCount;
        private int _maximumConcurrentCallbacks;
        private int _resyncCount;
        private int _stateCount;

        public string ExpectedRole { get; }
        public int ActivityCount => Volatile.Read(ref _activityCount);
        public int MaximumConcurrentCallbacks => Volatile.Read(ref _maximumConcurrentCallbacks);
        public int ResyncCount => Volatile.Read(ref _resyncCount);
        public int StateCount => Volatile.Read(ref _stateCount);

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_eventsLock)
                    return _events.ToArray();
            }
        }

        public CallbackRecorder(string expectedRole) => ExpectedRole = expectedRole;

        public Task OperatorStateReceivedAsync(OperatorLiveControlStatus status)
        {
            Interlocked.Increment(ref _stateCount);
            return RecordAsync("operator-state");
        }

        public Task OperatorActivityReceivedAsync(ControlActivityBatch batch)
        {
            Interlocked.Increment(ref _activityCount);
            return RecordAsync("operator-log");
        }

        public Task GuestStateReceivedAsync(GuestStatus status)
        {
            Interlocked.Increment(ref _stateCount);
            return RecordAsync("guest-state");
        }

        public async Task ResyncRequiredAsync(ControlResyncRequired resync)
        {
            EnterCallback();
            try
            {
                if (Interlocked.Increment(ref _resyncCount) == 1)
                    _resync.TrySetResult(resync);
                lock (_eventsLock)
                    _events.Add($"resync:{resync.Code}");
                await Task.CompletedTask;
            }
            finally
            {
                ExitCallback();
            }
        }

        public Task<ControlResyncRequired> WaitForResyncAsync(CancellationToken cancellationToken) =>
            _resync.Task.WaitAsync(cancellationToken);

        private Task RecordAsync(string callback)
        {
            EnterCallback();
            try
            {
                lock (_eventsLock)
                    _events.Add(callback);
                return Task.CompletedTask;
            }
            finally
            {
                ExitCallback();
            }
        }

        private void EnterCallback()
        {
            var active = Interlocked.Increment(ref _activeCallbacks);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrentCallbacks);
                if (maximum >= active ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCallbacks, active, maximum) == maximum)
                    return;
            }
        }

        private void ExitCallback() => Interlocked.Decrement(ref _activeCallbacks);
    }
}
