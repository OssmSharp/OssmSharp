using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using OssmSharp.Runtime.Oem.Protocol;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Owns command sequencing, last known state, arming, and the reusable connection lifecycle for a provider.
/// </summary>
/// <remarks>
/// Lifecycle transitions are serialized with device I/O, except that Stop does not wait for an earlier response or live read.
/// Providers must own issued transfers through settlement even when a response wait is abandoned.
/// Only observations update last known state.
/// </remarks>
public abstract class DeviceBase : IOssmDevice
{
    /// <summary>
    /// Protects lifecycle, request, and last known state from concurrent calls and provider callbacks.
    /// </summary>
    private readonly Lock sync = new();

    /// <summary>
    /// Serializes non-Stop operations and provider cleanup.
    /// </summary>
    private readonly SemaphoreSlim io = new(1, 1);

    /// <summary>
    /// Cancels operation waits and cooperative work when disposal starts.
    /// </summary>
    private readonly CancellationTokenSource shutdown = new();

    /// <summary>
    /// Host-owned logger for operation failures and isolated subscriber errors.
    /// </summary>
    private readonly ILogger<DeviceBase> logger;

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
    /// Initializes the execution owner without connecting or arming.
    /// </summary>
    /// <param name="deviceId"></param>
    /// <param name="logger">The host-owned operational logger.</param>
    protected DeviceBase(string deviceId, ILogger<DeviceBase> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(logger);
        this.DeviceId = deviceId;
        this.logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (sync)
            {
                return connected;
            }
        }
    }

    /// <inheritdoc />
    public virtual string? Name => null;


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
                return providerFailure?.SourceException;
            }
        }
    }

    internal FirmwareSettings? Transmitted
    {
        get
        {
            lock (sync)
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
        lock (sync)
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
        lock (sync)
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
            lock (sync)
            {
                if (!disposing)
                {
                    ThrowIfInterrupted(attempt);
                }
            }

            attemptCancellation.Token.ThrowIfCancellationRequested();
            logger.ConnectionSetupStage("waiting for initial motion observation");
            UpdateState(await ReadStateAsync(attemptCancellation.Token).WaitAsync(attemptCancellation.Token).ConfigureAwait(false));
            lock (sync)
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
            lock (sync)
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
        lock (sync)
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
            lock (sync)
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
        lock (sync)
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
        CancellationToken connectionToken;
        lock (sync)
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
        lock (sync)
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
        lock (sync)
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
            lock (sync)
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
        lock (sync)
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

    /// <summary>
    /// Establishes fresh provider connectivity and subscriptions that report observations through <see cref="UpdateState"/>.
    /// </summary>
    /// <returns>Completion of setup, without any motion writes.</returns>
    /// <remarks>
    /// Runtime reads initial state through <see cref="ReadStateAsync"/> and arms only after that succeeds.
    /// On failure or cancellation, Runtime calls <see cref="DisconnectCoreAsync"/> to release partial setup.
    /// Observations, responses, and failures from earlier connections must not reach Runtime.
    /// </remarks>
    protected abstract Task ConnectCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the firmware to run patterns before a request writes its controls.
    /// </summary>
    /// <param name="cancellationToken" />
    /// <returns>Whether the firmware now runs patterns; false when it is waiting, for example for homing or its speed knob.</returns>
    /// <remarks>
    /// Called once per request, so it must succeed without effect when patterns already run.
    /// Cancellation follows <see cref="WriteCommandAsync"/>.
    /// The firmware decides whether motion is safe to start.
    /// </remarks>
    protected abstract Task<bool> PreparePatternAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads and decodes live firmware controls without a provider-side state cache.
    /// </summary>
    /// <returns>A decoded observation; malformed responses and provider errors must propagate.</returns>
    protected abstract Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Releases per-connection resources after sent commands finish, including partial setup.
    /// </summary>
    /// <returns>Completion of cleanup.</returns>
    /// <remarks>
    /// Called once per connection attempt that needs cleanup, and again after a failed cleanup.
    /// Tolerate repeated calls and keep the provider instance reusable for another <see cref="ConnectCoreAsync"/>.
    /// </remarks>
    protected abstract Task DisconnectCoreAsync();

    /// <summary>
    /// Records failure of the current connection, disarms, and discards settings not yet sent, without sending anything.
    /// </summary>
    /// <param name="exception">The connection or notification failure to propagate to subsequent operations.</param>
    /// <remarks>
    /// Last known state remains available from cached reads. Reset cannot clear the failure; a successful
    /// reconnection can. Cleanup still waits for sent commands to finish. Calls without an active or pending connection are ignored.
    /// </remarks>
    protected void SetFaulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (sync)
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
    protected void UpdateState(DeviceState observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool changed;
        lock (sync)
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
    /// Sends one primitive assignment and awaits the provider's completion semantics.
    /// </summary>
    /// <param name="command">The already validated firmware control assignment.</param>
    /// <param name="cancellationToken" />
    /// <remarks>
    /// A cancelled setting may abandon its terminal response, but its issued transfer must settle before this hook returns.
    /// Stop uses an uncancellable token and awaits its terminal response without waiting for an earlier response or live read.
    /// Do not interpret patterns or accept complete settings here.
    /// </remarks>
    /// <returns>Completion of the issued transfer and, unless abandoned, its terminal response.</returns>
    private protected abstract Task WriteCommandAsync(FirmwareCommand command, CancellationToken cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, bool requiresConnection = false, CancellationToken connectionToken = default, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource waiting;
        lock (sync)
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
            throw new InvalidOperationException("The device is not ready to run patterns. Wait for homing to finish and turn the speed knob to zero, then submit the settings again.");
        }

        foreach (var command in current.Settings.ToCommands())
        {
            long sequence;
            lock (sync)
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
        lock (sync)
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
        lock (sync)
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

        lock (sync)
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
    /// The caller must hold <see cref="sync"/>. When <paramref name="started"/> is non-null,
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
        lock (sync)
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
        lock (sync)
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
            lock (sync)
            {
                StateChanged = null;
            }

            io.Dispose();
            shutdown.Dispose();
        }
    }
}
