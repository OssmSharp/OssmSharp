namespace OssmSharp.Device.Runtime.Tests;

/// <summary>
/// Exercises shared device behavior with a fake clock and explicitly controlled transport settlement.
/// </summary>
/// <param name="timeProvider">The clock shared with the test.</param>
/// <param name="logger">The logger tracked by the test.</param>
public sealed class TestDevice(
    FakeTimeProvider timeProvider,
    ILogger<TestDevice> logger)
    : DeviceBase(
        timeProvider,
        logger)
{
    /// <summary>
    /// Provides the identity reported by this fake transport.
    /// </summary>
    public static readonly DeviceInformation SimulatorInformation = new(
        "simulator",
        "ossm-rs-sim-v1",
        null,
        [MotionPattern.SimpleStroke]);

    private static readonly MotionSettings StoppedSettings = new(
        MotionDeviceState.StrokeEngineIdle, 0, 10, 50, 20, 0, MotionPattern.SimpleStroke, 0);

    private readonly TaskCompletionSource _dispatchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _preparationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _preparationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _coreDisposalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _coreDisposalCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _coreDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _dispatchError;
    private int _coreDisposalCount;
    private bool _dispatchingSafetyStop;

    /// <summary>
    /// Gets or sets whether writes wait for <see cref="CompleteDispatch"/>; configure before issuing a write.
    /// </summary>
    public bool BlockDispatch { get; set; }

    /// <summary>Gets or sets whether provider preflight waits until explicitly settled.</summary>
    public bool BlockPreparation { get; set; }

    /// <summary>Gets when provider preflight has entered its owned native operation.</summary>
    public Task PreparationStarted => _preparationStarted.Task;

    /// <summary>
    /// Gets or sets whether core cleanup waits for <see cref="CompleteCoreDisposal"/>.
    /// </summary>
    public bool BlockCoreDisposal { get; set; }

    /// <summary>
    /// Gets or sets whether startup reports a connection; configure before starting the device.
    /// </summary>
    public bool ConnectOnStart { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the connection waits for explicit transport settlement after stop.
    /// </summary>
    public bool IgnoreStopCancellation { get; set; }

    /// <summary>
    /// Gets or sets whether dispatch reports its own state before settling; configure before issuing a write.
    /// </summary>
    public bool ReportStateDuringDispatch { get; set; }

    /// <summary>Simulates a transport error after an irreversible write was issued.</summary>
    public bool IssueBeforeFailure { get; set; }

    /// <summary>Validates requested settings before the fake records a write.</summary>
    public Action<MotionSettings>? MotionValidation { get; set; }

    /// <summary>Observes the dispatch kind before the fake records a write.</summary>
    public Action<MotionSettings, bool>? BeforeDispatch { get; set; }

    /// <summary>
    /// Gets completion when a write reaches the transport.
    /// </summary>
    public Task DispatchStarted => _dispatchStarted.Task;

    /// <summary>
    /// Gets completion when core transport cleanup begins.
    /// </summary>
    public Task CoreDisposalStarted => _coreDisposalStarted.Task;

    /// <summary>
    /// Gets completion when the base releases transport resources.
    /// </summary>
    public Task CoreDisposed => _coreDisposed.Task;

    /// <summary>
    /// Gets the number of times core transport cleanup started.
    /// </summary>
    public int CoreDisposalCount => Volatile.Read(ref _coreDisposalCount);

    /// <summary>
    /// Gets the number of writes issued to the transport.
    /// </summary>
    public int DispatchCount { get; private set; }

    /// <summary>
    /// Settles blocked transport writes independently of caller cancellation.
    /// </summary>
    public void CompleteDispatch() => _dispatchCompleted.TrySetResult();

    /// <summary>Settles a delayed provider preflight after Stop has latched.</summary>
    public void CompletePreparation() => _preparationCompleted.TrySetResult();

    /// <summary>
    /// Settles blocked core transport cleanup.
    /// </summary>
    public void CompleteCoreDisposal() => _coreDisposalCompleted.TrySetResult();

    /// <summary>
    /// Configures the transport error raised during dispatch.
    /// </summary>
    /// <param name="exception">The transport error to raise, or <see langword="null"/> to stop failing.</param>
    public void FailDispatchWith(Exception? exception) => _dispatchError = exception;

    /// <summary>
    /// Ends the transport with an optional failure.
    /// </summary>
    /// <param name="error">The transport failure, or null for normal termination.</param>
    public void EndTransport(Exception? error = null)
    {
        if (error is null)
        {
            _stopped.TrySetResult();
        }
        else
        {
            _stopped.TrySetException(error);
        }
    }

    /// <summary>
    /// Reports a connection and the fake transport's identity.
    /// </summary>
    public void Connect() => PublishConnected(SimulatorInformation);

    /// <summary>
    /// Reports connection loss without changing device state.
    /// </summary>
    public void Disconnect() => PublishDisconnected();

    /// <summary>
    /// Reports a device error without changing the connection.
    /// </summary>
    /// <param name="exception">The error to publish.</param>
    public void Fail(Exception exception) => PublishError(exception);

    /// <summary>
    /// Sends state fields through the base validation and publication path.
    /// </summary>
    /// <param name="settings">The reported settings.</param>
    /// <param name="timestamp">The sample timestamp.</param>
    /// <param name="velocity">The reported velocity, or null when unknown.</param>
    public void ReportState(MotionSettings settings, DateTime timestamp, double? velocity = null) =>
        ReportState(new DeviceState
        {
            Timestamp = timestamp,
            MotionSettings = settings,
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = velocity,
        });

    /// <summary>
    /// Sends state through the base validation and publication path.
    /// </summary>
    /// <param name="state">The state to validate and publish.</param>
    public void ReportState(DeviceState state) => PublishState(state, null);

    /// <summary>
    /// Sends attributed state through the base validation and publication path.
    /// </summary>
    /// <param name="state">The state to validate and publish.</param>
    /// <param name="isOwnWrite">Whether the transport attributes the state to the pending write.</param>
    public void ReportState(DeviceState state, bool? isOwnWrite) => PublishState(state, isOwnWrite);

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectOnStart)
        {
            PublishConnected(SimulatorInformation);
        }

        using var registration = IgnoreStopCancellation
            ? default
            : cancellationToken.Register(() => _stopped.TrySetResult());
        await _stopped.Task;
    }

    /// <inheritdoc />
    protected override void ValidateMotion(MotionSettings settings) =>
        MotionValidation?.Invoke(settings);

    /// <inheritdoc />
    protected override async Task PrepareAsync(CancellationToken requestCancellationToken, CancellationToken shutdownToken)
    {
        _preparationStarted.TrySetResult();
        if (BlockPreparation)
            await _preparationCompleted.Task;
        requestCancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    protected override Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken) =>
        DispatchCoreAsync(motionSettings);

    /// <inheritdoc />
    protected override async Task DispatchSafetyStopAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        // Records the speed-only write as a stopped tuple so tests can read it like any other write.
        var zero = (LatestState?.MotionSettings ?? StoppedSettings) with { Speed = 0 };
        _dispatchingSafetyStop = true;
        try
        {
            await DispatchMotionAsync(zero, requestCancellationToken, shutdownToken);
        }
        finally
        {
            _dispatchingSafetyStop = false;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        Interlocked.Increment(ref _coreDisposalCount);
        _coreDisposalStarted.TrySetResult();
        if (BlockCoreDisposal)
        {
            await _coreDisposalCompleted.Task;
        }

        _coreDisposed.TrySetResult();
        await base.DisposeCoreAsync();
    }

    private async Task DispatchCoreAsync(MotionSettings motionSettings)
    {
        if (BeforeDispatch is { } beforeDispatch)
        {
            await Task.Yield();
            beforeDispatch(motionSettings, _dispatchingSafetyStop);
        }
        DispatchCount++;
        _dispatchStarted.TrySetResult();
        if (IssueBeforeFailure)
        {
            MarkWriteIssued();
        }

        if (_dispatchError is { } error)
        {
            throw error;
        }

        if (ReportStateDuringDispatch)
        {
            PublishState(
                new DeviceState
                {
                    Timestamp = timeProvider.GetUtcNow().UtcDateTime,
                    MotionSettings = motionSettings,
                    SessionId = Guid.Empty,
                },
                true);
        }

        if (BlockDispatch)
        {
            await _dispatchCompleted.Task;
        }
    }
}
