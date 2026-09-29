using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Owns command sequencing, last known state, arming, and the reusable connection lifecycle for a provider.
/// </summary>
/// <remarks>
/// Lifecycle transitions are serialized with device I/O, except that Stop is sent without waiting for a command in progress.
/// Providers must let commands they have sent finish before completing their hooks,
/// including on cancellation or error. Writes update expectations, never last known state or a claim about physical motion.
/// </remarks>
public abstract class DeviceBase : IOssmDevice
{
    private readonly Lock sync = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ILogger<DeviceBase> logger;
    private readonly double[] expected = new double[5];
    private readonly Queue<double>[] unobserved = [new(), new(), new(), new(), new()];
    private TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task stopWrites = Task.CompletedTask;
    private SettingsSubmission? submission;
    private DeviceState? state;
    private ExceptionDispatchInfo? providerFailure;
    private Task? disposal;

    /// <summary>
    /// Counts queued or running operations and Stop writes; <see cref="drained"/> completes when it returns to zero.
    /// </summary>
    private int operationCount;

    /// <summary>
    /// Counts unfinished Stop calls, including those deferred until setup completes; Reset fails while any remain.
    /// </summary>
    private int pendingStops;

    /// <summary>
    /// Increments on every disarm so Connect and Reset can detect a disarm after they captured it.
    /// </summary>
    private long disarmVersion;

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
    /// Indicates that the next observation sets fresh expectations instead of being checked for external changes.
    /// </summary>
    private bool baselinePending;

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
    /// <param name="logger">The host-owned operational logger.</param>
    protected DeviceBase(ILogger<DeviceBase> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
        drained.SetResult();
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

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

        await CleanupAsync().ConfigureAwait(false);
        long version;
        lock (sync)
        {
            ThrowIfInterrupted(attempt);
            connecting = true;
            baselinePending = true;
            cleanupPending = true;
            version = disarmVersion;
        }

        try
        {
            await ConnectCoreAsync(token).ConfigureAwait(false);
            UpdateState(await ReadStateAsync(token).ConfigureAwait(false));
            bool armedByConnection;
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
                armedByConnection = version == disarmVersion;
                armed = armedByConnection;
            }

            if (armedByConnection)
            {
                NotifyStateChanged();
            }

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

            if (connectionError is not OperationCanceledException && !reported)
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
    }, cancellationToken);

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        long disconnection;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            disconnection = ++connection;
            connecting = false;
            connected = false;
            changed = DisarmLocked();
        }

        if (changed)
        {
            NotifyStateChanged();
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

            await CleanupAsync().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SetResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SettingsSubmission current;
        lock (sync)
        {
            EnsureConnected();
            if (!armed)
            {
                throw new InvalidOperationException("Commands are disarmed; explicitly reset before submitting settings.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new(SetStatus.Cancelled, 0);
            }

            current = Supersede(settings, pendingCancellation);
        }

        try
        {
            return await ExecuteAsync(
                async _ =>
            {
                foreach (var command in current.Settings.ToCommands())
                {
                    lock (sync)
                    {
                        if (current.PendingCancellation.IsCancellationRequested || !armed || disposing)
                        {
                            if (current.Status == SetStatus.Completed)
                            {
                                current.Status = SetStatus.Cancelled;
                            }

                            return current.Result;
                        }

                        Expect(command);
                    }

                    try
                    {
                        await WriteCommandAsync(command, CancellationToken.None).ConfigureAwait(false);
                        current.CommandsWritten++;
                    }
                    catch (Exception exception)
                    {
                        Disarm();
                        logger.OperationFailed("settings write", exception);
                        throw new DeviceWriteException(command, current.CommandsWritten, exception);
                    }
                }

                lock (sync)
                {
                    if (current.PendingCancellation.IsCancellationRequested && current.Status == SetStatus.Completed)
                    {
                        current.Status = SetStatus.Cancelled;
                    }

                    return current.Result;
                }
            }, current.PendingCancellation.Token, requiresConnection: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (current.PendingCancellation.IsCancellationRequested || disposing)
        {
            lock (sync)
            {
                if (current.Status == SetStatus.Completed)
                {
                    current.Status = SetStatus.Cancelled;
                }

                return current.Result;
            }
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(submission, current))
                {
                    submission = null;
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<DeviceState?> GetStateAsync(bool bypassCache = false, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!bypassCache)
            {
                return Task.FromResult(state);
            }

            EnsureConnected();
        }

        return ExecuteAsync<DeviceState?>(
            async token =>
        {
            try
            {
                var observation = await ReadStateAsync(token).ConfigureAwait(false);
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
        }, cancellationToken, requiresConnection: true);
    }

    /// <inheritdoc />
    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        TaskCompletionSource? sent = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            if (!connecting)
            {
                EnsureConnected();
                cancellationToken.ThrowIfCancellationRequested();

                // Sent now, alongside any command already in progress; cleanup and disposal wait for it.
                sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
                stopWrites = Task.WhenAll(stopWrites, sent.Task);
                if (operationCount++ == 0)
                {
                    drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            changed = DisarmLocked();
            pendingStops++;
        }

        if (changed)
        {
            NotifyStateChanged();
        }

        try
        {
            if (sent is null)
            {
                // Stop during setup prevents arming and is sent once the connection is established.
                await ExecuteAsync(
                    async _ =>
                {
                    await WriteStopAsync().ConfigureAwait(false);
                    return true;
                }, cancellationToken, requiresConnection: true).ConfigureAwait(false);
            }
            else
            {
                await WriteStopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (sync)
            {
                pendingStops--;
                if (sent is not null && --operationCount == 0)
                {
                    drained.TrySetResult();
                }
            }

            sent?.TrySetResult();
        }
    }

    /// <inheritdoc />
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        long version;
        lock (sync)
        {
            EnsureConnected();
            version = disarmVersion;
        }

        return ExecuteAsync(
            _ =>
        {
            bool changed;
            lock (sync)
            {
                if (pendingStops != 0 || version != disarmVersion)
                {
                    throw new InvalidOperationException("Reset was interrupted by disarming or Stop; explicitly reset again after it completes.");
                }

                changed = !armed;
                armed = true;
            }

            if (changed)
            {
                NotifyStateChanged();
            }

            return Task.FromResult(true);
        }, cancellationToken, requiresConnection: true);
    }

    /// <summary>
    /// Disarms, discards commands not yet sent, waits for sent commands to finish, runs <see cref="OnDisposingAsync"/>,
    /// releases connection resources and subscribers, and prevents reconnection.
    /// </summary>
    /// <returns>Completion of cleanup; repeated calls observe the same cleanup result.</returns>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (sync)
        {
            if (disposal is null)
            {
                disposing = true;
                connection++;
                connecting = false;
                connected = false;
                var changed = DisarmLocked();
                disposal = CompleteDisposalAsync(drained.Task, shutdown.CancelAsync(), changed);
            }

            return new(disposal);
        }
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
    /// Sends one primitive assignment and awaits the provider's completion semantics.
    /// </summary>
    /// <param name="command">The already validated firmware control assignment.</param>
    /// <param name="cancellationToken" />
    /// <remarks>
    /// Complete only after a sent command finishes, even on cancellation. Do not interpret patterns or accept complete settings here.
    /// A Stop (speed zero) can be sent while another command or live read is still in progress.
    /// </remarks>
    /// <returns>Completion of the issued write, not confirmation of physical motion.</returns>
    protected abstract Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken);

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
    /// Performs provider disposal work while any connection is still open, such as a final Stop.
    /// </summary>
    /// <returns>Completion of the provider's disposal work.</returns>
    /// <remarks>
    /// Called once, after sent commands finish and before <see cref="DisconnectCoreAsync"/>. Runtime is already disarmed and
    /// may have no connection. A failure propagates from <see cref="DisposeAsync"/> after connection resources are released.
    /// </remarks>
    protected virtual ValueTask OnDisposingAsync() => ValueTask.CompletedTask;

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
    /// Records an incoming observation and disarms if changed controls are unexplained by issued assignments.
    /// </summary>
    /// <param name="observation">Decoded controls from a provider read or notification.</param>
    /// <remarks>
    /// Unchanged last-observed controls and unobserved issued assignments are accepted while notifications catch up.
    /// Observing an issued value accounts for earlier assignments to that control; subsequent reversions are external changes.
    /// The first observation of each connection establishes fresh expectations. Observations never arm or cause motion writes,
    /// and are ignored without an active or pending connection. Subscriber failures are logged and cannot interrupt device execution.
    /// </remarks>
    protected void UpdateState(DeviceState observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool changed;
        bool external = false;
        lock (sync)
        {
            if (disposing || !(connecting || connected))
            {
                return;
            }

            if (baselinePending)
            {
                baselinePending = false;
                SetExpectation(observation);
            }
            else
            {
                external = DetectExternalChange(observation);
            }

            changed = state != observation || external;
            state = observation;
        }

        if (external)
        {
            logger.ExternalChange();
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool requiresConnection = false)
    {
        CancellationTokenSource waiting;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            if (operationCount++ == 0)
            {
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
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
            lock (sync)
            {
                if (--operationCount == 0)
                {
                    drained.TrySetResult();
                }
            }
        }
    }

    private SettingsSubmission Supersede(MotionSettings settings, CancellationTokenSource pendingCancellation)
    {
        if (submission is not null)
        {
            submission.Status = SetStatus.Superseded;
            submission.PendingCancellation.Cancel();
        }

        submission = new(settings, pendingCancellation);
        return submission;
    }

    private void CancelPending()
    {
        if (submission is not null)
        {
            submission.Status = SetStatus.Cancelled;
            submission.PendingCancellation.Cancel();
        }
    }

    private bool DetectExternalChange(DeviceState observation)
    {
        if (state is null)
        {
            return false;
        }

        var previous = state.Settings.ToCommands();
        var incoming = observation.Settings.ToCommands();
        var unexplained = false;
        for (var index = 0; index < incoming.Length; index++)
        {
            var command = incoming[index];
            if (command.Value == previous[index].Value)
            {
                continue;
            }

            var pending = unobserved[(int)command.Control];
            if (pending.Contains(command.Value))
            {
                while (pending.Dequeue() != command.Value)
                {
                }
            }
            else
            {
                unexplained = true;
                pending.Clear();
                expected[(int)command.Control] = command.Value;
            }
        }

        if (!unexplained || !(armed || connecting))
        {
            return false;
        }

        // During setup this prevents the attempt from arming.
        DisarmLocked();
        return true;
    }

    private void Expect(DeviceCommand command)
    {
        var index = (int)command.Control;
        if (expected[index] != command.Value)
        {
            unobserved[index].Enqueue(command.Value);
            expected[index] = command.Value;
        }
    }

    private void SetExpectation(DeviceState observation)
    {
        foreach (var command in observation.Settings.ToCommands())
        {
            expected[(int)command.Control] = command.Value;
            unobserved[(int)command.Control].Clear();
        }
    }

    private bool DisarmLocked()
    {
        var changed = armed;
        armed = false;
        disarmVersion++;
        CancelPending();
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

    private async Task CleanupAsync()
    {
        Task stops;
        lock (sync)
        {
            if (!cleanupPending)
            {
                return;
            }

            stops = stopWrites;
        }

        await stops.ConfigureAwait(false);
        await DisconnectCoreAsync().ConfigureAwait(false);
        lock (sync)
        {
            cleanupPending = false;
        }
    }

    private async Task WriteStopAsync()
    {
        var command = new DeviceCommand(DeviceControl.Speed, 0);
        lock (sync)
        {
            Expect(command);
        }

        try
        {
            await WriteCommandAsync(command, CancellationToken.None).ConfigureAwait(false);
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

    private async Task CompleteDisposalAsync(Task pending, Task cancellation, bool changed)
    {
        await Task.Yield();
        try
        {
            if (changed)
            {
                NotifyStateChanged();
            }

            await cancellation.ConfigureAwait(false);
            await pending.ConfigureAwait(false);
            ExceptionDispatchInfo? providerDisposalFailure = null;
            try
            {
                await OnDisposingAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                providerDisposalFailure = ExceptionDispatchInfo.Capture(exception);
            }

            try
            {
                await CleanupAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (providerDisposalFailure is not null)
            {
                throw new AggregateException("Provider disposal and cleanup both failed.", providerDisposalFailure.SourceException, cleanupError);
            }

            providerDisposalFailure?.Throw();
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
