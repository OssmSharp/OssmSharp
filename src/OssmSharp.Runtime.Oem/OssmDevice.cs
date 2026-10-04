using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem;

public sealed class OssmDevice : IOssmDevice
{
    /// <summary>
    /// Maximum byte length of an encoded request, response, or complete sensor frame.
    /// </summary>
    private const int MaximumMessageBytes = 509;

    /// <summary>
    /// Byte length of the sensor-frame header preceding the motion JSON payload.
    /// </summary>
    private const int StreamHeaderBytes = 20;

    /// <summary>
    /// Associates peripherals with their owning clients for exclusive connections and lifecycle callbacks.
    /// </summary>
    private static readonly ConditionalWeakTable<IPeripheral, OssmDevice> Owners = [];

    /// <summary>
    /// Protects lifecycle, request, and last known state from concurrent calls and provider callbacks.
    /// </summary>
    private readonly Lock lifecycleSync = new();

    /// <summary>
    /// Serializes non-Stop operations and provider cleanup.
    /// </summary>
    private readonly SemaphoreSlim io = new(1, 1);

    /// <summary>
    /// Cancels operation waits and cooperative work when disposal starts.
    /// </summary>
    private readonly CancellationTokenSource shutdown = new();

    /// <summary>
    /// Host-owned bluetooth manager for access checks and peripheral lookup.
    /// </summary>
    private readonly IBleManager manager;

    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly OssmDeviceOptions options;

    /// <summary>
    /// Protects per-connection state and pending request and read ownership.
    /// </summary>
    private readonly Lock sync = new();

    /// <summary>
    /// Serializes issued GATT writes without holding the gate while awaiting terminal responses.
    /// </summary>
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>
    /// Correlates outstanding request identifiers with their terminal-response completions.
    /// </summary>
    private readonly Dictionary<uint, TaskCompletionSource<Response>> requests = [];

    /// <summary>
    /// Shared Stop result joined by concurrent Stop callers, disconnect, and disposal while still in progress.
    /// </summary>
    private Task? stop;

    /// <summary>
    /// Latest accepted settings request; newer requests supersede its unsent commands.
    /// </summary>
    private SetOperationContext? request;

    /// <summary>
    /// Last observed controls, retained across disconnection and never inferred from writes.
    /// </summary>
    private DeviceState? state;

    private FirmwareSettings? transmitted;
    private long writeSequence;
    private long confirmedSpeedSequence;

    /// <summary>
    /// Connection failure retained with its stack trace until a successful reconnection.
    /// </summary>
    private ExceptionDispatchInfo? providerFailure;

    /// <summary>
    /// Shared cleanup result returned by every disposal call, including when cleanup fails.
    /// </summary>
    private Task? disposal;

    /// <summary>
    /// Per-connection cancellation source for interrupting setup and live reads during teardown.
    /// </summary>
    private CancellationTokenSource? connectionCancellation;

    /// <summary>
    /// Tracks connection cancellation so cleanup waits for callbacks before disposing the source.
    /// </summary>
    private Task? connectionStopping;

    /// <summary>
    /// Increments when Stop or disarming must invalidate a Reset that captured the previous version.
    /// </summary>
    private long interruptionVersion;

    /// <summary>
    /// Identifies the current connection attempt; Connect, Disconnect, and disposal increment it to supersede earlier attempts.
    /// </summary>
    private long connection;

    /// <summary>
    /// Indicates that connection setup is in progress and has not yet been armed, failed, or interrupted.
    /// </summary>
    private bool connecting;

    /// <summary>
    /// Indicates an established connection that has not been disconnected, faulted, or disposed.
    /// </summary>
    private bool connected;

    /// <summary>
    /// Indicates that provider connection resources may still need <see cref="DisconnectCoreAsync"/>.
    /// </summary>
    private bool cleanupPending;

    /// <summary>
    /// Indicates that settings may be sent; only a successful connection or Reset sets it.
    /// </summary>
    private bool armed;

    /// <summary>
    /// Indicates that disposal has started; new operations are rejected and provider callbacks are ignored.
    /// </summary>
    private bool disposing;

    /// <summary>
    /// Completion signal for the priority Stop transfer; ordinary writes yield while it is pending.
    /// </summary>
    private TaskCompletionSource? stopTransfer;

    /// <summary>
    /// Owns response, motion-stream, and characteristic-subscription observers for the current connection.
    /// </summary>
    private CompositeDisposable subscriptions = [];

    /// <summary>
    /// Owns connection-status and connection-failure observers until disconnection is acknowledged.
    /// </summary>
    private CompositeDisposable lifecycle = [];

    /// <summary>
    /// Signals native disconnection for the current connection generation.
    /// </summary>
    /// <remarks>
    /// Replaced before teardown so an earlier loss notification cannot acknowledge a later connection closure.
    /// </remarks>
    private TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Pending live read, completed by the next valid motion observation or failed when the connection is abandoned.
    /// </summary>
    private TaskCompletionSource<DeviceState>? liveRead;

    /// <summary>
    /// Per-connection cancellation source that stops lease acquisition and renewal during teardown.
    /// </summary>
    private CancellationTokenSource? leaseStopping;

    /// <summary>
    /// Lease acquisition and renewal work, awaited during teardown before releasing control ownership.
    /// </summary>
    private Task leaseMaintenance = Task.CompletedTask;

    /// <summary>
    /// Peripheral retained until native disconnection and resource cleanup finish.
    /// </summary>
    private IPeripheral? peripheral;

    /// <summary>
    /// Identifies the current connection attempt so callbacks from earlier connections are ignored.
    /// </summary>
    private long generation;

    /// <summary>
    /// Indicates that the current connection generation has not been abandoned.
    /// </summary>
    private bool active;

    /// <summary>
    /// Request identifier counter; allocation skips zero and identifiers still in use.
    /// </summary>
    private uint nextRequestId;

    /// <summary>
    /// Control-lease token returned by acquisition; zero means no lease has been acquired for this connection.
    /// </summary>
    private uint lease;

