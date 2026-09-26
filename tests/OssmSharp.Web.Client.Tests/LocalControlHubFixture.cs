using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace OssmSharp.Web.Client.Tests;

/// <summary>
/// Hosts a loopback SignalR hub that records client requests and controls callback timing.
/// </summary>
[SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling",
    Justification = "Test-only SignalR fixture must compose its integration-host dependencies.")]
public sealed class LocalControlHubFixture : IAsyncDisposable
{
    public const string ServiceKeyValue = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA";
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ordinaryCallbackAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _connectionLock = new();
    private readonly object _invocationLock = new();
    private readonly Dictionary<string, string> _rejections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _invocationSignals = new(StringComparer.Ordinal);
    private readonly List<HubInvocation> _invocations = [];
    private WebApplication? _application;
    private IHubContext<LocalControlHub>? _hubContext;
    private Action? _abortConnection;
    private string? _connectionId;
    private int _connectionCount;
    private int _disconnectionCount;
    private int _disposed;

    public string ControlOrigin { get; private set; } = "";
    public string? AuthorizationHeader { get; private set; }
    public string? ConnectionPath { get; private set; }
    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public int DisconnectionCount => Volatile.Read(ref _disconnectionCount);
    public bool SendOperatorStateOnSubscribe { get; set; }
    public bool UsedWebSockets { get; private set; }
    internal IReadOnlyList<HubInvocation> Invocations
    {
        get
        {
            lock (_invocationLock)
                return _invocations.ToArray();
        }
    }

    /// <summary>
    /// Starts the hub on an ephemeral loopback port; dispose the returned fixture to stop it.
    /// </summary>
    /// <returns>The running hub fixture.</returns>
    public static async Task<LocalControlHubFixture> StartAsync(CancellationToken cancellationToken)
    {
        var fixture = new LocalControlHubFixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(LocalControlHubFixture).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture);
        builder.Services.AddSignalR();

        var application = builder.Build();
        application.MapHub<LocalControlHub>("/hubs/control/v1", options =>
            options.Transports = HttpTransportType.WebSockets);

