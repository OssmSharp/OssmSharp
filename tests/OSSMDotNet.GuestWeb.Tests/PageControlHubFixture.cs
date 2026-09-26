using System.Net;

namespace OssmSharp.Web.GuestWeb.Tests;

/// <summary>
/// Hosts a loopback control hub for guest page tests and records its calls.
/// </summary>
public sealed class PageControlHubFixture : IAsyncDisposable
{
    internal const string ServiceKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";

    private readonly object _gate = new();
    private readonly List<PageHubInvocation> _invocations = [];
    private readonly Dictionary<string, string> _rejections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _invocationSignals = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication? _application;
    private IHubContext<PageControlHub>? _hub;
    private string? _connectionId;
    private int _connectionCount;
    private int _disconnectionCount;
    private int _disposed;

    internal string ControlOrigin { get; private set; } = "";
    internal OperatorLiveControlStatus? OperatorStatus { get; set; }
    internal int ConnectionCount => Volatile.Read(ref _connectionCount);
    internal int DisconnectionCount => Volatile.Read(ref _disconnectionCount);

    internal IReadOnlyList<PageHubInvocation> Invocations
    {
        get
        {
            lock (_gate)
                return _invocations.ToArray();
        }
    }

    /// <summary>
    /// Starts the hub on an ephemeral loopback port; dispose the fixture when finished.
    /// </summary>
    /// <returns>The running hub fixture.</returns>
    internal static async Task<PageControlHubFixture> StartAsync(CancellationToken cancellationToken)
    {
        var fixture = new PageControlHubFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(PageControlHubFixture).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture);
        builder.Services.AddSignalR();

        var application = builder.Build();
        application.MapHub<PageControlHub>("/hubs/control/v1", options =>
            options.Transports = HttpTransportType.WebSockets);