    /// <summary>
    /// Motion-stream identifier used to reject other streams; zero means no stream has started for this connection.
    /// </summary>
    private byte streamId;

    /// <summary>
    /// Initializes a caller-owned, reusable client for a device selected by its discovered platform identifier.
    /// </summary>
    /// <param name="manager">The host-owned bluetooth manager used for discovery.</param>
    /// <param name="identifier">The opaque identifier returned by <see cref="IDeviceDiscovery"/>.</param>
    /// <param name="timeProvider">The clock used to renew the control lease before its reported expiry and to detect lost responses.</param>
    /// <param name="logger">The operational logger for Runtime's execution owner.</param>
    public OssmDevice(IBleManager manager, string identifier, TimeProvider timeProvider, ILogger<OssmDevice> logger)
        : this(manager, identifier, timeProvider, logger, Options.Create(new OssmDeviceOptions()))
    {
    }

    /// <summary>
    /// Initializes a caller-owned, reusable client with configured request limits for a device selected by its discovered platform identifier.
    /// </summary>
    /// <param name="manager">The host-owned bluetooth manager used for discovery.</param>
    /// <param name="identifier">The opaque identifier returned by <see cref="IDeviceDiscovery"/>.</param>
    /// <param name="timeProvider">The clock used to renew the control lease before its reported expiry and to detect lost responses.</param>
    /// <param name="logger">The operational logger for Runtime's execution owner.</param>
    /// <param name="options">The request retry and terminal-response wait limits.</param>
    public OssmDevice(IBleManager manager, string identifier, TimeProvider timeProvider, ILogger<OssmDevice> logger, IOptions<OssmDeviceOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        DeviceId = identifier;
        this.manager = manager;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.options = options.Value;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public string? Name
    {
        get
        {
            lock (sync)
            {
                return peripheral?.Name;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (lifecycleSync)
            {
                return connected;
            }
        }
    }

    /// <inheritdoc />
    public string DeviceId { get; private init; }

    /// <inheritdoc />
    public bool IsArmed
    {
        get
        {
            lock (lifecycleSync)
            {
                return armed;
            }
        }
    }

    /// <inheritdoc />
    public Exception? ConnectionFailure
    {
        get
        {
            lock (lifecycleSync)
            {
                return providerFailure?.SourceException;
            }
        }
    }

    internal FirmwareSettings? Transmitted
    {
        get
        {
            lock (lifecycleSync)
            {
                return transmitted;
            }
        }
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default) => ExecuteAsync(
        async token =>
    {
        long attempt;
        lock (lifecycleSync)
        {
            if (connected)
            {
                return true;
            }

            attempt = ++connection;
        }

        logger.OperationStarted("connect");
        await CleanupAsync().ConfigureAwait(false);
        CancellationTokenSource session;
        lock (lifecycleSync)
        {
            ThrowIfInterrupted(attempt);
            connecting = true;
            transmitted = null;
            cleanupPending = true;
            connectionCancellation = session = new();
            connectionStopping = null;
        }

        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        logger.ConnectionStateChanged("connecting");
        try
        {
            await ConnectCoreAsync(attemptCancellation.Token).ConfigureAwait(false);
            lock (lifecycleSync)
            {
                if (!disposing)
                {
                    ThrowIfInterrupted(attempt);
                }
            }

            attemptCancellation.Token.ThrowIfCancellationRequested();
            logger.ConnectionSetupStage("waiting for initial motion observation");
            UpdateState(await ReadStateAsync(attemptCancellation.Token).WaitAsync(attemptCancellation.Token).ConfigureAwait(false));
            lock (lifecycleSync)
            {
                ThrowIfInterrupted(attempt);
                if (!connecting)
                {
                    // SetFaulted ended this attempt during setup.
                    providerFailure!.Throw();
                }

                connecting = false;
                connected = true;
                providerFailure = null;
                armed = true;
            }

            logger.OperationCompleted("connect");
            logger.ConnectionStateChanged("connected");
            NotifyStateChanged();
            return true;
        }
        catch (Exception connectionError)
        {
            bool reported;
            lock (lifecycleSync)
            {
                if (attempt == connection)
                {
                    connecting = false;
                }

                reported = ReferenceEquals(connectionError, providerFailure?.SourceException);
            }

            if (connectionError is OperationCanceledException)
            {
                logger.ConnectionCancelled();
            }
            else if (!reported)
            {
                logger.OperationFailed("connect", connectionError);
            }

            try
            {
                await CleanupAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Connection setup and cleanup both failed.", connectionError, cleanupError);
            }

            throw;
        }
    },
        cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        long disconnection;
        Task? pendingStop;
        TaskCompletionSource? started;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            pendingStop = PrepareStop(out started);
            disconnection = ++connection;
            connecting = false;
            connected = false;
            changed = DisarmLocked();
            connectionStopping ??= connectionCancellation?.CancelAsync();
        }

        if (changed)
        {
            NotifyStateChanged();
        }

        if (started is not null)
        {
            _ = CompleteStopAsync(started);
        }

        await ExecuteAsync(
            async _ =>
        {
            lock (lifecycleSync)
            {
                if (disconnection != connection)
                {
                    // A later connection attempt owns cleanup of the connection this call ended.
                    return true;
                }
            }

            await CleanupAsync(pendingStop).ConfigureAwait(false);
            return true;
        },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SetResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        logger.ApiCalled("Set");
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetOperationContext current;
        lock (lifecycleSync)
        {
            EnsureConnected();
            if (stop is { IsCompleted: false })
            {
                throw new InvalidOperationException("Settings request was interrupted by Stop; submit again after it completes.");
            }

            if (!armed)
            {
                throw new InvalidOperationException("Commands are disarmed; explicitly reset before submitting settings.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                logger.SettingsFinished(SetStatus.Cancelled, 0);
                return new(SetStatus.Cancelled, 0);
            }

            current = Supersede(settings, pendingCancellation);
        }

        try
        {
            var result = await ExecuteAsync(
                _ => ApplySettingsAsync(current),
                requiresConnection: true,
                cancellationToken: current.PendingCancellation.Token).ConfigureAwait(false);
            logger.SettingsFinished(result.Status, result.CommandsWritten);
            return result;
        }
        catch (OperationCanceledException) when (current.PendingCancellation.IsCancellationRequested || disposing)
        {
            lock (lifecycleSync)
            {
                if (current.Status == SetStatus.Completed)
                {
                    current.Status = SetStatus.Cancelled;
                }

                logger.SettingsFinished(current.Status, current.CommandsWritten);
                return current.Result;
            }
        }
        finally
        {
            lock (lifecycleSync)
            {
                if (ReferenceEquals(request, current))
                {
                    request = null;
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<DeviceState?> GetStateAsync(bool bypassCache = false, CancellationToken cancellationToken = default)
    {
        CancellationToken connectionToken;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bypassCache)
            {
                return Task.FromResult(state);
            }

            EnsureConnected();
            connectionToken = connectionCancellation!.Token;
        }

        return ExecuteAsync<DeviceState?>(
            async token =>
        {
            try
            {
                var observation = await ReadStateAsync(token).WaitAsync(token).ConfigureAwait(false);
                UpdateState(observation);
                return observation;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Disarm();
                logger.OperationFailed("live read", exception);
                throw;
            }
        },
            requiresConnection: true,
            connectionToken: connectionToken,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("Stop");
        TaskCompletionSource? started = null;
        Task result;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (stop is { IsCompleted: false })
            {
                return stop;
            }

            if (!connecting)
            {
                EnsureConnected();
            }

            cancellationToken.ThrowIfCancellationRequested();
            InterruptLocked();
            if (connecting)
            {
                connection++;
                connecting = false;
                connectionStopping ??= connectionCancellation?.CancelAsync();
                result = Task.CompletedTask;
            }
            else
            {
                started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                result = stop = started.Task;
            }
        }

        if (started is not null)
        {
            _ = CompleteStopAsync(started);
        }

        return result;
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        long version;
        lock (lifecycleSync)
        {
            EnsureConnected();
            if (stop is { IsCompleted: false })
            {
                throw new InvalidOperationException("Reset was interrupted by disarming or Stop; explicitly reset again after it completes.");
            }

            version = interruptionVersion;
        }

        return ExecuteAsync(
            _ =>
        {
            bool changed;
            lock (lifecycleSync)
            {
                if (stop is { IsCompleted: false } || version != interruptionVersion)
                {
                    throw new InvalidOperationException("Reset was interrupted by disarming or Stop; explicitly reset again after it completes.");
                }

                changed = !armed;
                armed = true;
            }

            logger.OperationCompleted("reset");
            if (changed)
            {
                NotifyStateChanged();
            }

            return Task.FromResult(true);
        },
            requiresConnection: true,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Disarms, discards commands not yet sent, sends or joins Stop, releases connection resources and subscribers,
    /// and prevents reconnection.
    /// </summary>
    /// <returns>Completion of cleanup; repeated calls observe the same cleanup result.</returns>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        TaskCompletionSource? started;
        Task? pendingStop;
        Task disposingTask;
        bool changed;
        lock (lifecycleSync)
        {
            if (disposal is not null)
            {
                return new(disposal);
            }

            pendingStop = PrepareStop(out started);
            disposing = true;
            connection++;
            connecting = false;
            connected = false;
            changed = DisarmLocked();
            connectionStopping ??= connectionCancellation?.CancelAsync();
            disposingTask = disposal = CompleteDisposalAsync(pendingStop, shutdown.CancelAsync(), changed);
        }

        if (started is not null)
        {
            _ = CompleteStopAsync(started);
        }

        return new(disposingTask);
    }

    internal static void PeripheralStateChanged(IPeripheral peripheral)
    {
        if (peripheral.Status == ConnectionState.Disconnected && Owners.TryGetValue(peripheral, out var owner))
        {
            owner.ConnectionLost(new IOException("The bluetooth connection was lost."));
        }
    }

    /// <summary>
    /// Records failure of the current connection, disarms, and discards settings not yet sent, without sending anything.
    /// </summary>
    /// <param name="exception">The connection or notification failure to propagate to subsequent operations.</param>
    /// <remarks>
    /// Last known state remains available from cached reads. Reset cannot clear the failure; a successful
    /// reconnection can. Cleanup still waits for sent commands to finish. Calls without an active or pending connection are ignored.
    /// </remarks>
    private void SetFaulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (lifecycleSync)
        {
            if (disposing || !(connecting || connected))
            {
                return;
            }

            providerFailure = ExceptionDispatchInfo.Capture(exception);
            connecting = false;
            connected = false;
            DisarmLocked();
        }

        logger.OperationFailed("provider connection", exception);
        NotifyStateChanged();
    }

    /// <summary>
    /// Records an incoming observation without changing arming or cancelling settings.
    /// </summary>
    /// <param name="observation">Decoded controls from a provider read or notification.</param>
    /// <remarks>
    /// Observations never cause motion writes and are ignored without an active or pending connection.
    /// Subscriber failures are logged and cannot interrupt device execution.
    /// </remarks>
    private void UpdateState(DeviceState observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool changed;
        lock (lifecycleSync)
        {
            if (disposing || !(connecting || connected))
            {
                return;
            }

            changed = state != observation;
            state = observation;
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Establishes fresh connectivity and subscriptions that deliver observations through <see cref="UpdateState"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation of connection setup.</param>
    /// <returns>Completion of setup, without any motion writes.</returns>
    /// <remarks>
    /// The initial state is read through <see cref="ReadStateAsync"/> before arming.
    /// Failure or cancellation releases partial setup through <see cref="DisconnectCoreAsync"/>.
    /// Observations, responses, and failures from earlier connections must not reach this connection.
    /// </remarks>
    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrappers add a timeout that abandons issued GATT I/O; the raw observables are awaited to settlement.")]
    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.ConnectionSetupStage("requesting bluetooth access");
        var access = await manager.RequestAccessAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (access != AccessState.Available)
        {
            throw new InvalidOperationException($"Bluetooth access is {access}. Enable bluetooth and grant the application bluetooth permission.");
        }

        logger.ConnectionSetupStage("resolving peripheral after bluetooth access was granted");
        var selected = manager.GetKnownPeripheral(DeviceId)
            ?? throw new InvalidOperationException("The selected bluetooth device is unavailable. Discover devices again before connecting.");
        if (selected.Status != ConnectionState.Disconnected)
        {
            throw new InvalidOperationException("The selected bluetooth device is already in use by another connection.");
        }

        if (!Owners.TryAdd(selected, this))
        {
            throw new InvalidOperationException("The selected bluetooth device is already owned by another connection.");
        }

        long current;
        CompositeDisposable owned;
        CompositeDisposable events;
        var stopping = new CancellationTokenSource();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            current = ++generation;
            active = true;
            peripheral = selected;
            disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            subscriptions = owned = [];
            lifecycle = events = [];
            leaseStopping = stopping;
            nextRequestId = 0;
            lease = 0;
            streamId = 0;
        }

        var connectIssued = false;
        events.Add(selected.WhenStatusChanged().Skip(1)
            .Where(status => status == ConnectionState.Disconnected)
            .Subscribe(
                _ => ConnectionLost(current, new IOException("The bluetooth connection was lost.")),
                exception => Fail(current, exception)));
        events.Add(
            selected.WhenConnectionFailed().Subscribe(
                exception =>
                {
                    if (Volatile.Read(ref connectIssued))
                    {
                        ConnectionLost(current, exception);
                    }
                },
                exception => Fail(current, exception)));
        Volatile.Write(ref connectIssued, true);
        logger.ConnectionSetupStage("connecting peripheral");
        await selected.ConnectAsync(new ConnectionConfig(AutoConnect: false), cancellationToken).ConfigureAwait(false);
        logger.ConnectionSetupStage("reading protocol information");
        var protocolInfo = await selected.ReadCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.ProtocolInfo).ToTask().ConfigureAwait(false);
        ValidateProtocolInfo(protocolInfo.Data);
        logger.ConnectionSetupStage("subscribing to responses");
        await SubscribeAsync(current, selected, owned, BleProtocolConstants.Response, "response", true, HandleResponse, cancellationToken).ConfigureAwait(false);
        logger.ConnectionSetupStage("subscribing to motion observations");
        await SubscribeAsync(current, selected, owned, BleProtocolConstants.SensorStream, "sensor stream", false, HandleMotionObservation, cancellationToken).ConfigureAwait(false);
        logger.ConnectionSetupStage("acquiring control lease");
        lock (sync)
        {
            leaseMaintenance = MaintainLeaseAsync(current, acquired, stopping.Token);
        }

        await acquired.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        logger.ConnectionSetupStage("starting motion stream");
        await StartMotionStreamAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the firmware to run patterns before a request writes its controls.
    /// </summary>
    /// <param name="cancellationToken">Cancellation follows <see cref="WriteCommandAsync"/>.</param>
    /// <returns>Whether the firmware now runs patterns; false when it is waiting for homing or its speed knob.</returns>
    /// <remarks>
    /// Configures direct speed control, then selects the stroke engine and checks readiness from its terminal response.
    /// Homing and preflight report not ready.
    /// Called once per request, so it must succeed without effect when patterns already run.
    /// The firmware decides whether motion is safe to start.
    /// </remarks>
    private async Task<bool> PreparePatternAsync(CancellationToken cancellationToken)
    {
        long current;
        lock (sync)
        {
            current = generation;
        }

        await SendRequestAsync(current, "setting.write", "setting.speedKnobAsLimit", new JsonObject { ["value"] = false }, leased: true, cancellationToken).ConfigureAwait(false);
        var response = await SendRequestAsync(current, "target.set", "target.strokeEngine", null, leased: true, cancellationToken).ConfigureAwait(false);
        return response.StateAfter is "strokeEngine.idle" or "strokeEngine.pattern";
    }

    /// <summary>
    /// Reads and decodes live firmware controls without a provider-side state cache.
    /// </summary>
    /// <param name="cancellationToken">Cancellation of the observation wait.</param>
    /// <returns>A decoded observation; malformed responses and provider errors must propagate.</returns>
    /// <remarks>
    /// Waits for the next valid motion-stream observation; cancellation abandons only the wait.
    /// </remarks>
    private async Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        var read = new TaskCompletionSource<DeviceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (!active || streamId == 0)
            {
                throw new IOException("The motion stream is not active.");
            }

            liveRead = read;
        }

        try
        {
            return await read.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                if (liveRead == read)
                {
                    liveRead = null;
                }
            }
        }
    }

    /// <summary>
    /// Releases per-connection resources after sent commands finish, including partial setup.
    /// </summary>
    /// <returns>Completion of cleanup.</returns>
    /// <remarks>
    /// Called once per connection attempt that needs cleanup, and again after a failed cleanup.
    /// Repeated calls keep the instance reusable for another <see cref="ConnectCoreAsync"/>.
    /// Stops the stream and releases the lease only while the connection is still usable; the firmware also
    /// releases both when the bluetooth connection closes. A release failure propagates after teardown completes.
    /// </remarks>
    private async Task DisconnectCoreAsync()
    {
        IPeripheral? selected;
        long current;
        CancellationTokenSource? stopping;
        Task maintenance;
        CompositeDisposable owned;
        CompositeDisposable events;
        bool acquiring;
        lock (sync)
        {
            selected = peripheral;
            if (selected is null)
            {
                return;
            }

            current = generation;
            stopping = leaseStopping;
            maintenance = leaseMaintenance;
            owned = subscriptions;
            events = lifecycle;
            acquiring = lease == 0;
        }

        var closed = new IOException("The bluetooth connection was closed.");
        if (acquiring)
        {
            // Without a lease, an unanswered acquisition would otherwise wait forever; its GATT write still settles.
            Abandon(current, closed);
        }

        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        await maintenance.ConfigureAwait(false);
        bool leased;
        bool streaming;
        lock (sync)
        {
            leased = active && lease != 0;
            streaming = active && streamId != 0;
        }

        ExceptionDispatchInfo? releaseFailure = null;
        if (leased && selected.Status == ConnectionState.Connected)
        {
            try
            {
                if (streaming)
                {
                    await SendRequestAsync(current, "stream.stop", null, null, leased: true, CancellationToken.None).ConfigureAwait(false);
                }

                await SendRequestAsync(current, "control.release", null, null, leased: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                releaseFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        Abandon(current, closed);
        owned.Dispose();
        TaskCompletionSource lost;
        lock (sync)
        {
            // An earlier loss acknowledgement does not cover a reconnection made by Shiny's notification subscriptions.
            lost = disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        selected.CancelConnection();
        if (selected.Status == ConnectionState.Disconnected)
        {
            lost.TrySetResult();
        }

        await lost.Task.ConfigureAwait(false);
        events.Dispose();
        Owners.Remove(selected);
        lock (sync)
        {
            peripheral = null;
            leaseStopping = null;
            leaseMaintenance = Task.CompletedTask;
        }

        stopping?.Dispose();
        releaseFailure?.Throw();
    }

    /// <summary>
    /// Sends one primitive assignment and awaits the issued transfer and, unless abandoned, its terminal response.
    /// </summary>
    /// <param name="command">The already validated firmware control assignment.</param>
    /// <param name="cancellationToken">Cancellation of the terminal-response wait, not the issued transfer.</param>
    /// <returns>Completion of the issued transfer and, unless abandoned, its terminal response.</returns>
    /// <remarks>
    /// A cancelled setting may abandon its terminal response, but its issued transfer must settle before this hook returns.
    /// Stop uses an uncancellable token and awaits its terminal response without waiting for an earlier response or live read.
    /// Do not interpret patterns or accept complete settings here.
    /// </remarks>
    private Task WriteCommandAsync(FirmwareCommand command, CancellationToken cancellationToken)
    {
        var path = command.Setting switch
        {
            FirmwareSetting.Pattern => "motion.pattern",
            FirmwareSetting.Speed => "motion.speed",
            FirmwareSetting.Stroke => "motion.stroke",
            FirmwareSetting.Depth => "motion.depth",
            FirmwareSetting.Sensation => "motion.sensation",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command.Setting, "Unknown firmware control."),
        };
        long current;
        lock (sync)
        {
            current = generation;
        }

        return SendRequestAsync(current, "setting.write", path, new JsonObject { ["value"] = command.Value }, leased: true, cancellationToken);
    }

    private static void ValidateProtocolInfo(byte[]? data)
    {
        try
        {
            using var document = JsonDocument.Parse(data ?? []);
            if (TryGetString(document.RootElement, "protocol", out var protocol)
                && protocol == "rad-ble"
                && TryGetUInt32(document.RootElement, "version", out var version)
                && version == 1)
            {
                return;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException("The device does not report RAD BLE protocol version 1.");
    }

    private static bool TryGetUInt32(JsonElement element, string name, out uint value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetUInt32(out value);
    }

    private static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        return value is not null;
    }

    private static TimeSpan ReadLeaseLifetime(JsonElement result) => TryGetUInt32(result, "ttlMs", out var milliseconds) && milliseconds != 0
        ? TimeSpan.FromMilliseconds(milliseconds)
        : throw new InvalidDataException("The device did not report a positive control lease lifetime.");

    private static Response ParseResponse(byte[]? data)
    {
        if (data is null || data.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("A RAD response must contain at most 509 bytes.");
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (!TryGetUInt32(root, "v", out var version)
            || version != 1
            || !TryGetUInt32(root, "id", out var id)
            || !TryGetString(root, "stage", out var stage)
            || stage is not ("accepted" or "completed" or "failed")
            || !root.TryGetProperty("ok", out var status)
            || status.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("A RAD response requires v=1, an id, a known stage, and ok.");
        }

        var ok = status.GetBoolean();
        TryGetString(root, "code", out var code);
        TryGetString(root, "message", out var message);
        TryGetString(root, "stateAfter", out var stateAfter);
        var result = root.TryGetProperty("result", out var value) ? value.Clone() : default;
        return new(id, stage, ok, code, message, result, stateAfter);
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, bool requiresConnection = false, CancellationToken connectionToken = default, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource waiting;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token, connectionToken);
        }

        try
        {
            await io.WaitAsync(waiting.Token).ConfigureAwait(false);
            try
            {
                waiting.Token.ThrowIfCancellationRequested();
                if (requiresConnection)
                {
                    lock (lifecycleSync)
                    {
                        EnsureConnected();
                    }
                }

                return await operation(waiting.Token).ConfigureAwait(false);
            }
            finally
            {
                io.Release();
            }
        }
        finally
        {
            waiting.Dispose();
        }
    }

    private async Task<SetResult> ApplySettingsAsync(SetOperationContext current)
    {
        lock (lifecycleSync)
        {
            if (IsDiscardedLocked(current))
            {
                return current.Result;
            }
        }

        var ready = false;
        var prepared = await TryStepAsync(
            current,
            async () => ready = await PreparePatternAsync(current.PendingCancellation.Token).ConfigureAwait(false),
            exception => new IOException($"Preparing the device to run the pattern failed; no settings were sent. {exception.Message}", exception)).ConfigureAwait(false);

        if (!prepared)
        {
            return current.Result;
        }

        if (!ready)
        {
            throw new InvalidOperationException("The device is not ready to run patterns. Wait for homing to finish and turn the speed knob to zero, then submit the settings again.");
        }

        foreach (var command in current.Settings.ToCommands())
        {
            long sequence;
            lock (lifecycleSync)
            {
                if (IsDiscardedLocked(current))
                {
                    return current.Result;
                }

                sequence = ++writeSequence;
            }

            logger.CommandStarted(command.Setting, command.Value);
            if (!await TryStepAsync(
                current,
                () => WriteCommandAsync(command, current.PendingCancellation.Token),
                exception => new DeviceWriteException(current.CommandsWritten, exception)).ConfigureAwait(false))
            {
                return current.Result;
            }

            logger.CommandCompleted(command.Setting);
            current.CommandsWritten++;
            RecordTransmitted(command, sequence);
        }

        lock (lifecycleSync)
        {
            if (current.PendingCancellation.IsCancellationRequested && current.Status == SetStatus.Completed)
            {
                current.Status = SetStatus.Cancelled;
            }

            return current.Result;
        }
    }

    /// <summary>
    /// Runs one step of a request, returning false when the request was discarded while the step ran.
    /// </summary>
    private async Task<bool> TryStepAsync(SetOperationContext current, Func<Task> step, Func<Exception, IOException> failure)
    {
        try
        {
            await step().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (current.PendingCancellation.IsCancellationRequested)
        {
            Exception? connectionError;
            lock (lifecycleSync)
            {
                connectionError = providerFailure?.SourceException;
                if (connectionError is null)
                {
                    if (current.Status == SetStatus.Completed)
                    {
                        current.Status = SetStatus.Cancelled;
                    }

                    return false;
                }
            }

            logger.OperationFailed("settings write", connectionError);
            throw failure(connectionError);
        }
        catch (Exception exception)
        {
            Disarm();
            logger.OperationFailed("settings write", exception);
            throw failure(exception);
        }
    }

    private bool IsDiscardedLocked(SetOperationContext current)
    {
        if (!current.PendingCancellation.IsCancellationRequested && armed && !disposing)
        {
            return false;
        }

        if (current.Status == SetStatus.Completed)
        {
            current.Status = SetStatus.Cancelled;
        }

        return true;
    }

    private SetOperationContext Supersede(MotionSettings settings, CancellationTokenSource pendingCancellation)
    {
        if (request is not null)
        {
            request.Status = SetStatus.Superseded;
            request.PendingCancellation.Cancel();
        }

        request = new(settings, pendingCancellation);
        return request;
    }

    private void CancelPending()
    {
        if (request is not null)
        {
            request.Status = SetStatus.Cancelled;
            request.PendingCancellation.Cancel();
        }
    }

    private void InterruptLocked()
    {
        interruptionVersion++;
        CancelPending();
    }

    private bool DisarmLocked()
    {
        var changed = armed;
        armed = false;
        InterruptLocked();
        return changed;
    }

    private void Disarm()
    {
        bool changed;
        lock (lifecycleSync)
        {
            changed = DisarmLocked();
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposing, this);
        if (!connected)
        {
            providerFailure?.Throw();
            throw new InvalidOperationException("Connect before using the device.");
        }
    }

    private void ThrowIfInterrupted(long attempt)
    {
        ObjectDisposedException.ThrowIf(disposing, this);
        if (attempt != connection)
        {
            throw new InvalidOperationException("The connection attempt was interrupted by a disconnect.");
        }
    }

    private async Task CleanupAsync(Task? joinedStop = null)
    {
        Task? pendingStop;
        Task? stopping;
        lock (lifecycleSync)
        {
            if (!cleanupPending)
            {
                return;
            }

            pendingStop = joinedStop ?? (stop is { IsCompleted: false } ? stop : null);
            stopping = connectionStopping;
        }

        logger.OperationStarted("connection cleanup");
        logger.ConnectionStateChanged("disconnecting");
        List<Exception> failures = [];
        try
        {
            if (stopping is not null)
            {
                await stopping.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (pendingStop is not null)
            {
                await pendingStop.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupError) when (failures.Count > 0)
        {
            failures.Add(cleanupError);
            throw new AggregateException("Cancellation, Stop, and cleanup failed.", failures);
        }

        lock (lifecycleSync)
        {
            cleanupPending = false;
            connectionCancellation?.Dispose();
            connectionCancellation = null;
            connectionStopping = null;
            stop = null;
        }

        logger.ConnectionStateChanged("disconnected");
        if (failures.Count > 1)
        {
            throw new AggregateException("Cancellation and Stop failed.", failures);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        logger.OperationCompleted("connection cleanup");
    }

    /// <summary>
    /// Returns the pending Stop or reserves a new one when connected, without sending a command.
    /// </summary>
    /// <param name="started">The newly reserved completion source, or null when no new Stop is needed.</param>
    /// <returns>Completion of the pending or reserved Stop, or null when neither exists.</returns>
    /// <remarks>
    /// The caller must hold <see cref="lifecycleSync"/>. When <paramref name="started"/> is non-null,
    /// the caller must invoke <see cref="CompleteStopAsync"/> after releasing the lock to send the Stop command.
    /// </remarks>
    private Task? PrepareStop(out TaskCompletionSource? started)
    {
        started = null;
        if (connected && stop is not { IsCompleted: false })
        {
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stop = started.Task;
        }

        return stop is { IsCompleted: false } ? stop : null;
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            await WriteStopAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            Disarm();
            completion.TrySetException(exception);
        }
    }

    private async Task WriteStopAsync()
    {
        var command = new FirmwareCommand(FirmwareSetting.Speed, 0);
        long sequence;
        lock (lifecycleSync)
        {
            sequence = ++writeSequence;
        }
        logger.OperationStarted("Stop");
        try
        {
            await WriteCommandAsync(command, CancellationToken.None).ConfigureAwait(false);
            RecordTransmitted(command, sequence);
            logger.OperationCompleted("Stop");
            logger.StopCompleted();
        }
        catch (Exception exception)
        {
            logger.OperationFailed("Stop write", exception);
            throw;
        }
    }

    private void NotifyStateChanged()
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                logger.SubscriberFailed(exception);
            }
        }
    }

    private void RecordTransmitted(FirmwareCommand command, long sequence)
    {
        lock (lifecycleSync)
        {
            var previous = transmitted ?? new();
            if (command.Setting == FirmwareSetting.Speed)
            {
                if (sequence < confirmedSpeedSequence)
                {
                    return;
                }

                confirmedSpeedSequence = sequence;
            }

            transmitted = command.Setting switch
            {
                FirmwareSetting.Pattern => previous with { Pattern = command.Value },
                FirmwareSetting.Speed => previous with { DeviceVelocityPercent = command.Value },
                FirmwareSetting.Stroke => previous with { Stroke = command.Value },
                FirmwareSetting.Depth => previous with { Depth = command.Value },
                FirmwareSetting.Sensation => previous with { Sensation = command.Value },
                _ => previous,
            };
        }
    }

    private async Task CompleteDisposalAsync(Task? pendingStop, Task cancellation, bool changed)
    {
        await Task.Yield();
        try
        {
            if (changed)
            {
                NotifyStateChanged();
            }

            await cancellation.ConfigureAwait(false);
            await io.WaitAsync().ConfigureAwait(false);
            try
            {
                await CleanupAsync(pendingStop).ConfigureAwait(false);
            }
            finally
            {
                io.Release();
            }
        }
        finally
        {
            lock (lifecycleSync)
            {
                StateChanged = null;
            }

            io.Dispose();
            shutdown.Dispose();
        }
    }

    private async Task SubscribeAsync(
        long current,
        IPeripheral selected,
        CompositeDisposable owned,
        string characteristic,
        string name,
        bool indicate,
        Action<long, byte[]?> handler,
        CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owned.Add(
            selected.WhenCharacteristicSubscriptionChanged(BleProtocolConstants.Service, characteristic).Subscribe(
                info =>
                {
                    if (info.IsNotifying)
                    {
                        ready.TrySetResult();
                    }
                    else if (!ready.TrySetException(new IOException($"The bluetooth {name} subscription could not be enabled.")))
                    {
                        Fail(current, new IOException($"The bluetooth {name} subscription was lost."));
                    }
                },
                exception =>
                {
                    ready.TrySetException(exception);
                    Fail(current, exception);
                }));
        owned.Add(selected.NotifyCharacteristic(BleProtocolConstants.Service, characteristic, indicate).Subscribe(
            result => handler(current, result.Data),
            exception =>
            {
                ready.TrySetException(exception);
                Fail(current, exception);
            },
            () => Fail(current, new IOException($"The bluetooth {name} subscription ended."))));
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Response> SendRequestAsync(
        long current,
        string operation,
        string? path,
        JsonObject? arguments,
        bool leased,
        CancellationToken cancellationToken)
    {
        var response = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var priority = operation == "setting.write" && path == "motion.speed"
            && arguments?["value"]?.GetValue<int>() == 0
            && !cancellationToken.CanBeCanceled
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        IPeripheral selected;
        uint id;
        uint? token;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                throw new IOException("The bluetooth connection is no longer active.");
            }

            if (leased && lease == 0)
            {
                throw new InvalidOperationException("No control lease is held.");
            }

            do
            {
                id = unchecked(++nextRequestId);
            }
            while (id == 0 || requests.ContainsKey(id));

            token = leased ? lease : null;
            selected = peripheral!;
            requests.Add(id, response);
            if (priority is not null)
            {
                stopTransfer = priority;
            }
        }

        try
        {
            var payload = Request.EncodeWithArguments(id, operation, path, arguments, token);
            if (payload.Length > MaximumMessageBytes)
            {
                throw new InvalidOperationException($"The {operation} request exceeds {MaximumMessageBytes} bytes.");
            }

            for (var attempt = 1; ; attempt++)
            {
                logger.RequestSending(id, operation, path, attempt);
                await WriteRequestAsync(current, selected, payload, priority, cancellationToken).ConfigureAwait(false);
                logger.RequestTransferred(id, operation, attempt);
                try
                {
                    var terminalResponse = await response.Task.WaitAsync(options.ResponseTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
                    logger.RequestCompleted(id, terminalResponse.Ok);
                    return terminalResponse;
                }
                catch (TimeoutException) when (attempt < options.MaximumRequestAttempts)
                {
                    // The firmware replays a cached response for a repeated identifier and re-executes one it could not cache.
                    logger.RequestResent(operation);
                }
                catch (TimeoutException)
                {
                    var failure = new IOException($"The device did not answer the {operation} request.");
                    Fail(current, failure);
                    throw failure;
                }
            }
        }
        finally
        {
            priority?.TrySetResult();
            lock (sync)
            {
                if (requests.TryGetValue(id, out var pending) && pending == response)
                {
                    requests.Remove(id);
                }
            }
        }
    }

    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrapper adds a timeout that abandons issued GATT I/O; the raw observable is awaited to settlement.")]
    private async Task WriteRequestAsync(long current, IPeripheral selected, byte[] payload, TaskCompletionSource? priority, CancellationToken cancellationToken)
    {
        while (true)
        {
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task? higherPriority = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (!IsCurrent(current))
                    {
                        throw new IOException("The bluetooth connection is no longer active.");
                    }

                    if (priority is null && stopTransfer is { Task.IsCompleted: false })
                    {
                        higherPriority = stopTransfer.Task;
                    }
                }

                if (higherPriority is null)
                {
                    await selected
                        .WriteCharacteristic(BleProtocolConstants.Service, BleProtocolConstants.Request, payload, withResponse: true)
                        .ToTask()
                        .ConfigureAwait(false);
                    return;
                }
            }
            finally
            {
                writes.Release();
                priority?.TrySetResult();
            }

            await higherPriority.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleResponse(long current, byte[]? data)
    {
        Response response;
        try
        {
            response = ParseResponse(data);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // An uncorrelated response cannot complete a request; its sender resends after the response timeout.
            logger.ResponseDiscarded(exception);
            return;
        }

        TaskCompletionSource<Response>? pending;
        lock (sync)
        {
            if (!IsCurrent(current) || !requests.TryGetValue(response.Id, out pending))
            {
                return;
            }
        }

        logger.RequestResponseReceived(response.Id, response.Stage, response.Ok);
        // Accepted requests remain pending until a terminal stage.
        if (response.Stage == "completed" && response.Ok)
        {
            pending.TrySetResult(response);
        }
        else if (response.Stage != "accepted")
        {
            pending.TrySetException(new IOException($"The device rejected the request: {response.Code ?? "unknown"}{(response.Message is null ? string.Empty : $" ({response.Message})")}."));
        }
    }

    private async Task MaintainLeaseAsync(long current, TaskCompletionSource acquired, CancellationToken stopping)
    {
        TimeSpan lifetime;
        DateTimeOffset requested;
        try
        {
            requested = timeProvider.GetUtcNow();
            var result = (await SendRequestAsync(current, "control.acquire", null, null, leased: false, stopping).ConfigureAwait(false)).Result;
            if (!TryGetUInt32(result, "lease", out var token) || token == 0)
            {
                throw new InvalidDataException("The device did not return a control lease.");
            }

            lifetime = ReadLeaseLifetime(result);
            lock (sync)
            {
                if (!IsCurrent(current))
                {
                    throw new IOException("The bluetooth connection is no longer active.");
                }

                lease = token;
            }

            logger.LeaseAcquired(lifetime.TotalMilliseconds);
            acquired.TrySetResult();
        }
        catch (Exception exception)
        {
            acquired.TrySetException(exception);
            return;
        }

        while (true)
        {
            var expires = requested + lifetime;
            try
            {
                var delay = requested + (lifetime / 2) - timeProvider.GetUtcNow();
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, timeProvider, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            requested = timeProvider.GetUtcNow();
            var renewal = SendRequestAsync(current, "control.renew", null, null, leased: true, stopping);
            logger.LeaseRenewalStarted((expires - timeProvider.GetUtcNow()).TotalMilliseconds);
            try
            {
                var remaining = expires - timeProvider.GetUtcNow();
                // Issued renewal is bounded by lease expiry, not by disconnect.
                lifetime = ReadLeaseLifetime((await renewal.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, CancellationToken.None).ConfigureAwait(false)).Result);
                logger.LeaseRenewed((timeProvider.GetUtcNow() - requested).TotalMilliseconds, lifetime.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (exception is TimeoutException)
                {
                    logger.LeaseRenewalExpired((expires - requested).TotalMilliseconds);
                }

                var failure = exception is TimeoutException
                    ? new IOException("The control lease expired before renewal completed; control ownership is lost.")
                    : new IOException("Control lease renewal failed; control ownership is lost.", exception);
                Fail(current, failure);
                try
                {
                    await renewal.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The connection failure above already reports this result.
                }

                return;
            }
        }
    }

    private async Task StartMotionStreamAsync(long current, CancellationToken cancellationToken)
    {
        var result = (await SendRequestAsync(current, "stream.start", "motion.speed", null, leased: true, cancellationToken).ConfigureAwait(false)).Result;
        if (!TryGetUInt32(result, "streamId", out var id)
            || id is 0 or > byte.MaxValue
            || !TryGetString(result, "surface", out var surface)
            || surface != "motion")
        {
            throw new InvalidDataException("The device did not start a motion stream.");
        }

        lock (sync)
        {
            if (!IsCurrent(current))
            {
                throw new IOException("The bluetooth connection is no longer active.");
            }

            streamId = (byte)id;
        }
    }

    private void HandleMotionObservation(long current, byte[]? data)
    {
        byte expectedStream;
        lock (sync)
        {
            if (!IsCurrent(current) || streamId == 0)
            {
                return;
            }

            expectedStream = streamId;
        }

        if (data is null || data.Length <= StreamHeaderBytes || data.Length > MaximumMessageBytes)
        {
            Fail(current, new InvalidDataException("The device sent a malformed sensor stream frame."));
            return;
        }

        if (data[1] != expectedStream)
        {
            // Frames from an earlier stream can arrive after it is replaced.
            return;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        if (data[0] != 1 || (flags & 1) == 0 || length != data.Length - StreamHeaderBytes)
        {
            Fail(current, new InvalidDataException("The device sent a malformed sensor stream frame."));
            return;
        }

        DeviceState observation;
        try
        {
            observation = DeviceState.FromBytes(data.AsMemory(StreamHeaderBytes));
        }
        catch (InvalidDataException exception)
        {
            // The firmware can emit truncated or incomplete payloads in valid frames; wait for the next observation.
            logger.ObservationDiscarded(exception);
            return;
        }

        TaskCompletionSource<DeviceState>? read;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return;
            }

            read = liveRead;
            liveRead = null;
        }

        if (read is null)
        {
            lock (sync)
            {
                if (IsCurrent(current))
                {
                    UpdateState(observation);
                }
            }
        }
        else
        {
            read.TrySetResult(observation);
        }
    }

    private bool IsCurrent(long current) => active && generation == current;

    private void ConnectionLost(Exception exception)
    {
        long current;
        lock (sync)
        {
            current = generation;
        }

        ConnectionLost(current, exception);
    }

    private void ConnectionLost(long current, Exception exception)
    {
        TaskCompletionSource lost;
        lock (sync)
        {
            if (current != generation)
            {
                return;
            }

            lost = disconnected;
        }

        lost.TrySetResult();
        Fail(current, exception);
    }

    private void Fail(long current, Exception exception)
    {
        CompositeDisposable owned;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return;
            }

            owned = subscriptions;
        }

        if (Abandon(current, exception))
        {
            // Live notification subscriptions make Shiny reconnect; a failed connection must not be revived.
            owned.Dispose();
            lock (sync)
            {
                if (current == generation)
                {
                    SetFaulted(exception);
                }
            }
        }
    }

    private bool Abandon(long current, Exception exception)
    {
        TaskCompletionSource<Response>[] pending;
        TaskCompletionSource<DeviceState>? read;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return false;
            }

            active = false;
            pending = [.. requests.Values];
            requests.Clear();
            read = liveRead;
            liveRead = null;
        }

        foreach (var request in pending)
        {
            request.TrySetException(exception);
        }

        read?.TrySetException(exception);
        return true;
    }

    private readonly record struct Response(uint Id, string Stage, bool Ok, string? Code, string? Message, JsonElement Result, string? StateAfter);
}
