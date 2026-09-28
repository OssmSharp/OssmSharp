using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using OssmSharp.Device.Primitives;

namespace OssmSharp.Device.Runtime;

/// <summary>
/// Owns command sequencing, last known state, and explicit arming for a provider connection.
/// </summary>
/// <remarks>
/// Providers must settle issued I/O before completing their hooks, including on cancellation or error.
/// Writes update expectations, never last known state or a claim about physical motion.
/// Dispose the instance after a failed connection attempt before creating another connection.
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
    private SettingsSubmission? submission;
    private DeviceState? state;
    private ExceptionDispatchInfo? providerFailure;
    private Task? disposal;
    private int operationCount;
    private int pendingStops;
    private long disarmVersion;
    private bool connected;
    private bool connectionAttempted;
    private bool armed;
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
        async () =>
    {
        lock (sync)
        {
            if (connected)
            {
                return true;
            }

            if (connectionAttempted)
            {
                throw new InvalidOperationException("Dispose this device before attempting another connection.");
            }

            connectionAttempted = true;
        }

        try
        {
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            UpdateState(await ReadStateAsync(cancellationToken).ConfigureAwait(false));
            await CompleteStateReadAsync(cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                providerFailure?.Throw();
                // Prevents the instance from being marked as connected
                // if Dispose is called while the connectiong is being established.
                // If it's disposing, connected is forced to be false.
                connected = !disposing;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Disarm();
            logger.OperationFailed("connect", exception);
            throw;
        }
    }, cancellationToken);

    /// <inheritdoc />
    public async Task<SettingsResult> SetAsync(MotionSettings settings, CancellationToken cancellationToken = default)
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
                return new(SettingsOutcome.Cancelled, 0);
            }

            current = Supersede(settings, pendingCancellation);
        }

        try
        {
            return await ExecuteAsync(
                async () =>
            {
                foreach (var command in current.Settings.ToCommands())
                {
                    lock (sync)
                    {
                        if (current.PendingCancellation.IsCancellationRequested || !armed || disposing)
                        {
                            if (current.Outcome == SettingsOutcome.Completed)
                            {
                                current.Outcome = SettingsOutcome.Cancelled;
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
                    if (current.PendingCancellation.IsCancellationRequested && current.Outcome == SettingsOutcome.Completed)
                    {
                        current.Outcome = SettingsOutcome.Cancelled;
                    }

                    return current.Result;
                }
            }, current.PendingCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (current.PendingCancellation.IsCancellationRequested || disposing)
        {
            lock (sync)
            {
                if (current.Outcome == SettingsOutcome.Completed)
                {
                    current.Outcome = SettingsOutcome.Cancelled;
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
            async () =>
        {
            try
            {
                var observation = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
                UpdateState(observation);
                await CompleteStateReadAsync(cancellationToken).ConfigureAwait(false);
                return observation;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Disarm();
                logger.OperationFailed("live read", exception);
                throw;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        lock (sync)
        {
            EnsureConnected();
            changed = DisarmLocked();
            pendingStops++;
        }

        if (changed)
        {
            NotifyStateChanged();
        }

        try
        {
            await ExecuteAsync(
                async () =>
            {
                var command = new DeviceCommand(DeviceControl.Speed, 0);
                lock (sync)
                {
                    Expect(command);
                }

                try
                {
                    await WriteCommandAsync(command, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }
                catch (Exception exception)
                {
                    logger.OperationFailed("Stop write", exception);
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                pendingStops--;
            }
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
            () =>
        {
            bool changed;
            lock (sync)
            {
                EnsureConnected();
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
        }, cancellationToken);
    }

    /// <summary>
    /// Disarms, cancels unissued operations, awaits issued I/O, and releases provider subscriptions and connection.
    /// </summary>
    /// <returns>Completion of cleanup; repeated calls observe the same cleanup outcome.</returns>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (sync)
        {
            if (disposal is null)
            {
                disposing = true;
                var changed = DisarmLocked();
                disposal = DisposeCoreAsync(drained.Task, shutdown.CancelAsync(), changed);
            }

            return new(disposal);
        }
    }

    /// <summary>
    /// Opens the provider connection and subscribes to state observations using <see cref="UpdateState"/>.
    /// </summary>
    /// <returns>Completion of connection and subscription setup, without any motion writes.</returns>
    protected abstract Task ConnectCoreAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends exactly one primitive assignment and propagates provider errors.
    /// </summary>
    /// <param name="command">The already validated firmware control assignment.</param>
    /// <param name="cancellationToken" />
    /// <remarks>
    /// Complete only after issued I/O settles, even on cancellation. Do not interpret patterns or accept complete settings here.
    /// </remarks>
    /// <returns>Completion of the issued write, not confirmation of physical motion.</returns>
    protected abstract Task WriteCommandAsync(DeviceCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Reads and decodes live firmware controls without a provider-side state cache.
    /// </summary>
    /// <returns>A decoded observation; malformed responses and provider errors must propagate.</returns>
    protected abstract Task<DeviceState> ReadStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Completes provider read cleanup after Runtime records a successful observation.
    /// </summary>
    /// <returns>A task completing when any temporarily suspended observations have resumed.</returns>
    /// <remarks>
    /// Called under the I/O gate after a successful read. Failure is surfaced as part of that read.
    /// Providers whose read fails must settle their cleanup before throwing from <see cref="ReadStateAsync"/>.
    /// </remarks>
    protected virtual Task CompleteStateReadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Releases the provider connection and subscriptions after all issued operations settle.
    /// </summary>
    /// <returns>Completion of cleanup, including after a partial connection failure.</returns>
    protected abstract Task DisconnectAsync();

    /// <summary>
    /// Records a terminal asynchronous provider failure, disarms, and cancels unissued settings without issuing any I/O.
    /// </summary>
    /// <param name="exception">The connection or notification failure to propagate to subsequent operations.</param>
    /// <remarks>
    /// Last known state remains available from cached reads. Reset cannot clear a failed connection;
    /// dispose this instance and create a new one. Issued I/O is still awaited before cleanup.
    /// </remarks>
    protected void FailConnection(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (sync)
        {
            if (disposing || providerFailure is not null)
            {
                return;
            }

            providerFailure = ExceptionDispatchInfo.Capture(exception);
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
    /// A first observation establishes a baseline. Observations never arm or cause motion writes.
    /// Subscriber failures are logged and cannot interrupt device execution.
    /// </remarks>
    protected void UpdateState(DeviceState observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool changed;
        bool external;
        lock (sync)
        {
            if (disposing || providerFailure is not null)
            {
                return;
            }

            if (state is null)
            {
                SetExpectation(observation);
            }

            external = DetectExternalChange(observation);
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

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
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
                lock (sync)
                {
                    providerFailure?.Throw();
                }

                return await operation().ConfigureAwait(false);
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
            submission.Outcome = SettingsOutcome.Superseded;
            submission.PendingCancellation.Cancel();
        }

        submission = new(settings, pendingCancellation);
        return submission;
    }

    private void CancelPending()
    {
        if (submission is not null)
        {
            submission.Outcome = SettingsOutcome.Cancelled;
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

        if (!unexplained || !armed)
        {
            return false;
        }

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
        providerFailure?.Throw();
        if (!connected)
        {
            throw new InvalidOperationException("Connect before using the device.");
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

    private async Task DisposeCoreAsync(Task pending, Task cancellation, bool changed)
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
            if (connectionAttempted)
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (sync)
            {
                connected = false;
                StateChanged = null;
            }

            io.Dispose();
            shutdown.Dispose();
        }
    }
}