        try
        {
            await application.StartAsync(cancellationToken);
            fixture._application = application;
            fixture._hub = application.Services.GetRequiredService<IHubContext<PageControlHub>>();
            fixture.ControlOrigin = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            return fixture;
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    internal GuestControlClient CreateGuestClient(int commandTimeoutSeconds = 2)
    {
        var options = CreateOptions(commandTimeoutSeconds);
        return new GuestControlClient(new ControlHubConnectionFactory(options), options);
    }

    internal OperatorControlClient CreateOperatorClient(int commandTimeoutSeconds = 2)
    {
        var options = CreateOptions(commandTimeoutSeconds);
        return new OperatorControlClient(new ControlHubConnectionFactory(options), options);
    }

    internal Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
        _connected.Task.WaitAsync(cancellationToken);

    internal Task WaitForDisconnectionAsync(CancellationToken cancellationToken) =>
        _disconnected.Task.WaitAsync(cancellationToken);

    internal Task WaitForInvocationAsync(string method, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_invocations.Any(invocation => invocation.Method == method))
                return Task.CompletedTask;

            if (!_invocationSignals.TryGetValue(method, out var signal))
            {
                signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _invocationSignals.Add(method, signal);
            }

            return signal.Task.WaitAsync(cancellationToken);
        }
    }

    internal void Reject(string method, string code)
    {
        lock (_gate)
            _rejections[method] = code;
    }

    /// <summary>
    /// Holds subsequent calls to a hub method until <see cref="Release"/> is called.
    /// </summary>
    /// <param name="method">The hub method to hold.</param>
    internal void Hold(string method)
    {
        lock (_gate)
        {
            _rejections.Remove(method);
            if (!_holds.ContainsKey(method))
                _holds.Add(method, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    internal void Release(string method)
    {
        lock (_gate)
        {
            if (_holds.TryGetValue(method, out var hold))
                hold.TrySetResult();
        }
    }

    internal async Task SendGuestStatusAsync(GuestStatus status, CancellationToken cancellationToken) =>
        await ConnectedClients().Client(ConnectionId).SendAsync("guestState", status, cancellationToken);

    internal async Task SendOperatorStateAsync(OperatorLiveControlStatus status, CancellationToken cancellationToken) =>
        await ConnectedClients().Client(ConnectionId).SendAsync("operatorState", status, cancellationToken);

    internal async Task SendOperatorLogAsync(ControlActivityBatch batch, CancellationToken cancellationToken) =>
        await ConnectedClients().Client(ConnectionId).SendAsync("operatorLog", batch, cancellationToken);

    internal async Task SendResyncAsync(string code, CancellationToken cancellationToken) =>
        await ConnectedClients().Client(ConnectionId).SendAsync("resyncRequired", new ControlResyncRequired(code), cancellationToken);

    internal void RegisterConnection(HubCallerContext context)
    {
        lock (_gate)
            _connectionId = context.ConnectionId;

        Interlocked.Increment(ref _connectionCount);
        _connected.TrySetResult();
    }

    internal void RegisterDisconnection()
    {
        Interlocked.Increment(ref _disconnectionCount);
        _disconnected.TrySetResult();
    }

    internal async Task<ControlAcknowledgement> SubscribeOperatorAsync(
        IClientProxy caller,
        string correlation,
        long afterSequence)
    {
        var acknowledgement = await RecordAsync("SubscribeOperator", [correlation, afterSequence]);
        if (acknowledgement.Accepted && OperatorStatus is { } status)
            await caller.SendAsync("operatorState", status);
        return acknowledgement;
    }

    internal async Task<GuestJoinReceipt> JoinAsync(GuestJoinRequest request)
    {
        var acknowledgement = await RecordAsync("Join", [request]);
        return acknowledgement.Accepted
            ? new("joined", request.Correlation)
            : new(acknowledgement.Code, acknowledgement.Correlation) { Accepted = false };
    }

    /// <summary>
    /// Records a hub call before applying a configured hold or rejection and returning its acknowledgement.
    /// </summary>
    /// <param name="method">The hub method invoked by the client.</param>
    /// <param name="arguments">The call arguments used to recover its correlation.</param>
    /// <returns>The accepted or rejected acknowledgement for the recorded call.</returns>
    internal async Task<ControlAcknowledgement> RecordAsync(string method, object?[] arguments)
    {
        TaskCompletionSource? hold;
        string? rejection;
        lock (_gate)
        {
            _invocations.Add(new(method, arguments));
            if (_invocationSignals.TryGetValue(method, out var signal))
                signal.TrySetResult();
            _holds.TryGetValue(method, out hold);
            _rejections.TryGetValue(method, out rejection);
        }

        if (rejection is null && hold is not null)
            await hold.Task;

        var correlation = arguments.OfType<string>().LastOrDefault()
            ?? arguments.OfType<GuestJoinRequest>().SingleOrDefault()?.Correlation
            ?? arguments.OfType<GuestInputRequest>().SingleOrDefault()?.Correlation
            ?? throw new InvalidOperationException($"The {method} fixture call had no correlation.");
        return rejection is null
            ? new($"accepted-{method}", correlation)
            : new(rejection, correlation) { Accepted = false };
    }

    internal IOptions<ControlClientOptions> CreateOptions(int commandTimeoutSeconds = 2) =>
        Options.Create(new ControlClientOptions
        {
            ControlOrigin = ControlOrigin,
            ServiceKey = ServiceKey,
            KeepAliveSeconds = 1,
            ServerTimeoutSeconds = 3,
            CommandTimeoutSeconds = commandTimeoutSeconds
        });

    internal static GuestStatus ReadyGuestStatus(long snapshotRevision, int speedCap = 80, int maximumDepth = 60) =>
        new(
            "Ready",
            "fixture",
            1,
            0,
            true,
            false,
            new ControlValues(0, speedCap, maximumDepth, maximumDepth, 50),
            null,
            null,
            new GuestLimits(speedCap, maximumDepth),
            7,
            "Approved",
            "fixture-connection",
            0,
            new GuestGrant("grant-1", "epoch-1", 7, 750))
        {
            Current = new ControlValues(0, speedCap, maximumDepth, maximumDepth, 50),
            DeviceEnabled = true,
            IsSimulator = true,
            SnapshotRevision = snapshotRevision
        };

    internal static GuestStatus LockedGuestStatus(long snapshotRevision) =>
        ReadyGuestStatus(snapshotRevision) with { Mode = "Locked" };

    internal static OperatorLiveControlStatus OperatorStatusFor(
        AuthorityPrecondition precondition,
        int speedCap = 80,
        int maximumDepth = 60,
        string sessionMode = "PendingApproval",
        long snapshotRevision = 1,
        string invitationUrl = "http://127.0.0.1:5182/join/fixture#invite=public-link") =>
        new(
            new(
                new ControlSnapshot(
                    "Paused",
                    "fixture",
                    1,
                    0,
                    true,
                    false,
                    new(MotionDeviceState.StrokeEngineIdle, 0, 10, 50, 20, 0, MotionPattern.SimpleStroke, 0),
                    null,
                    new ControlLimits(speedCap, maximumDepth),
                    7,
                    sessionMode,
                    precondition.GuestAdmission.SessionId,
                    precondition.GuestAdmission.ConnectionId,
                    0,
                    null)
                {
                    DeviceEnabled = false,
                    IsSimulator = true
                },
                precondition,
                snapshotRevision),
            new Invitation("fixture", invitationUrl, null, true));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            foreach (var hold in _holds.Values)
                hold.TrySetResult();
        }

        if (_application is not null)
        {
            try
            {
                await _application.StopAsync();
            }
            finally
            {
                await _application.DisposeAsync();
            }
        }
    }

    private string ConnectionId
    {
        get
        {
            lock (_gate)
                return _connectionId ?? throw new InvalidOperationException("The hub connection was not registered.");
        }
    }

    private IHubClients ConnectedClients() =>
        (_hub ?? throw new InvalidOperationException("The hub context was not initialized.")).Clients;
}
