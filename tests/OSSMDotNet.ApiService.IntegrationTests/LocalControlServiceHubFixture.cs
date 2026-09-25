using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading.Channels;

namespace OSSMDotNet.ApiService.IntegrationTests;

/// <summary>
/// Hosts a loopback API hub with controllable authority, device, and callback observations.
/// </summary>
[SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling",
    Justification = "Test-only SignalR fixture must compose its integration-host dependencies.")]
internal sealed partial class LocalControlServiceHubFixture : IAsyncDisposable
{
    internal const string AdminKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    internal const string GuestKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbQ";
    internal static readonly Uri GuestOrigin = new("http://127.0.0.1:5182");

    private WebApplication? _application;
    private int _disposed;

    private LocalControlServiceHubFixture()
    {
        Time = new ManualTimeProvider();
        Bridge = new FakeBridge();
        Engine = new ControlEngine(Bridge, Time);
        Bridge.Connected = true;
        Bridge.Publish();
        Authority = new ControlAuthority(Engine, Time);
        Registry = new ControlHubConnectionRegistry();
        Activity = new ControlActivityProvider(Time);
        Operations = new FixtureOperations(Authority, Bridge, Activity);
        Guests = new GuestControlOperations(Authority);
        FaultDevice = new SimulatedDevice(
            Options.Create(new SimulatedDeviceOptions { ExecutablePath = LocalRustSimulatorPath.Resolve() }),
            Time,
            NullLogger<SimulatedDevice>.Instance);
        Faults = new SimulatorFaultOperations(Authority, FaultDevice);
    }

    internal ManualTimeProvider Time { get; }
    internal FakeBridge Bridge { get; }
    internal ControlEngine Engine { get; }
    internal ControlAuthority Authority { get; }
    internal ControlHubConnectionRegistry Registry { get; }
    internal ControlActivityProvider Activity { get; }
    internal FixtureOperations Operations { get; }
    internal GuestControlOperations Guests { get; }
    internal SimulatedDevice FaultDevice { get; }
    internal SimulatorFaultOperations Faults { get; }
    internal ControlPushDispatcher Dispatcher { get; private set; } = null!;
    internal string ControlOrigin { get; private set; } = "";

