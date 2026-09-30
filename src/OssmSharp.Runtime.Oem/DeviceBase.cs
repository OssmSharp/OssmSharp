using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace OssmSharp.Runtime.Oem;

/// <summary>
/// Owns command sequencing, last known state, arming, and the reusable connection lifecycle for a provider.
/// </summary>
/// <remarks>
/// Lifecycle transitions are serialized with device I/O, except that Stop does not wait for an earlier response or live read.
/// Providers must own issued transfers through settlement even when a response wait is abandoned.
/// Writes update expectations, never last known state or a claim about physical motion.
/// </remarks>
public abstract class DeviceBase : IOssmDevice
{
    private readonly Lock sync = new();
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ILogger<DeviceBase> logger;
    private readonly int[] expected = new int[5];
    private readonly Queue<int>[] unobserved = [new(), new(), new(), new(), new()];
    private Task? stop;
    private SettingsSubmission? submission;
    private DeviceState? state;
    private ExceptionDispatchInfo? providerFailure;
    private Task? disposal;
    private CancellationTokenSource? connectionCancellation;
    private Task? connectionStopping;

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
    /// Indicates that the firmware is preparing a pattern; observations replace expectations instead of being checked.
    /// </summary>
    private bool baselineHeld;

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
        CancellationTokenSource session;
        lock (sync)
        {
            ThrowIfInterrupted(attempt);
            connecting = true;
            baselinePending = true;
            cleanupPending = true;
            connectionCancellation = session = new();
            connectionStopping = null;
            version = disarmVersion;
        }

        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
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
            UpdateState(await ReadStateAsync(attemptCancellation.Token).WaitAsync(attemptCancellation.Token).ConfigureAwait(false));
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
    }, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        long disconnection;
        Task? pendingStop;
        TaskCompletionSource? started = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (connected && stop is not { IsCompleted: false })
            {
                started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                stop = started.Task;
            }

            pendingStop = stop is { IsCompleted: false } ? stop : null;
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
        }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
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
                lock (sync)
                {
                    if (IsDiscardedLocked(current))
                    {
                        return current.Result;
                    }

                    baselineHeld = true;
                }

                var ready = false;
                bool prepared;
                try
                {
                    prepared = await TryStepAsync(
                        current,
                        async () => ready = await PreparePatternAsync(current.PendingCancellation.Token).ConfigureAwait(false),
                        exception => new IOException($"Preparing the device to run the pattern failed; no settings were sent. {exception.Message}", exception)).ConfigureAwait(false);
                }
                finally
                {
                    lock (sync)
                    {
                        baselineHeld = false;
                    }
                }

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
                    lock (sync)
                    {
                        if (IsDiscardedLocked(current))
                        {
                            return current.Result;
                        }

                        Expect(command);
                    }

                    if (!await TryStepAsync(
                        current,
                        () => WriteCommandAsync(command, current.PendingCancellation.Token),
                        exception => new DeviceWriteException(command, current.CommandsWritten, exception)).ConfigureAwait(false))
                    {
                        return current.Result;
                    }

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
            }, requiresConnection: true, cancellationToken: current.PendingCancellation.Token).ConfigureAwait(false);
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
        }, requiresConnection: true, connectionToken: connectionToken, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
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
            changed = DisarmLocked();
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

        if (changed)
        {
            NotifyStateChanged();
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

            version = disarmVersion;
        }

        return ExecuteAsync(
            _ =>
        {
            bool changed;
            lock (sync)
            {
                if (stop is { IsCompleted: false } || version != disarmVersion)
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
        }, requiresConnection: true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Disarms, discards commands not yet sent, sends or joins Stop, releases connection resources and subscribers,
    /// and prevents reconnection.
    /// </summary>
    /// <returns>Completion of cleanup; repeated calls observe the same cleanup result.</returns>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        TaskCompletionSource? started = null;
        Task? pendingStop;
        Task disposingTask;
        bool changed;
        lock (sync)
        {
            if (disposal is not null)
            {
                return new(disposal);
            }

            if (connected && stop is not { IsCompleted: false })
            {
                started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                stop = started.Task;
            }

            pendingStop = stop is { IsCompleted: false } ? stop : null;
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
    /// Sends one primitive assignment and awaits the provider's completion semantics.
    /// </summary>
    /// <param name="command">The already validated firmware control assignment.</param>
    /// <param name="cancellationToken" />
    /// <remarks>
    /// A cancelled setting may abandon its terminal response, but its issued transfer must settle before this hook returns.
    /// Stop uses an uncancellable token and awaits its terminal response without waiting for an earlier response or live read.
    /// Do not interpret patterns or accept complete settings here.
    /// </remarks>
    /// <returns>Completion of the issued transfer and, unless abandoned, its terminal response; not confirmation of physical motion.</returns>
    protected abstract Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the firmware to run patterns before a submission writes its controls.
    /// </summary>
    /// <param name="cancellationToken" />
    /// <returns>Whether the firmware now runs patterns; false when it is waiting, for example for homing or its speed knob.</returns>
    /// <remarks>
    /// Called once per submission, so it must succeed without effect when patterns already run. Observations received
    /// before it returns replace expectations instead of disarming; if preparation changes controls, report an
    /// observation of them through <see cref="UpdateState"/> before returning. Cancellation follows <see cref="WriteCommandAsync"/>.
    /// The firmware decides whether motion is safe to start; completion does not confirm physical motion.
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
    /// Records an incoming observation and disarms if changed controls are unexplained by issued assignments.
    /// </summary>
    /// <param name="observation">Decoded controls from a provider read or notification.</param>
    /// <remarks>
    /// Unchanged last-observed controls and unobserved issued assignments are accepted while notifications catch up.
    /// Observing an issued value accounts for earlier assignments to that control; subsequent reversions are external changes.
    /// Speed is not checked because the firmware reports its effective speed, which the speed knob can scale.
    /// The first observation of each connection, and observations during <see cref="PreparePatternAsync"/>, establish
    /// fresh expectations. Observations never arm or cause motion writes,
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

            if (baselinePending || baselineHeld)
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

    /// <summary>
    /// Runs one step of a submission, returning false when the submission was discarded while the step ran.
    /// </summary>
    private async Task<bool> TryStepAsync(SettingsSubmission current, Func<Task> step, Func<Exception, IOException> failure)
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

    private bool IsDiscardedLocked(SettingsSubmission current)
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

        var previous = state.ComparableControls();
        var incoming = observation.ComparableControls();
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
        if (command.Control == DeviceControl.Speed)
        {
            return;
        }

        var index = (int)command.Control;
        if (expected[index] != command.Value)
        {
            unobserved[index].Enqueue(command.Value);
            expected[index] = command.Value;
        }
    }

    private void SetExpectation(DeviceState observation)
    {
        foreach (var command in observation.ComparableControls())
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

        if (failures.Count > 1)
        {
            throw new AggregateException("Cancellation and Stop failed.", failures);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
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
            completion.TrySetException(exception);
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