        try
        {
            await application.StartAsync(cancellationToken);
            fixture._application = application;
            fixture._hubContext = application.Services.GetRequiredService<IHubContext<LocalControlHub>>();
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

    public OperatorControlClient CreateOperatorClient(int commandTimeoutSeconds = 2,
        ILogger<ControlHubConnectionFactory>? logger = null)
    {
        var options = CreateOptions(commandTimeoutSeconds);
        var factory = logger is null ? new ControlHubConnectionFactory(options) : new ControlHubConnectionFactory(options, logger);
        return new OperatorControlClient(factory, options);
    }

    public GuestControlClient CreateGuestClient(int commandTimeoutSeconds = 2,
        ILogger<ControlHubConnectionFactory>? logger = null)
    {
        var options = CreateOptions(commandTimeoutSeconds);
        var factory = logger is null ? new ControlHubConnectionFactory(options) : new ControlHubConnectionFactory(options, logger);
        return new GuestControlClient(factory, options);
    }

    public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
        _connected.Task.WaitAsync(cancellationToken);

    public Task WaitForDisconnectionAsync(CancellationToken cancellationToken) =>
        _disconnected.Task.WaitAsync(cancellationToken);

    public Task WaitForOrdinaryCallbackAttemptAsync(CancellationToken cancellationToken) =>
        _ordinaryCallbackAttempted.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Waits for an invocation of the named hub method, including one already recorded.
    /// </summary>
    /// <param name="method">The hub method to observe.</param>
    public Task WaitForInvocationAsync(string method, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        lock (_invocationLock)
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

    public void Reject(string method, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        lock (_invocationLock)
            _rejections[method] = code;
    }

    /// <summary>
    /// Holds subsequent calls to a hub method until <see cref="Release"/> is called.
    /// </summary>
    /// <param name="method">The hub method to hold.</param>
    public void Hold(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        lock (_invocationLock)
        {
            if (!_holds.ContainsKey(method))
                _holds.Add(method, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    public void Release(string method)
    {
        lock (_invocationLock)
        {
            if (_holds.TryGetValue(method, out var hold))
                hold.TrySetResult();
        }
    }

    public async Task SendOperatorCallbacksAsync(CancellationToken cancellationToken)
    {
        var clients = GetConnectedClients(out var connectionId);
        await clients.Client(connectionId).SendAsync("operatorState", OperatorStatus, cancellationToken);
        await clients.Client(connectionId).SendAsync("operatorLog", ActivityBatch, cancellationToken);
        await clients.Client(connectionId).SendAsync("resyncRequired", new ControlResyncRequired("fixture-resync"), cancellationToken);
    }

    public async Task SendGuestCallbacksAsync(CancellationToken cancellationToken)
    {
        var clients = GetConnectedClients(out var connectionId);
        await clients.Client(connectionId).SendAsync("guestState", GuestStatus, cancellationToken);
        await clients.Client(connectionId).SendAsync("resyncRequired", new ControlResyncRequired("fixture-resync"), cancellationToken);
    }

    /// <summary>
    /// Aborts the active connection, then attempts ordinary callbacks to test terminal delivery.
    /// </summary>
    /// <param name="role">The subscribed client role whose callbacks are attempted.</param>
    public async Task AbortAndAttemptOrdinaryCallbacksAsync(string role, CancellationToken cancellationToken)
    {
        await WaitForConnectionAsync(cancellationToken);
        Action abort;
        string connectionId;
        lock (_connectionLock)
        {
            abort = _abortConnection ?? throw new InvalidOperationException("The hub connection was not registered.");
            connectionId = _connectionId ?? throw new InvalidOperationException("The hub connection has no identifier.");
        }

        abort();
        try
        {
            var clients = (_hubContext ?? throw new InvalidOperationException("The hub context was not initialized."))
                .Clients.Client(connectionId);
            if (role == "operator")
            {
                await clients.SendAsync("operatorState", OperatorStatus, cancellationToken: cancellationToken);
                await clients.SendAsync("operatorLog", ActivityBatch, cancellationToken: cancellationToken);
            }
            else
            {
                await clients.SendAsync("guestState", GuestStatus, cancellationToken: cancellationToken);
            }
        }
        catch (Exception)
        {
            // An abort may remove the connection before the test host can enqueue this ordinary callback.
        }
        finally
        {
            _ordinaryCallbackAttempted.TrySetResult();
        }
    }

    internal void RegisterConnection(HubCallerContext context)
    {
        var request = context.GetHttpContext()
            ?? throw new InvalidOperationException("The loopback hub connection did not include an HTTP context.");

        lock (_connectionLock)
        {
            _connectionId = context.ConnectionId;
            _abortConnection = context.Abort;
            AuthorizationHeader = request.Request.Headers.Authorization.ToString();
            ConnectionPath = request.Request.Path;
            UsedWebSockets = request.WebSockets.IsWebSocketRequest;
        }
        Interlocked.Increment(ref _connectionCount);
        _connected.TrySetResult();
    }

    internal void RegisterDisconnection()
    {
        Interlocked.Increment(ref _disconnectionCount);
        _disconnected.TrySetResult();
    }

    internal async Task<ControlAcknowledgement> SubscribeOperatorAsync(
        IClientProxy caller, string correlation, long afterSequence)
    {
        var acknowledgement = await AcknowledgeAsync("SubscribeOperator", [correlation, afterSequence]);
        if (acknowledgement.Accepted && SendOperatorStateOnSubscribe)
            await caller.SendAsync("operatorState", OperatorStatus);
        return acknowledgement;
    }

    internal Task<ControlAcknowledgement> SubscribeGuestAsync(string correlation) =>
        AcknowledgeAsync("SubscribeGuest", [correlation]);

    internal async Task<GuestJoinReceipt> JoinAsync(GuestJoinRequest request)
    {
        var acknowledgement = await RecordAsync("Join", [request]);
        return acknowledgement.Accepted
            ? new("joined", request.Correlation)
            : new(acknowledgement.Code, acknowledgement.Correlation) { Accepted = false };
    }

    internal Task<ControlAcknowledgement> AcknowledgeAsync(string method, params object?[] arguments) =>
        RecordAsync(method, arguments);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_invocationLock)
        {
            foreach (var hold in _holds.Values)
                hold.TrySetResult();
        }

        if (_application is not null)
        {
            try { await _application.StopAsync(); }
            finally { await _application.DisposeAsync(); }
        }
    }

    public IOptions<ControlClientOptions> CreateOptions(int commandTimeoutSeconds = 2) =>
        Options.Create(CreateClientOptions(commandTimeoutSeconds));

    public ControlClientOptions CreateClientOptions(int commandTimeoutSeconds = 2) =>
        new()
        {
            ControlOrigin = ControlOrigin,
            ServiceKey = ServiceKeyValue,
            KeepAliveSeconds = 1,
            ServerTimeoutSeconds = 3,
            CommandTimeoutSeconds = commandTimeoutSeconds
        };

    public static OperatorLiveControlStatus OperatorStatus =>
        new(
            new(
                new ControlSnapshot(
                    "paused",
                    "fixture",
                    1,
                    0,
                    true,
                    false,
                    new(MotionDeviceState.StrokeEngineIdle, 0, 10, 50, 20, 0, MotionPattern.SimpleStroke, 0),
                    null,
                    new ControlLimits(1, 1),
                    1,
                    "none",
                    null,
                    null,
                    0,
                    null),
                new AuthorityPrecondition("fixture", 1, new GuestAdmission(null, null, "none")),
                1),
            new Invitation("fixture", "http://127.0.0.1/fixture", null, true));

    public static ControlActivityBatch ActivityBatch =>
        new(1, 1, 1, 1, null, false, [new ControlActivityEntry(1, DateTimeOffset.UnixEpoch, "Information", "fixture")]);

    public static GuestStatus GuestStatus =>
        new(
            "paused",
            "fixture",
            1,
            0,
            true,
            false,
            new ControlValues(0, 0, 10, 20, 50),
            null,
            null,
            new GuestLimits(1, 1),
            1,
            "none",
            null,
            0,
            null);

    private IHubClients GetConnectedClients(out string connectionId)
    {
        lock (_connectionLock)
        {
            connectionId = _connectionId ?? throw new InvalidOperationException("The hub connection has no identifier.");
        }
        return (_hubContext ?? throw new InvalidOperationException("The hub context was not initialized.")).Clients;
    }

    private async Task<ControlAcknowledgement> RecordAsync(string method, object?[] arguments)
    {
        TaskCompletionSource? hold;
        string? rejection;
        lock (_invocationLock)
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
}