    [SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling",
        Justification = "Test-only SignalR host setup configures every integration dependency.")]
    /// <summary>
    /// Starts the hub on an ephemeral loopback port and owns its authority and device graph.
    /// </summary>
    /// <returns>The running fixture; dispose it to release the host and device graph.</returns>
    internal static async Task<LocalControlServiceHubFixture> StartAsync(CancellationToken cancellationToken)
    {
        var fixture = new LocalControlServiceHubFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(LocalControlServiceHubFixture).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });
        builder.Configuration["AllowedHosts"] = "127.0.0.1";
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.Configure<ControlServiceOptions>(options =>
        {
            options.AdminWebServiceKey = AdminKey;
            options.GuestWebServiceKey = GuestKey;
        });
        builder.Services.AddSingleton(fixture.Authority);
        builder.Services.AddSingleton(fixture.Registry);
        builder.Services.AddSingleton(fixture.Activity);
        builder.Services.AddSingleton(fixture.Guests);
        builder.Services.AddSingleton<IControlOperations>(fixture.Operations);
        builder.Services.AddSingleton<IControlHubPushSink, SignalRControlHubPushSink>();
        builder.Services.AddSingleton<ControlPushDispatcher>();
        builder.Services.AddSingleton(new DisconnectObserver());
        builder.Services.AddSingleton<SimulatorFaultOperations>(fixture.Faults);
        builder.Services.AddApiAuthentication();
        builder.Services.AddApiAuthorization();
        builder.Services.AddApiHub();
        builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(options =>
            Microsoft.AspNetCore.SignalR.HubOptionsExtensions.AddFilter(options, typeof(DisconnectObserver)));

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControlHubV1();

        try
        {
            await application.StartAsync(cancellationToken);
            fixture._application = application;
            fixture.Dispatcher = application.Services.GetRequiredService<ControlPushDispatcher>();
            fixture.ControlOrigin = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            return fixture;
        }
        catch
        {
            await application.DisposeAsync();
            await fixture.DisposeGraphAsync();
            throw;
        }
    }

    internal async Task<HubClient> ConnectAsync(HubRole role, CancellationToken cancellationToken)
    {
        var callbacks = new HubCallbackRecorder();
        var connection = CreateConnection(role switch
        {
            HubRole.Admin => AdminKey,
            HubRole.Guest => GuestKey,
            _ => null
        });
        connection.On<OperatorLiveControlStatus>("operatorState", callbacks.OperatorStateReceivedAsync);
        connection.On<GuestStatus>("guestState", callbacks.GuestStateReceivedAsync);
        connection.On<ControlActivityBatch>("operatorLog", callbacks.OperatorLogReceivedAsync);
        connection.On<ControlResyncRequired>("resyncRequired", callbacks.ResyncReceivedAsync);
        try
        {
            await connection.StartAsync(cancellationToken);
            return new HubClient(connection, callbacks);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal HubConnection CreateAnonymousConnection() => CreateConnection(null);

    internal Task WaitForDisconnectAsync(string connectionId, CancellationToken cancellationToken) =>
        _application!.Services.GetRequiredService<DisconnectObserver>().WaitAsync(connectionId, cancellationToken);

    internal async Task DispatchAsync(CancellationToken cancellationToken)
    {
        Dispatcher.Trigger();
        await Dispatcher.DispatchOnceAsync(cancellationToken);
    }

    internal Invitation ReadInvitation() => Authority.ReadInvitation(GuestOrigin);

    internal JoinResult CreatePendingGuest(string logicalConnectionId = "fixture-guest")
    {
        var invitation = ReadInvitation();
        return Authority.JoinGuest(invitation.SessionId, InvitationProof(invitation), logicalConnectionId);
    }

    internal JoinResult PrepareApprovedGuest(string logicalConnectionId = "fixture-guest")
    {
        Authority.Recover(Authority.Read().Precondition);
        var joined = CreatePendingGuest(logicalConnectionId);
        Authority.Approve(Authority.Read().Precondition, joined.ConnectionId);
        return joined;
    }

    /// <summary>
    /// Recovers authority, joins and approves a guest, then arms the session.
    /// </summary>
    /// <param name="logicalConnectionId">The guest connection identifier.</param>
    /// <returns>The joined guest's credentials and session.</returns>
    internal JoinResult PrepareArmedGuest(string logicalConnectionId = "fixture-guest")
    {
        var joined = PrepareApprovedGuest(logicalConnectionId);
        Authority.Arm(Authority.Read().Precondition);
        return joined;
    }

    internal void LockAndSettle()
    {
        Bridge.External(TestMotion.Settings(depth: 21));
        Engine.Tick();
        Engine.Tick();
        Bridge.Publish();
        Engine.Tick();
        Bridge.ClearCalls();
    }

    internal void AppendOperatorActivity(string message)
    {
        var logger = Activity.CreateLogger("OSSMDotNet.ControlService.ContractTests");
        LogOperatorActivity(logger, message);
    }

    [LoggerMessage(EventId = 200, EventName = "OperatorActivityOperation", Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogOperatorActivity(ILogger logger, string message);

    internal static string InvitationProof(Invitation invitation) =>
        new Uri(invitation.Url).Fragment["#invite=".Length..];

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_application is not null)
            {
                try { await _application.StopAsync(); }
                finally { await _application.DisposeAsync(); }
            }
        }
        finally
        {
            await DisposeGraphAsync();
        }
    }

    private HubConnection CreateConnection(string? key)
    {
        var endpoint = new Uri(new Uri(ControlOrigin, UriKind.Absolute), "/hubs/control/v1");
        return new HubConnectionBuilder()
            .WithUrl(endpoint, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = false;
                if (key is not null)
                    options.Headers.Add("Authorization", $"ServiceKey {key}");
            })
            .Build();
    }

    private async ValueTask DisposeGraphAsync()
    {
        Activity.Dispose();
        await Authority.DisposeAsync();
        await FaultDevice.DisposeAsync();
        await Bridge.DisposeAsync();
    }

    /// <summary>
    /// Selects which service key the fixture uses for a hub connection.
    /// </summary>
    internal enum HubRole { Admin, Guest }

    /// <summary>
    /// Owns an authenticated test connection and its callback recorder.
    /// </summary>
    /// <param name="connection">The connection disposed with this client.</param>
    /// <param name="callbacks">The callback recorder attached to the connection.</param>
    internal sealed class HubClient(HubConnection connection, HubCallbackRecorder callbacks) : IAsyncDisposable
    {
        internal string ConnectionId => connection.ConnectionId
            ?? throw new InvalidOperationException("The SignalR connection has no current connection identifier.");
        internal HubCallbackRecorder Callbacks { get; } = callbacks;

        internal Task<T> InvokeAsync<T>(string method, params object?[] arguments) =>
            connection.InvokeCoreAsync<T>(method, arguments, TestContext.Current.CancellationToken);

        internal Task InvokeAsync(string method, params object?[] arguments) =>
            connection.InvokeCoreAsync(method, arguments, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    /// <summary>
    /// Queues hub callbacks and retains their arrival order for assertions.
    /// </summary>
    internal sealed class HubCallbackRecorder
    {
        private readonly Channel<OperatorLiveControlStatus> _operatorStates = Channel.CreateUnbounded<OperatorLiveControlStatus>();
        private readonly Channel<GuestStatus> _guestStates = Channel.CreateUnbounded<GuestStatus>();
        private readonly Channel<ControlActivityBatch> _operatorLogs = Channel.CreateUnbounded<ControlActivityBatch>();
        private readonly Channel<ControlResyncRequired> _resyncs = Channel.CreateUnbounded<ControlResyncRequired>();
        private readonly List<string> _events = [];
        private readonly object _gate = new();

        internal IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                    return _events.ToArray();
            }
        }

        internal int OperatorStateCount => _operatorStates.Reader.Count;
        internal int GuestStateCount => _guestStates.Reader.Count;
        internal int OperatorLogCount => _operatorLogs.Reader.Count;

        internal Task OperatorStateReceivedAsync(OperatorLiveControlStatus state)
        {
            Record("operatorState");
            _operatorStates.Writer.TryWrite(state);
            return Task.CompletedTask;
        }

        internal Task GuestStateReceivedAsync(GuestStatus state)
        {
            Record("guestState");
            _guestStates.Writer.TryWrite(state);
            return Task.CompletedTask;
        }

        internal Task OperatorLogReceivedAsync(ControlActivityBatch batch)
        {
            Record("operatorLog");
            _operatorLogs.Writer.TryWrite(batch);
            return Task.CompletedTask;
        }

        internal Task ResyncReceivedAsync(ControlResyncRequired resync)
        {
            Record("resyncRequired");
            _resyncs.Writer.TryWrite(resync);
            return Task.CompletedTask;
        }

        internal async Task<OperatorLiveControlStatus> NextOperatorStateAsync(CancellationToken cancellationToken) =>
            await _operatorStates.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        internal async Task<GuestStatus> NextGuestStateAsync(CancellationToken cancellationToken) =>
            await _guestStates.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        internal async Task<ControlActivityBatch> NextOperatorLogAsync(CancellationToken cancellationToken) =>
            await _operatorLogs.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        internal async Task<ControlResyncRequired> NextResyncAsync(CancellationToken cancellationToken) =>
            await _resyncs.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        internal bool TryTakeOperatorState(out OperatorLiveControlStatus? state) =>
            _operatorStates.Reader.TryRead(out state);

        internal bool TryTakeGuestState(out GuestStatus? state) =>
            _guestStates.Reader.TryRead(out state);

        private void Record(string name)
        {
            lock (_gate)
                _events.Add(name);
        }
    }

    /// <summary>
    /// Routes hub operations to the test authority and simulates device fault requests.
    /// </summary>
    internal sealed class FixtureOperations(
        ControlAuthority authority,
        FakeBridge bridge,
        ControlActivityProvider activity) : IControlOperations
    {
        public ControlAuthoritySnapshot Read() => authority.Read();
        public IReadOnlyList<ControlActivityEntry> ReadActivity(long afterSequence) => activity.ReadAfter(afterSequence);
        public Invitation ReadInvitation() => authority.ReadInvitation(GuestOrigin);
        public OperatorLiveControlStatus ReadOperatorStatus() => authority.ReadOperatorStatus(GuestOrigin);
        public ControlAuthoritySnapshot CycleInvitation(AuthorityPrecondition expected) =>
            authority.CycleInvitation(expected, GuestOrigin);
        public ControlAuthoritySnapshot Approve(ApproveGuestRequest? request, AuthorityPrecondition expected)
        {
            if (request is null)
                throw new ControlRejectedException("invalid-request");
            return authority.Approve(expected, request.ConnectionId);
        }
        public ControlAuthoritySnapshot ApplySessionLimits(ControlLimits limits, AuthorityPrecondition expected) =>
            authority.ApplySessionLimits(expected, limits);
        public ControlAuthoritySnapshot Recover(AuthorityPrecondition expected) => authority.Recover(expected);
        public ControlAuthoritySnapshot Arm(AuthorityPrecondition expected) => authority.Arm(expected);
        public ControlAuthoritySnapshot AllowControl(AuthorityPrecondition expected) => authority.AllowControl(expected);
        public ControlAuthoritySnapshot Stop() => authority.Stop();
        public ControlAuthoritySnapshot End(AuthorityPrecondition expected) => authority.End(expected, GuestOrigin);
        public ControlAuthoritySnapshot Unlock(UnlockControlRequest? request, AuthorityPrecondition expected)
        {
            if (request is null)
                throw new ControlRejectedException("invalid-request");
            return authority.Unlock(expected, request.LockRevision);
        }

        public Task InjectSimulatorFaultAsync(SimulatorFaultRequest? request)
        {
            if (request is null)
                throw new ControlRejectedException("invalid-request");
            var desired = authority.Read().Snapshot.Desired;
            switch (request.Kind)
            {
                case "external-zero":
                    bridge.External(desired with { Speed = 0 });
                    return Task.CompletedTask;
                case "external-depth":
                    bridge.External(desired with { Depth = Math.Min(100, desired.Depth + 1) });
                    return Task.CompletedTask;
                default:
                    throw new ControlRejectedException("unknown-simulator-fault");
            }
        }
    }

    /// <summary>
    /// Signals when a specific hub connection has finished disconnecting.
    /// </summary>
    public sealed class DisconnectObserver : Microsoft.AspNetCore.SignalR.IHubFilter
    {
        private readonly Dictionary<string, TaskCompletionSource> _disconnections = [];
        private readonly object _gate = new();

        public ValueTask<object?> InvokeMethodAsync(
            Microsoft.AspNetCore.SignalR.HubInvocationContext invocationContext,
            Func<Microsoft.AspNetCore.SignalR.HubInvocationContext, ValueTask<object?>> next) =>
            next(invocationContext);

        public Task OnConnectedAsync(
            Microsoft.AspNetCore.SignalR.HubLifetimeContext context,
            Func<Microsoft.AspNetCore.SignalR.HubLifetimeContext, Task> next) =>
            next(context);

        public async Task OnDisconnectedAsync(
            Microsoft.AspNetCore.SignalR.HubLifetimeContext context,
            Exception? exception,
            Func<Microsoft.AspNetCore.SignalR.HubLifetimeContext, Exception?, Task> next)
        {
            await next(context, exception);
            lock (_gate)
            {
                if (!_disconnections.TryGetValue(context.Context.ConnectionId, out var completed))
                    _disconnections[context.Context.ConnectionId] = completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                completed.TrySetResult();
            }
        }

        internal Task WaitAsync(string connectionId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_disconnections.TryGetValue(connectionId, out var completed))
                    _disconnections[connectionId] = completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return completed.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Advances UTC time and elapsed timestamps together for hub timing assertions.
    /// </summary>
    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
