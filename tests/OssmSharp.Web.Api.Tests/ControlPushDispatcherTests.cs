using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OssmSharp.Web.Application;
using OssmSharp.Web.Application.Models;
using OssmSharp.Web.Application.Tests;

namespace OssmSharp.Web.Api.Tests;

/// <summary>
/// Checks scoped hub pushes, bounded activity delivery, and subscriber resynchronization.
/// </summary>
public sealed partial class ControlPushDispatcherTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task DispatcherTriggerPublishesFreshOperatorProjectionIncludingTheOperatorInvitation()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        fixture.Registry.SubscribeOperator("operator", 0, -1);

        fixture.Dispatcher.Trigger();
        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        var callback = Assert.Single(fixture.Sink.Calls);
        var status = Assert.IsType<OperatorLiveControlStatus>(callback.Payload);
        var serialized = JsonSerializer.Serialize(callback.Payload, JsonOptions);
        var invitation = fixture.Authority.ReadInvitation(new Uri("http://127.0.0.1:5182"));

        Assert.Equal("operatorState", callback.Method);
        Assert.Equal(fixture.Authority.Read().Precondition.AuthorityRevision, status.Authority.Precondition.AuthorityRevision);
        Assert.Contains(invitation.Url, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowSubscriberDoesNotStallAuthorityAndIsRemovedForResync()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        fixture.Registry.SubscribeOperator("slow", 0, -1);
        fixture.Sink.SlowConnections.Add("slow");

        var dispatch = fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Sink.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        fixture.Authority.Stop();
        stopwatch.Stop();

        await dispatch;

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"Authority was stalled for {stopwatch.Elapsed}.");
        Assert.Empty(fixture.Registry.OperatorConnections());
        Assert.Contains(fixture.Sink.Calls, call => call.ConnectionId == "slow" && call.Method == "resyncRequired");
    }

    [Fact]
    public async Task FailedSubscriberIsRemovedAndReceivesBestEffortResyncCallback()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        fixture.Registry.SubscribeOperator("failed", 0, -1);
        fixture.Sink.FailMethods.Add("operatorState");

        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Registry.OperatorConnections());
        Assert.Collection(fixture.Sink.Calls,
            call => Assert.Equal("operatorState", call.Method),
            call => Assert.Equal("resyncRequired", call.Method));
        var resync = Assert.IsType<ControlResyncRequired>(fixture.Sink.Calls[1].Payload);
        var diagnostic = Assert.Single(fixture.Activity.ReadAfter(0));
        Assert.Equal("delivery-resync-required", resync.Code);
        Assert.Equal("A control-service client delivery failed; resynchronization was requested.", diagnostic.Message);
        Assert.DoesNotContain("operatorState", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetainedMaxEscapedMultibyteActivityBatchesFitWithoutOmissionsDuplicatesAndReportGap()
    {
        using var activity = new ControlActivityProvider(TimeProvider.System);
        var logger = activity.CreateLogger("OssmSharp.Web.Application.Tests");
        var message = string.Concat(Enumerable.Repeat("\"漢字\\\n", 100));
        for (var index = 0; index < 205; index++)
            WriteActivity(logger, $"{index:D3}:{message}");

        var cursor = 0L;
        var received = new List<long>();
        var sawGap = false;
        while (true)
        {
            var batch = activity.ReadBatchAfter(cursor,
                candidate => ControlHubPayload.InvocationByteCount("operatorLog", candidate),
                ControlHubPayload.MaximumBytes);
            Assert.True(InvocationBytes("operatorLog", batch) <= 16_384);
            Assert.True(batch.Entries.Count > 0);
            Assert.Equal(batch.Entries[0].Sequence, batch.FirstSequence);
            Assert.Equal(batch.Entries[^1].Sequence, batch.LastSequence);
            received.AddRange(batch.Entries.Select(entry => entry.Sequence));
            sawGap |= batch.Gap;
            cursor = batch.LastSequence;
            if (batch.ContinuationSequence is null)
                break;
            Assert.Equal(cursor, batch.ContinuationSequence.Value);
        }

        Assert.True(sawGap);
        Assert.Equal(6, received[0]);
        Assert.Equal(205, received[^1]);
        Assert.Equal(Enumerable.Range(6, 200).Select(static value => (long)value), received);
        Assert.Equal(received.Count, received.Distinct().Count());
    }

    [Fact]
    public async Task OperatorStateAfterGuestAdmissionUsesSnapshotPublicationVersionEvenWhenAuthorityRevisionIsUnchanged()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        fixture.Registry.SubscribeOperator("operator", 0, -1);

        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        var firstCall = await fixture.Sink.ReceiveAsync("initial operator state", TestContext.Current.CancellationToken);
        var first = Assert.IsType<OperatorLiveControlStatus>(firstCall.Payload);
        Assert.Equal(("operator", "operatorState"), (firstCall.ConnectionId, firstCall.Method));
        Assert.Equal("None", first.Authority.Snapshot.SessionMode);
        Assert.Null(first.Authority.Precondition.GuestAdmission.SessionId);

        fixture.Registry.StageGuest("guest");
        var guests = new GuestOperationsHarness(new GuestControlOperations(fixture.Authority), fixture.Registry);
        var receipt = guests.Join("guest", new("join", first.Invitation.SessionId,
            new Uri(first.Invitation.Url).Fragment["#invite=".Length..]));
        var afterAdmission = fixture.Authority.Read();

        Assert.Equal("joined", receipt.Code);
        Assert.Equal(first.Authority.Precondition.AuthorityRevision,
            afterAdmission.Precondition.AuthorityRevision);
        Assert.True(afterAdmission.SnapshotRevision > first.Authority.SnapshotRevision);
        Assert.Equal("PendingApproval", afterAdmission.Snapshot.SessionMode);

        fixture.Dispatcher.Trigger();
        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        var secondCall = await fixture.Sink.ReceiveAsync("post-admission operator state", TestContext.Current.CancellationToken);
        var second = Assert.IsType<OperatorLiveControlStatus>(secondCall.Payload);
        Assert.Equal(("operator", "operatorState"), (secondCall.ConnectionId, secondCall.Method));
        Assert.Equal(first.Authority.Precondition.AuthorityRevision,
            second.Authority.Precondition.AuthorityRevision);
        Assert.True(second.Authority.SnapshotRevision > first.Authority.SnapshotRevision);
        Assert.Equal("PendingApproval", second.Authority.Precondition.GuestAdmission.SessionMode);
        Assert.Equal(first.Invitation.SessionId, second.Authority.Precondition.GuestAdmission.SessionId);
        Assert.False(fixture.Sink.TryTakeDelivery(out _));
    }

    [Fact]
    public async Task TerminalOperatorStateIsDeliveredOncePerPublicationRevisionAndAgainForANewerSnapshot()
    {
        await using var fixture = await DispatcherFixture.CreateAsync();
        fixture.Registry.SubscribeOperator("operator", 0, -1);
        var invitation = fixture.Authority.ReadInvitation(new Uri("http://127.0.0.1:5182"));
        fixture.Registry.StageGuest("guest");
        var guests = new GuestOperationsHarness(new GuestControlOperations(fixture.Authority), fixture.Registry);
        guests.Join("guest", new("join", invitation.SessionId,
            new Uri(invitation.Url).Fragment["#invite=".Length..]));
        var leave = guests.Leave("guest", "leave");
        var terminalSnapshot = fixture.Authority.Read();

        Assert.Equal("ended", leave.Code);
        Assert.Equal("Ended", terminalSnapshot.Snapshot.SessionMode);

        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        var firstTerminalCall = await fixture.Sink.ReceiveAsync("initial terminal operator state", TestContext.Current.CancellationToken);
        var firstTerminal = Assert.IsType<OperatorLiveControlStatus>(firstTerminalCall.Payload);
        Assert.Equal(("operator", "operatorState"), (firstTerminalCall.ConnectionId, firstTerminalCall.Method));
        Assert.Equal("Ended", firstTerminal.Authority.Snapshot.SessionMode);
        Assert.Equal(terminalSnapshot.SnapshotRevision, firstTerminal.Authority.SnapshotRevision);

        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Sink.TryTakeDelivery(out _));

        fixture.Authority.Tick();
        var newerTerminalSnapshot = fixture.Authority.Read();
        Assert.True(newerTerminalSnapshot.SnapshotRevision > terminalSnapshot.SnapshotRevision);
        Assert.Equal("Ended", newerTerminalSnapshot.Snapshot.SessionMode);

        fixture.Dispatcher.Trigger();
        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        var newerTerminalCall = await fixture.Sink.ReceiveAsync("newer terminal operator state", TestContext.Current.CancellationToken);
        var newerTerminal = Assert.IsType<OperatorLiveControlStatus>(newerTerminalCall.Payload);
        Assert.Equal(("operator", "operatorState"), (newerTerminalCall.ConnectionId, newerTerminalCall.Method));
        Assert.Equal("Ended", newerTerminal.Authority.Snapshot.SessionMode);
        Assert.True(newerTerminal.Authority.SnapshotRevision > firstTerminal.Authority.SnapshotRevision);

        await fixture.Dispatcher.DispatchOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.Sink.TryTakeDelivery(out _));
    }

    private static int InvocationBytes(string target, object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type = 1, target, arguments = new[] { payload } },
            JsonOptions).Length + 1;

    [LoggerMessage(EventId = 200, EventName = "OperatorActivityOperation", Level = LogLevel.Information, Message = "{Message}")]
    private static partial void WriteActivity(ILogger logger, string message);

    /// <summary>
    /// Owns the authority, registry, activity capture, and push sink used by dispatcher tests.
    /// </summary>
    private sealed class DispatcherFixture : IAsyncDisposable
    {
        private DispatcherFixture(FakeBridge bridge, ControlEngine engine, ControlAuthority authority,
            ControlHubConnectionRegistry registry, ControlActivityProvider activity, ILoggerFactory loggerFactory,
            TestPushSink sink, ControlPushDispatcher dispatcher)
        {
            Bridge = bridge;
            Engine = engine;
            Authority = authority;
            Registry = registry;
            Activity = activity;
            LoggerFactory = loggerFactory;
            Sink = sink;
            Dispatcher = dispatcher;
        }

        internal FakeBridge Bridge { get; }
        internal ControlEngine Engine { get; }
        internal ControlAuthority Authority { get; }
        internal ControlHubConnectionRegistry Registry { get; }
        internal ControlActivityProvider Activity { get; }
        internal ILoggerFactory LoggerFactory { get; }
        internal TestPushSink Sink { get; }
        internal ControlPushDispatcher Dispatcher { get; }

        internal static async Task<DispatcherFixture> CreateAsync()
        {
            var bridge = new FakeBridge();
            var engine = new ControlEngine(bridge, TimeProvider.System);
            bridge.Connected = true;
            bridge.Publish();
            var authority = new ControlAuthority(engine, TimeProvider.System);
            var registry = new ControlHubConnectionRegistry();
            var activity = new ControlActivityProvider(TimeProvider.System);
            var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(activity));
            var sink = new TestPushSink();
            var operations = new TestOperations(authority);
            var dispatcher = new ControlPushDispatcher(operations, new GuestControlOperations(authority),
                registry, activity, sink, loggerFactory.CreateLogger<ControlPushDispatcher>());
            await Task.CompletedTask;
            return new(bridge, engine, authority, registry, activity, loggerFactory, sink, dispatcher);
        }

        public async ValueTask DisposeAsync()
        {
            Activity.Dispose();
            LoggerFactory.Dispose();
            await Authority.DisposeAsync();
            await Bridge.DisposeAsync();
        }
    }

    /// <summary>
    /// Exposes authority reads while rejecting commands unrelated to push delivery.
    /// </summary>
    private sealed class TestOperations(ControlAuthority authority) : IControlOperations
    {
        public ControlAuthoritySnapshot Read() => authority.Read();
        public IReadOnlyList<ControlActivityEntry> ReadActivity(long afterSequence) => [];
        public Invitation ReadInvitation() => authority.ReadInvitation(new Uri("http://127.0.0.1:5182"));
        public OperatorLiveControlStatus ReadOperatorStatus() => new(Read(), ReadInvitation());
        public ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot Approve(ApproveGuestRequest? request, AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot ApplySessionLimits(ControlLimits limits, AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot Recover(AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot Arm(AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot Stop() => throw new NotSupportedException();
        public ControlAuthoritySnapshot End(AuthorityPrecondition expected) => throw new NotSupportedException();
        public ControlAuthoritySnapshot Unlock(UnlockControlRequest? request, AuthorityPrecondition expected) => throw new NotSupportedException();
        public Task InjectSimulatorFaultAsync(SimulatorFaultRequest? request) => throw new NotSupportedException();
    }

    /// <summary>
    /// Records pushes and can stall or fail selected deliveries to exercise resynchronization.
    /// </summary>
    private sealed class TestPushSink : IControlHubPushSink
    {
        private readonly Channel<(string ConnectionId, string Method, object Payload)> _deliveries =
            Channel.CreateUnbounded<(string ConnectionId, string Method, object Payload)>();

        internal List<(string ConnectionId, string Method, object Payload)> Calls { get; } = [];
        internal HashSet<string> SlowConnections { get; } = [];
        internal HashSet<string> FailMethods { get; } = [];
        internal TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task<(string ConnectionId, string Method, object Payload)> ReceiveAsync(string expectedCallback,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _deliveries.Reader.ReadAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new Xunit.Sdk.XunitException($"Expected {expectedCallback} callback was not delivered.", exception);
            }
        }

        internal bool TryTakeDelivery(out (string ConnectionId, string Method, object Payload) delivery) =>
            _deliveries.Reader.TryRead(out delivery);

        public async Task SendAsync(string connectionId, string method, object payload, CancellationToken cancellationToken)
        {
            Calls.Add((connectionId, method, payload));
            _deliveries.Writer.TryWrite((connectionId, method, payload));
            FirstSendStarted.TrySetResult();
            if (SlowConnections.Contains(connectionId) && method != "resyncRequired")
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (FailMethods.Contains(method))
                throw new InvalidOperationException("simulated-send-failure");
        }
    }
}
