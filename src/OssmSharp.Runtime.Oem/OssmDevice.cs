using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OssmSharp.Runtime.Oem.Protocol;
using Shiny.BluetoothLE;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Owns a device connection, control lease, settings requests, and last known state over bluetooth.
/// </summary>
/// <remarks>
/// Dispose the client to end its connection and release its owned resources; the bluetooth manager remains host-owned.
/// Settings select firmware patterns and controls rather than directly driving motor motion.
/// </remarks>
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
    /// Prevents two clients from owning the same peripheral.
    /// </summary>
    private static readonly ConditionalWeakTable<IPeripheral, OssmDevice> Owners = [];

    /// <summary>
    /// Protects lifecycle, requests, connection resources, and last known state from concurrent calls and bluetooth callbacks.
    /// </summary>
    private readonly Lock sync = new();

    /// <summary>
    /// Serializes public operations other than Stop, together with connection cleanup, so teardown cannot overtake their issued transfers.
    /// </summary>
    private readonly SemaphoreSlim io = new(1, 1);

    /// <summary>
    /// Cancels ExecuteAsync gate waits and token-aware work when disposal starts; it does not cancel issued GATT transfers.
    /// </summary>
    /// <remarks>
    /// Disconnect uses connectionCancellation instead so the client remains reusable.
    /// </remarks>
    private readonly CancellationTokenSource shutdown = new();

    /// <summary>
    /// Host-owned bluetooth manager for access checks and peripheral lookup.
    /// </summary>
    private readonly IBleManager manager;

    /// <summary>
    /// Supplies the clock and timers for response timeouts and renewal before the firmware lease expires.
    /// </summary>
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Records API entries, lifecycle changes, request progress, and failures at their owning boundary.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Bounds terminal-response waits and retries that reuse the same firmware request identifier.
    /// </summary>
    private readonly OssmDeviceOptions options;

    /// <summary>
    /// Serializes issued GATT writes without holding the gate while awaiting terminal responses.
    /// </summary>
    /// <remarks>
    /// All GATT request writes, including setup and cleanup requests, share this gate.
    /// Not disposed: Stop and lease maintenance bypass io, so acquiring io alone does not join their work.
    /// </remarks>
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>
    /// Correlates outstanding request identifiers with their terminal-response completions.
    /// </summary>
    private readonly Dictionary<uint, TaskCompletionSource<RadResponse>> requests = [];

    /// <summary>
    /// Shared Stop result joined by concurrent Stop callers, disconnect, and disposal while still in progress.
    /// </summary>
    private Task? stop;

    /// <summary>
    /// Signals when an accepted Stop has transferred so queued non-Stop traffic yields until zero speed is sent.
    /// </summary>
    private TaskCompletionSource? stopTransfer;

    /// <summary>
    /// Latest accepted settings request, targeted by supersession and Stop without cancelling older issued transfers.
    /// </summary>
    private SetOperationContext? request;

    /// <summary>
    /// Cancels the one-shot retract and excludes competing settings and rearming until Stop settles.
    /// </summary>
    [SuppressMessage("Usage", "CA2213", Justification = "ResetPositionAsync owns and disposes this source after joining Stop, including when device disposal interrupts it.")]
    private CancellationTokenSource? resetPosition;

    /// <summary>
    /// Last observed controls, retained across disconnection and never inferred from writes.
    /// </summary>
    private DeviceState? state;

    /// <summary>
    /// Connection failure retained with its stack trace until a successful reconnection.
    /// </summary>
    private ExceptionDispatchInfo? connectionFailure;

    /// <summary>
    /// Shared cleanup result returned by every disposal call, including when cleanup fails.
    /// </summary>
    private Task? disposal;

    /// <summary>
    /// Identifies the connection for stale-callback checks and cancels setup and live reads during teardown.
    /// </summary>
    /// <remarks>
    /// Cleanup disposes the source after joining connectionStopping. Protocol cleanup uses its identity, not its
    /// cancelled token, so Stop and lease release can still use the transport while public operations are rejected.
    /// </remarks>
    private CancellationTokenSource? connectionCancellation;

    /// <summary>
    /// Tracks connection cancellation so cleanup waits for callbacks before disposing the source.
    /// </summary>
    private Task? connectionStopping;

    /// <summary>
    /// Invalidates earlier connection attempts and queued Reset calls when connectivity or arming is interrupted.
    /// </summary>
    /// <remarks>
    /// Lifecycle version, incremented by each connection attempt and interruption.
    /// </remarks>
    private long connection;

    /// <summary>
    /// Governs public operation admission through setup, connectivity, teardown, and permanent disposal.
    /// Unlike active, this can reject new operations while the transport still serves Stop and lease release.
    /// </summary>
    private ConnectionPhase phase;

    /// <summary>
    /// Gates settings independently of connectivity after disarming; successful connection or Reset restores it.
    /// </summary>
    private bool armed;

    /// <summary>
    /// Owns response, motion-stream, and characteristic-subscription observers for the current connection.
    /// </summary>
    private CompositeDisposable subscriptions = [];

    /// <summary>
    /// Keeps connection-status and failure observers alive after data subscriptions close, until native disconnection is acknowledged.
    /// </summary>
    private CompositeDisposable lifecycle = [];

    /// <summary>
    /// Signals native disconnection for the current connection.
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
    /// <remarks>
    /// Separate from connectionCancellation so lease maintenance can continue while teardown waits for Stop.
    /// DisconnectCoreAsync cancels and joins maintenance before releasing the lease and disposing this source.
    /// </remarks>
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
    /// Allows protocol traffic and correlated callbacks until Abandon, even after phase rejects public operations.
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

    private enum ConnectionPhase
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting,
        Disposing,
    }

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
            lock (sync)
            {
                return phase == ConnectionPhase.Connected;
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
            lock (sync)
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
            lock (sync)
            {
                return connectionFailure?.SourceException;
            }
        }
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("Connect");
        return ExecuteAsync(
        async token =>
    {
        long attempt;
        lock (sync)
        {
            if (phase == ConnectionPhase.Connected)
            {
                return true;
            }

            attempt = ++connection;
        }

        logger.OperationStarted("connect");
        await CleanupAsync().ConfigureAwait(false);
        CancellationTokenSource session;
        lock (sync)
        {
            ThrowIfInterrupted(attempt);
            phase = ConnectionPhase.Connecting;
            connectionCancellation = session = new();
            connectionStopping = null;
        }

        // token combines caller cancellation with shutdown. The session additionally interrupts this setup
        // on Stop or disconnect; issued GATT reads and writes still settle before partial cleanup.
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        logger.ConnectionStateChanged("connecting");
        try
        {
            await ConnectCoreAsync(attemptCancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (phase != ConnectionPhase.Disposing)
                {
                    ThrowIfInterrupted(attempt, session);
                }
            }

            attemptCancellation.Token.ThrowIfCancellationRequested();
            logger.ConnectionSetupStage("waiting for initial motion observation");
            await ReadStateAsync(attemptCancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                ThrowIfInterrupted(attempt, session);
                if (phase != ConnectionPhase.Connecting)
                {
                    // SetFaulted ended this attempt during setup.
                    connectionFailure!.Throw();
                }

                phase = ConnectionPhase.Connected;
                connectionFailure = null;
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
            lock (sync)
            {
                if (attempt == connection)
                {
                    phase = connectionCancellation is null ? ConnectionPhase.Disconnected : ConnectionPhase.Disconnecting;
                }

                reported = ReferenceEquals(connectionError, connectionFailure?.SourceException);
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
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("Disconnect");
        bool changed;
        CancellationTokenSource? disconnection;
        Task? pendingStop;
        TaskCompletionSource? started;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(phase == ConnectionPhase.Disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            pendingStop = PrepareStop(out started);
            changed = EndConnectionLocked();
            disconnection = connectionCancellation;
        }

        if (changed)
        {
            NotifyStateChanged();
        }

        if (started is not null)
        {
            _ = CompleteStopAsync(started);
        }

        // The caller token was checked before accepting disconnection. It cannot abandon the Stop or cleanup
        // this call now owns; ExecuteAsync still links shutdown so disposal can take over serialized cleanup.
        await ExecuteAsync(
            async _ =>
        {
            lock (sync)
            {
                if (!ReferenceEquals(disconnection, connectionCancellation))
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
    /// <remarks>
    /// Accepted requests complete asynchronously so cancellation cannot resume callers inside a state transition.
    /// Request results and command counts do not confirm that the device applied the selected settings.
    /// </remarks>
    public async Task<SetResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("Set");
        ArgumentNullException.ThrowIfNull(settings);
        // This request owns the linked source. Caller cancellation and owner-driven supersession share its token,
        // but the context preserves whether the result is Cancelled or Superseded.
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetOperationContext current;
        lock (sync)
        {
            EnsureConnected();
            if (stop is { IsCompleted: false })
            {
                throw new InvalidOperationException("Settings request was interrupted by Stop; submit again after it completes.");
            }

            if (resetPosition is not null)
            {
                throw new InvalidOperationException("Reset Position is running; submit settings again after it completes.");
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
            // ExecuteAsync links pending cancellation with shutdown for the io wait. ApplySettingsAsync uses the
            // pending token for each step; teardown cancels it through CancelPending, not through an issued transfer.
            // Each request configures speedKnobAsLimit=false and checks strokeEngine readiness before its controls.
            var result = await ExecuteAsync(
                _ => ApplySettingsAsync(current),
                requiresConnection: true,
                cancellationToken: current.PendingCancellation.Token).ConfigureAwait(false);
            logger.SettingsFinished(result.Status, result.CommandsWritten);
            return result;
        }
        catch (OperationCanceledException) when (current.PendingCancellation.IsCancellationRequested || shutdown.IsCancellationRequested)
        {
            lock (sync)
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
            // CancelPending and Supersede can cancel under sync. Force an asynchronous continuation even when
            // no I/O was awaited, so this method cannot complete and resume caller code inside that transition.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            lock (sync)
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
        logger.ApiCalled("GetState");
        CancellationToken connectionToken;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(phase == ConnectionPhase.Disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bypassCache)
            {
                return Task.FromResult(state);
            }

            EnsureConnected();
            // Only a fresh read needs the session token. Cached state remains available after disconnection.
            connectionToken = connectionCancellation!.Token;
        }

        return ExecuteAsync<DeviceState?>(
            async token =>
        {
            try
            {
                return await ReadStateAsync(token).ConfigureAwait(false);
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
    /// <remarks>
    /// The Stop command uses <see cref="CancellationToken.None"/> for its transfer and terminal-response wait.
    /// New settings and Reset calls are rejected while a Stop is in progress.
    /// </remarks>
    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("Stop");
        TaskCompletionSource? started = null;
        Task result;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(phase == ConnectionPhase.Disposing, this);
            if (stop is { IsCompleted: false })
            {
                // The first caller owns Stop. A joiner cannot cancel that shared safety operation.
                return stop;
            }

            if (phase != ConnectionPhase.Connecting)
            {
                EnsureConnected();
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Invalidate queued Reset calls and discard pending settings before publishing a Stop result.
            // This does not itself change armed; failed Stop writes disarm through CompleteStopAsync.
            InterruptLocked();
            if (phase == ConnectionPhase.Connecting)
            {
                // No usable connection exists yet. ConnectAsync owns setup settlement and partial cleanup;
                // retain CancelAsync's callback completion for cleanup instead of running callbacks inline here.
                phase = ConnectionPhase.Disconnecting;
                connectionStopping ??= connectionCancellation?.CancelAsync();
                result = Task.CompletedTask;
            }
            else
            {
                // Publish under sync so Set/Reset cannot enter and concurrent Stop calls join this result.
                started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                result = stop = started.Task;
            }
        }

        if (started is not null)
        {
            // Bypass io to avoid waiting for a settings response or live read. The writes gate still orders
            // transfers, and CancellationToken.None keeps Stop owned until its response or transport failure.
            _ = CompleteStopAsync(started);
        }

        return result;
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("Reset");
        long version;
        lock (sync)
        {
            EnsureConnected();
            if (stop is { IsCompleted: false } || resetPosition is not null)
            {
                throw new InvalidOperationException("Reset was interrupted by disarming or Stop; explicitly reset again after it completes.");
            }

            version = connection;
        }

        // ExecuteAsync checks caller cancellation and shutdown before invoking this step. The step ignores its
        // token: it only restores permission under sync, after checking that no interruption changed the version.
        return ExecuteAsync(
            _ =>
        {
            bool changed;
            lock (sync)
            {
                if (stop is { IsCompleted: false } || resetPosition is not null || version != connection)
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

    /// <inheritdoc />
    public async Task ResetPositionAsync(CancellationToken cancellationToken = default)
    {
        logger.ApiCalled("ResetPosition");
        CancellationTokenSource pendingCancellation;
        CancellationTokenSource session;
        lock (sync)
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();
            if (stop is { IsCompleted: false } || resetPosition is not null || !armed)
            {
                throw new InvalidOperationException("Reset Position requires armed requests and no Stop or Reset Position in progress.");
            }

            InterruptLocked();
            resetPosition = pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop = null;
            session = connectionCancellation!;
        }

        Exception? failure = null;
        try
        {
            await ExecuteAsync(
                async token =>
                {
                    logger.OperationStarted("reset position");
                    await WriteCommandAsync(new(FirmwareSetting.Speed, 0), token).ConfigureAwait(false);
                    await SendRequestAsync(session, "setting.write", "setting.speedKnobAsLimit", new JsonObject { ["value"] = false }, leased: true, token).ConfigureAwait(false);
                    var response = await SendRequestAsync(session, "target.set", "target.streaming", null, leased: true, token).ConfigureAwait(false);
                    if (response.StateAfter != "streaming.idle")
                    {
                        throw new InvalidOperationException("The device is not ready to reset position. Wait for homing and preflight to finish.");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, token).ConfigureAwait(false);
                    await WriteCommandAsync(new(FirmwareSetting.Stroke, 100), token).ConfigureAwait(false);
                    await WriteCommandAsync(new(FirmwareSetting.Depth, 100), token).ConfigureAwait(false);
                    await WriteCommandAsync(new(FirmwareSetting.Sensation, 100), token).ConfigureAwait(false);
                    await WriteCommandAsync(new(FirmwareSetting.Speed, 40), token).ConfigureAwait(false);
                    await SendRequestAsync(session, "target.set", "motion.position", new JsonObject { ["value"] = 100, ["durationMs"] = 5000 }, leased: true, token).ConfigureAwait(false);
                    while (true)
                    {
                        var position = (await SendRequestAsync(session, "sensor.read", "motion.position", null, leased: true, token).ConfigureAwait(false)).Result;
                        if (position.ValueKind != JsonValueKind.Object
                            || !position.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || !path.ValueEquals("motion.position")
                            || !position.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number
                            || !value.TryGetDouble(out var positionMm) || !double.IsFinite(positionMm))
                        {
                            throw new InvalidDataException("The device did not report a finite motion position in millimetres.");
                        }

                        if (positionMm == 0)
                        {
                            return true;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, token).ConfigureAwait(false);
                    }
                },
                requiresConnection: true,
                connectionToken: session.Token,
                cancellationToken: pendingCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (exception is not OperationCanceledException)
            {
                logger.OperationFailed("reset position", exception);
            }

            throw;
        }
        finally
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            try
            {
                Task? pendingStop;
                TaskCompletionSource? started = null;
                lock (sync)
                {
                    pendingStop = ReferenceEquals(session, connectionCancellation)
                        ? stop ?? PrepareStop(out started)
                        : null;
                }

                if (started is not null)
                {
                    _ = CompleteStopAsync(started);
                }

                if (pendingStop is not null)
                {
                    await pendingStop.ConfigureAwait(false);
                }
            }
            catch (Exception cleanupError) when (failure is not null)
            {
                throw new AggregateException("Reset Position and Stop both failed.", failure, cleanupError);
            }
            finally
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                lock (sync)
                {
                    resetPosition.Dispose();
                    resetPosition = null;
                }
            }
        }

        logger.OperationCompleted("reset position");
    }

    /// <summary>
    /// Disarms, discards commands not yet sent, sends or joins Stop, releases connection resources and subscribers,
    /// and prevents reconnection.
    /// </summary>
    /// <returns>Completion of cleanup; repeated calls observe the same cleanup result.</returns>
    /// <remarks>
    /// Disposal has no caller token. It cancels session work, pending settings, and serialized I/O waits,
    /// then joins shutdown cancellation and connection cleanup. Issued GATT transfers are not cancelled.
    /// Cleanup failures propagate through the shared disposal result.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        logger.ApiCalled("Dispose");
        GC.SuppressFinalize(this);
        TaskCompletionSource? started;
        Task? pendingStop;
        Task disposingTask;
        bool changed;
        lock (sync)
        {
            if (disposal is not null)
            {
                return new(disposal);
            }

            pendingStop = PrepareStop(out started);
            phase = ConnectionPhase.Disposing;
            changed = EndConnectionLocked();
            // EndConnectionLocked cancels session and pending settings; shutdown also wakes operations queued
            // on io. Retain CancelAsync's completion so disposal joins cancellation callbacks before cleanup.
            disposingTask = disposal = CompleteDisposalAsync(pendingStop, shutdown.CancelAsync(), changed);
        }

        if (started is not null)
        {
            _ = CompleteStopAsync(started);
        }

        return new(disposingTask);
    }

    /// <summary>
    /// Acknowledges native disconnection callbacks that Shiny may suppress from its status observable.
    /// </summary>
    /// <param name="peripheral">The peripheral whose native connection state changed.</param>
    internal static void PeripheralStateChanged(IPeripheral peripheral)
    {
        if (peripheral.Status != ConnectionState.Disconnected || !Owners.TryGetValue(peripheral, out var owner))
        {
            return;
        }

        TaskCompletionSource? acknowledgement;
        lock (owner.sync)
        {
            acknowledgement = ReferenceEquals(owner.peripheral, peripheral) ? owner.disconnected : null;
        }

        acknowledgement?.TrySetResult();
    }

    /// <summary>
    /// Records failure of the current connection, disarms, and discards settings not yet sent, without sending anything.
    /// </summary>
    /// <param name="current">The connection whose failure is being recorded.</param>
    /// <param name="exception">The connection or notification failure to propagate to subsequent operations.</param>
    /// <remarks>
    /// Last known state remains available from cached reads. Reset cannot clear the failure; a successful
    /// reconnection can. Cleanup still waits for sent commands to finish. Calls without an active or pending connection are ignored.
    /// </remarks>
    private void SetFaulted(CancellationTokenSource current, Exception exception)
    {
        lock (sync)
        {
            if (!ReferenceEquals(current, connectionCancellation) || phase is not (ConnectionPhase.Connecting or ConnectionPhase.Connected))
            {
                return;
            }

            var wasConnected = phase == ConnectionPhase.Connected;
            connectionFailure = ExceptionDispatchInfo.Capture(exception);
            phase = ConnectionPhase.Disconnecting;
            if (wasConnected)
            {
                DisarmLocked();
            }
            else
            {
                armed = false;
                CancelPending();
            }
        }

        logger.OperationFailed("connection", exception);
        NotifyStateChanged();
    }

    /// <summary>
    /// Establishes fresh connectivity and subscriptions that record complete motion observations.
    /// </summary>
    /// <returns>Completion of setup, without any motion writes.</returns>
    /// <remarks>
    /// The initial state is read through <see cref="ReadStateAsync"/> before arming.
    /// Failure or cancellation releases partial setup through <see cref="DisconnectCoreAsync"/>.
    /// Observations, responses, and failures from earlier connections must not reach this connection.
    /// The setup token links caller cancellation, shutdown, and session interruption. It cancels connection,
    /// subscription-readiness, lease-acquisition, stream-start, and initial-observation waits where used.
    /// Bluetooth access and issued GATT reads and writes are awaited without that token; cleanup is owned by ConnectAsync.
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

        CancellationTokenSource current;
        CompositeDisposable owned;
        CompositeDisposable events;
        var stopping = new CancellationTokenSource();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            current = connectionCancellation!;
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
        var maintenance = MaintainLeaseAsync(current, acquired, stopping.Token);
        lock (sync)
        {
            leaseMaintenance = maintenance;
        }

        await acquired.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        logger.ConnectionSetupStage("starting motion stream");
        await StartMotionStreamAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the firmware to run patterns before a request writes its controls.
    /// </summary>
    /// <returns>Whether the firmware is ready to run patterns.</returns>
    /// <remarks>
    /// Writes speedKnobAsLimit=false and uses that response's state to avoid re-entering an active stroke engine.
    /// Homing and preflight are not ready; other modes require selecting the stroke engine.
    /// Called once per request, including when patterns already run.
    /// The firmware decides whether motion is safe to start.
    /// Request cancellation stops issuance waits and terminal-response waits, not issued transfers.
    /// </remarks>
    private async Task<bool> PreparePatternAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource current;
        lock (sync)
        {
            current = connectionCancellation!;
        }

        var setup = await SendRequestAsync(current, "setting.write", "setting.speedKnobAsLimit", new JsonObject { ["value"] = false }, leased: true, cancellationToken).ConfigureAwait(false);
        if (setup.StateAfter is "strokeEngine.idle" or "strokeEngine.pattern")
        {
            return true;
        }

        if (setup.StateAfter is "strokeEngine.preflight" or "strokeEngine.homing")
        {
            logger.PatternNotReady(setup.StateAfter);
            return false;
        }

        var response = await SendRequestAsync(current, "target.set", "target.strokeEngine", null, leased: true, cancellationToken).ConfigureAwait(false);
        var ready = response.StateAfter is "strokeEngine.idle" or "strokeEngine.pattern";
        if (!ready)
        {
            logger.PatternNotReady(response.StateAfter);
        }

        return ready;
    }

    /// <summary>
    /// Reads and decodes live firmware controls from the motion stream.
    /// </summary>
    /// <returns>A decoded observation; malformed responses and bluetooth errors must propagate.</returns>
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
        CancellationTokenSource current;
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

            current = connectionCancellation!;
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
    /// <param name="cancellationToken" />
    /// <returns>Completion of the issued transfer and, unless abandoned, its terminal response.</returns>
    /// <remarks>
    /// Cancellation stops issuance waits and terminal-response waits, not an issued transfer.
    /// A cancelled setting may abandon its terminal response, but its issued transfer must settle before this method returns.
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
        CancellationTokenSource current;
        lock (sync)
        {
            current = connectionCancellation!;
        }

        return SendRequestAsync(current, "setting.write", path, new JsonObject { ["value"] = command.Value }, leased: true, cancellationToken);
    }

    /// <summary>
    /// Rejects incompatible protocol information before subscribing or acquiring device control.
    /// </summary>
    private static void ValidateProtocolInfo(byte[]? data)
    {
        try
        {
            if (JsonSerializer.Deserialize(data ?? [], RadJsonContext.Default.ProtocolInfo) is { Protocol: "rad-ble", Version: 1 })
            {
                return;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException("The device does not report RAD BLE protocol version 1.");
    }

    /// <summary>
    /// Decodes a required response object and translates absent or malformed results into the operation's protocol error.
    /// </summary>
    private static T ReadResult<T>(JsonElement result, JsonTypeInfo<T> type, string error)
        where T : class
    {
        try
        {
            if (result.ValueKind == JsonValueKind.Object && result.Deserialize(type) is { } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidDataException(error);
    }

    /// <summary>
    /// Requires a positive firmware lease lifetime for renewal scheduling.
    /// </summary>
    private static TimeSpan ReadLeaseLifetime(LeaseResult result) => result.TtlMs != 0
        ? TimeSpan.FromMilliseconds(result.TtlMs)
        : throw new InvalidDataException("The device did not report a positive control lease lifetime.");

    /// <summary>
    /// Validates response size, version, and stage before correlation with an outstanding request.
    /// </summary>
    private static RadResponse ParseResponse(byte[]? data)
    {
        if (data is null || data.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("A RAD response must contain at most 509 bytes.");
        }

        if (JsonSerializer.Deserialize(data, RadJsonContext.Default.RadResponse) is not { V: 1, Stage: "accepted" or "completed" or "failed" } response)
        {
            throw new InvalidDataException("A RAD response requires v=1, an id, a known stage, and ok.");
        }

        return response;
    }

    /// <summary>
    /// Admits non-Stop work through the operation gate and rechecks connection availability after waiting.
    /// </summary>
    /// <remarks>
    /// The linked token combines caller cancellation, disposal shutdown, and an optional connection token.
    /// Live reads supply the connection token; connection setup separately links its session token inside the operation.
    /// Settings supply their pending-request token. Cleanup deliberately ignores caller cancellation once accepted.
    /// Reset uses the token only for gate admission; its enabling step ignores it.
    /// The operation determines which waits use this token; it never cancels an already issued GATT transfer.
    /// </remarks>
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, bool requiresConnection = false, CancellationToken connectionToken = default, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource waiting;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(phase == ConnectionPhase.Disposing, this);
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
                    lock (sync)
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

    /// <summary>
    /// Prepares firmware pattern readiness, then sends controls in order while the accepted request remains eligible.
    /// </summary>
    private async Task<SetResult> ApplySettingsAsync(SetOperationContext current)
    {
        lock (sync)
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
            throw new InvalidOperationException("The device is not ready to run patterns. Wait for homing and preflight to finish, then request the settings again.");
        }

        foreach (var command in current.Settings.ToCommands())
        {
            lock (sync)
            {
                if (IsDiscardedLocked(current))
                {
                    return current.Result;
                }
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
        }

        lock (sync)
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
    /// <remarks>
    /// Pending-request cancellation ends the step without replacing its stored result unless a connection failure
    /// is recorded. A recorded failure is wrapped as a preparation or write error instead of returning cancellation.
    /// </remarks>
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
            lock (sync)
            {
                connectionError = connectionFailure?.SourceException;
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
        catch (IOException exception) when (current.CommandsWritten == 0
            && exception.GetBaseException() is DeviceRequestException { Code: "guard_rejected" } rejection)
        {
            logger.PatternNotReady(rejection.DeviceState);
            throw new InvalidOperationException("The device is not ready to run patterns. Wait for homing and preflight to finish, then request the settings again.", exception);
        }
        catch (Exception exception)
        {
            Disarm();
            logger.OperationFailed("settings write", exception);
            throw failure(exception);
        }
    }

    /// <summary>
    /// Prevents further settings steps after cancellation, disarming, or disposal while preserving a superseded result.
    /// </summary>
    private bool IsDiscardedLocked(SetOperationContext current)
    {
        if (!current.PendingCancellation.IsCancellationRequested && armed && phase != ConnectionPhase.Disposing)
        {
            return false;
        }

        if (current.Status == SetStatus.Completed)
        {
            current.Status = SetStatus.Cancelled;
        }

        return true;
    }

    /// <summary>
    /// Replaces the accepted request under sync, marking its predecessor superseded before cancelling its waits.
    /// </summary>
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

    /// <summary>
    /// Marks the accepted request cancelled under sync and prevents its remaining commands from being issued.
    /// </summary>
    private void CancelPending()
    {
        if (request is not null)
        {
            request.Status = SetStatus.Cancelled;
            request.PendingCancellation.Cancel();
        }
    }

    /// <summary>
    /// Invalidates connection-attempt and Reset versions as well as pending settings, without changing arming itself.
    /// </summary>
    private void InterruptLocked()
    {
        connection++;
        CancelPending();
        resetPosition?.Cancel();
    }

    /// <summary>
    /// Closes public operation admission and cancels session work while retaining the transport for required cleanup.
    /// </summary>
    private bool EndConnectionLocked()
    {
        if (phase != ConnectionPhase.Disposing)
        {
            phase = connectionCancellation is null ? ConnectionPhase.Disconnected : ConnectionPhase.Disconnecting;
        }

        var changed = DisarmLocked();
        connectionStopping ??= connectionCancellation?.CancelAsync();
        return changed;
    }

    /// <summary>
    /// Requires explicit rearming and invalidates queued work; returns whether arming changed for state notification.
    /// </summary>
    private bool DisarmLocked()
    {
        var changed = armed;
        armed = false;
        InterruptLocked();
        return changed;
    }

    /// <summary>
    /// Disarms under sync and notifies subscribers outside the state transition when arming changed.
    /// </summary>
    private void Disarm()
    {
        bool changed;
        lock (sync)
        {
            changed = DisarmLocked();
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Rejects disposed or unavailable connections, preserving the recorded connection failure when present.
    /// </summary>
    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(phase == ConnectionPhase.Disposing, this);
        if (phase != ConnectionPhase.Connected)
        {
            connectionFailure?.Throw();
            throw new InvalidOperationException("Connect before using the device.");
        }
    }

    /// <summary>
    /// Prevents an interrupted connection attempt from completing setup or arming a later lifecycle.
    /// </summary>
    /// <param name="attempt">The connection attempt being completed.</param>
    /// <param name="session">The attempt's session, once created; its cancellation reports the interruption as cancellation.</param>
    private void ThrowIfInterrupted(long attempt, CancellationTokenSource? session = null)
    {
        ObjectDisposedException.ThrowIf(phase == ConnectionPhase.Disposing, this);
        if (attempt != connection)
        {
            // Stop and Disconnect cancel the session with CancelAsync, which marks it cancelled immediately but
            // runs linked-token callbacks later. Setup can finish in between, so check the session itself to report
            // the same cancellation the linked token would have. IsCancellationRequested stays readable after disposal.
            if (session?.IsCancellationRequested == true)
            {
                throw new OperationCanceledException("The connection attempt was interrupted.");
            }

            throw new InvalidOperationException("The connection attempt was interrupted by a disconnect.");
        }
    }

    /// <summary>
    /// Joins session cancellation and Stop before transport teardown, preserving their failures after cleanup.
    /// </summary>
    private async Task CleanupAsync(Task? joinedStop = null)
    {
        Task? pendingStop;
        Task? stopping;
        lock (sync)
        {
            if (connectionCancellation is null)
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

        lock (sync)
        {
            connectionCancellation.Dispose();
            connectionCancellation = null;
            connectionStopping = null;
            stop = null;
            if (phase != ConnectionPhase.Disposing)
            {
                phase = ConnectionPhase.Disconnected;
            }
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
    /// The caller must hold <see cref="sync"/>. When <paramref name="started"/> is non-null,
    /// the caller must invoke <see cref="CompleteStopAsync"/> after releasing the lock to send the Stop command.
    /// </remarks>
    private Task? PrepareStop(out TaskCompletionSource? started)
    {
        started = null;
        if (phase == ConnectionPhase.Connected && stop is not { IsCompleted: false })
        {
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stop = started.Task;
        }

        return stop is { IsCompleted: false } ? stop : null;
    }

    /// <summary>
    /// Owns zero velocity and, during Reset Position, streaming exit; completes shared Stop and disarms on failure.
    /// </summary>
    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            CancellationTokenSource current;
            bool leaveStreaming;
            lock (sync)
            {
                current = connectionCancellation!;
                leaveStreaming = resetPosition is not null;
            }

            Exception? speedFailure = null;
            try
            {
                await WriteCommandAsync(new(FirmwareSetting.Speed, 0), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                speedFailure = exception;
                throw;
            }
            finally
            {
                if (leaveStreaming)
                {
                    try
                    {
                        await SendRequestAsync(current, "target.set", "target.menu", null, leased: true, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception menuFailure) when (speedFailure is not null)
                    {
                        throw new AggregateException("Zero velocity and streaming exit both failed.", speedFailure, menuFailure);
                    }
                }
            }

            logger.StopCompleted();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            Disarm();
            logger.OperationFailed("Stop write", exception);
            completion.TrySetException(exception);
        }
    }

    /// <summary>
    /// Delivers state notifications without allowing one subscriber's failure to interrupt device work or other subscribers.
    /// </summary>
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

    /// <summary>
    /// Leaves the disposal transition before notifying, then joins shutdown and serialized connection cleanup.
    /// </summary>
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
            lock (sync)
            {
                StateChanged = null;
            }

            io.Dispose();
            shutdown.Dispose();
        }
    }

    /// <summary>
    /// Owns both subscription readiness and data observers, routing later subscription failures to connection teardown.
    /// </summary>
    /// <remarks>
    /// The setup token cancels only the readiness wait. The connection owns the observers until cleanup or failure.
    /// </remarks>
    private async Task SubscribeAsync(
        CancellationTokenSource current,
        IPeripheral selected,
        CompositeDisposable owned,
        string characteristic,
        string name,
        bool indicate,
        Action<CancellationTokenSource, byte[]?> handler,
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

    /// <summary>
    /// Correlates a request through terminal response, retrying timeouts with the same identifier and encoded controls.
    /// </summary>
    /// <remarks>
    /// Cancellation stops gate waits, future retry issuance, and response waits, but each issued transfer must settle.
    /// The connection source identifies the transport; its cancelled token does not prohibit cleanup requests.
    /// Transport abandonment fails correlated responses independently of cancellation.
    /// </remarks>
    private async Task<RadResponse> SendRequestAsync(
        CancellationTokenSource current,
        string operation,
        string? path,
        JsonObject? arguments,
        bool leased,
        CancellationToken cancellationToken,
        Action? onFirstIssuance = null)
    {
        var response = new TaskCompletionSource<RadResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var queued = timeProvider.GetTimestamp();
                var issued = queued;
                Action onIssuing = () =>
                {
                    issued = timeProvider.GetTimestamp();
                    if (attempt == 1)
                    {
                        onFirstIssuance?.Invoke();
                    }

                    logger.RequestTransferStarted(id, operation, timeProvider.GetElapsedTime(queued, issued).TotalMilliseconds);
                };
                await WriteRequestAsync(current, selected, payload, priority, onIssuing, cancellationToken).ConfigureAwait(false);
                logger.RequestTransferred(id, operation, attempt, timeProvider.GetElapsedTime(issued).TotalMilliseconds);
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

    /// <summary>
    /// Serializes transfer issuance and retains the write gate until the native GATT observable settles.
    /// </summary>
    /// <remarks>
    /// Cancellation applies before issuance only. Passing it to ToTask would abandon ownership of an in-flight write.
    /// Terminal-response waiting occurs after this gate is released, allowing Stop to proceed independently.
    /// </remarks>
    [SuppressMessage("Usage", "CA1849", Justification = "Shiny's Async wrapper adds a timeout that abandons issued GATT I/O; the raw observable is awaited to settlement.")]
    private async Task WriteRequestAsync(
        CancellationTokenSource current,
        IPeripheral selected,
        byte[] payload,
        TaskCompletionSource? priority,
        Action onIssuing,
        CancellationToken cancellationToken)
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
                    onIssuing();
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

    /// <summary>
    /// Completes only correlated terminal responses from the current transport; accepted stages keep the request pending.
    /// </summary>
    private void HandleResponse(CancellationTokenSource current, byte[]? data)
    {
        RadResponse response;
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

        TaskCompletionSource<RadResponse>? pending;
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
            var rejection = new DeviceRequestException(response.Code ?? "unknown", response.StateAfter, response.Message);
            logger.RequestRejected(response.Id, rejection.Code, rejection.DeviceState);
            pending.TrySetException(new IOException(rejection.Message, rejection));
        }
    }

    /// <summary>
    /// Acquires control and renews it before firmware expiry, failing the transport if renewal cannot retain ownership.
    /// </summary>
    /// <remarks>
    /// leaseStopping cancels renewal delays, issuance waits, and response waits independently of session cancellation.
    /// Teardown joins this routine before release; an issued renewal transfer still settles. The outer renewal wait
    /// uses no cancellation token so lease expiry can fail the transport and then join the underlying renewal.
    /// </remarks>
    private async Task MaintainLeaseAsync(CancellationTokenSource current, TaskCompletionSource acquired, CancellationToken stopping)
    {
        TimeSpan lifetime;
        DateTimeOffset requested;
        try
        {
            requested = timeProvider.GetUtcNow();
            const string noLease = "The device did not return a control lease.";
            var response = await SendRequestAsync(
                current,
                "control.acquire",
                null,
                null,
                leased: false,
                stopping,
                onFirstIssuance: () => requested = timeProvider.GetUtcNow()).ConfigureAwait(false);
            var granted = ReadResult(response.Result, RadJsonContext.Default.LeaseResult, noLease);
            var token = granted.Lease != 0 ? granted.Lease : throw new InvalidDataException(noLease);
            lifetime = ReadLeaseLifetime(granted);
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
            var queued = requested;
            // Lease lifetime starts at first issuance, not while waiting behind an owned native transfer.
            // Retries keep that origin because the firmware may return a cached response.
            var renewal = SendRequestAsync(
                current,
                "control.renew",
                null,
                null,
                leased: true,
                stopping,
                onFirstIssuance: () => requested = timeProvider.GetUtcNow());
            logger.LeaseRenewalStarted((expires - timeProvider.GetUtcNow()).TotalMilliseconds);
            try
            {
                var remaining = expires - timeProvider.GetUtcNow();
                // Issued renewal is bounded by lease expiry, not by disconnect.
                var renewed = (await renewal.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider, CancellationToken.None).ConfigureAwait(false)).Result;
                lifetime = ReadLeaseLifetime(ReadResult(renewed, RadJsonContext.Default.LeaseResult, "The device did not report a positive control lease lifetime."));
                logger.LeaseRenewed((timeProvider.GetUtcNow() - queued).TotalMilliseconds, lifetime.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (exception is TimeoutException)
                {
                    logger.LeaseRenewalExpired((expires - queued).TotalMilliseconds);
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

    /// <summary>
    /// Starts the motion stream and retains its validated identifier to reject unrelated sensor frames.
    /// </summary>
    /// <remarks>
    /// The setup token cancels issuance and response waits, not an issued transfer. Connection cleanup stops
    /// the stream or closes the transport when setup is interrupted.
    /// </remarks>
    private async Task StartMotionStreamAsync(CancellationTokenSource current, CancellationToken cancellationToken)
    {
        const string notStarted = "The device did not start a motion stream.";
        var started = ReadResult((await SendRequestAsync(current, "stream.start", "motion.speed", null, leased: true, cancellationToken).ConfigureAwait(false)).Result, RadJsonContext.Default.StreamResult, notStarted);
        if (started is not { StreamId: > 0 and <= byte.MaxValue, Surface: "motion" })
        {
            throw new InvalidDataException(notStarted);
        }

        var id = started.StreamId;

        lock (sync)
        {
            if (!IsCurrent(current))
            {
                throw new IOException("The bluetooth connection is no longer active.");
            }

            streamId = (byte)id;
        }
    }

    /// <summary>
    /// Validates current-stream frames and delivers the next valid observation to a live read or the cached state.
    /// </summary>
    private void HandleMotionObservation(CancellationTokenSource current, byte[]? data)
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
        bool changed;
        lock (sync)
        {
            if (!IsCurrent(current))
            {
                return;
            }

            read = liveRead;
            liveRead = null;
            // Record at arrival, not after a read's continuation, which can run after a newer frame.
            changed = state != observation;
            state = observation;
        }

        read?.TrySetResult(observation);
        if (changed)
        {
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Requires transport availability and session identity, not public-operation admission or an uncancelled session token.
    /// </summary>
    private bool IsCurrent(CancellationTokenSource current) => active && ReferenceEquals(connectionCancellation, current);

    /// <summary>
    /// Acknowledges native disconnection for the matching session and fails any transport work that remains active.
    /// </summary>
    private void ConnectionLost(CancellationTokenSource current, Exception exception)
    {
        TaskCompletionSource lost;
        lock (sync)
        {
            if (!ReferenceEquals(current, connectionCancellation))
            {
                return;
            }

            lost = disconnected;
        }

        lost.TrySetResult();
        Fail(current, exception);
    }

    /// <summary>
    /// Abandons current transport work and removes data subscriptions that would otherwise trigger automatic reconnection.
    /// </summary>
    private void Fail(CancellationTokenSource current, Exception exception)
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
            SetFaulted(current, exception);
        }
    }

    /// <summary>
    /// Ends protocol traffic and fails response and live-read waiters without cancelling already issued native transfers.
    /// </summary>
    private bool Abandon(CancellationTokenSource current, Exception exception)
    {
        TaskCompletionSource<RadResponse>[] pending;
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
}
