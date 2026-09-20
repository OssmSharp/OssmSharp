using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace OSSMDotNet.Tests;

/// <summary>
/// Exercises shared device behavior with a fake clock and explicitly controlled transport settlement.
/// </summary>
/// <param name="timeProvider">The clock shared with the test.</param>
public sealed class TestDevice(FakeTimeProvider timeProvider)
    : OssmDeviceBase(
        new OssmDeviceOptions
        {
            ObservationLiveFor = TimeSpan.FromSeconds(2),
            ExpectationLifetime = TimeSpan.FromSeconds(1),
        },
        timeProvider,
        NullLogger.Instance)
{
    /// <summary>
    /// Provides the identity reported by this fake transport.
    /// </summary>
    public static readonly DeviceInformation SimulatorInformation = new(
        "simulator",
        "ossm-rs-sim-v1",
        null,
        [MotionPattern.SimpleStroke]);

    private readonly TaskCompletionSource _dispatchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _coreDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets or sets whether writes wait for <see cref="CompleteDispatch"/>; configure before issuing a write.
    /// </summary>
    public bool BlockDispatch { get; set; }

    /// <summary>
    /// Gets or sets whether startup reports a connection; configure before starting the device.
    /// </summary>
    public bool ConnectOnStart { get; set; } = true;

    /// <summary>
    /// Gets or sets whether dispatch reports its own observation before settling; configure before issuing a write.
    /// </summary>
    public bool ObserveDuringDispatch { get; set; }

    /// <summary>
    /// Gets completion when a write reaches the transport.
    /// </summary>
    public Task DispatchStarted => _dispatchStarted.Task;

    /// <summary>
    /// Gets completion when the base releases transport resources.
    /// </summary>
    public Task CoreDisposed => _coreDisposed.Task;

    /// <summary>
    /// Gets the number of writes issued to the transport.
    /// </summary>
    public int DispatchCount { get; private set; }

    /// <summary>
    /// Settles blocked transport writes independently of caller cancellation.
    /// </summary>
    public void CompleteDispatch() => _dispatchCompleted.TrySetResult();

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
    /// Reports connection loss without changing observations.
    /// </summary>
    public void Disconnect() => PublishDisconnected();

    /// <summary>
    /// Reports a device error without changing the connection.
    /// </summary>
    /// <param name="exception">The error to publish.</param>
    public void Fail(Exception exception) => PublishError(exception);

    /// <summary>
    /// Sends sample fields through the base's observation validation and publication path.
    /// </summary>
    /// <param name="settings">The observed settings.</param>
    /// <param name="timestamp">The sample timestamp.</param>
    /// <param name="velocity">The reported velocity, or null when unknown.</param>
    public void Observe(MotionSettings settings, DateTime timestamp, double? velocity = null) =>
        Observe(new DeviceState
        {
            Timestamp = timestamp,
            MotionSettings = settings,
            SessionId = Guid.Empty,
            VelocityMillimetresPerSecond = velocity,
        });

    /// <summary>
    /// Sends a sample through the base's observation validation and publication path.
    /// </summary>
    /// <param name="state">The sample to validate and publish.</param>
    public void Observe(DeviceState state) => PublishObservation(state, null);

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectOnStart)
        {
            PublishConnected(SimulatorInformation);
        }

        using var registration = cancellationToken.Register(() => _stopped.TrySetResult());
        await _stopped.Task;
    }

    /// <inheritdoc />
    protected override async Task DispatchMotionAsync(MotionSettings motionSettings, CancellationToken cancellationToken)
    {
        DispatchCount++;
        _dispatchStarted.TrySetResult();
        if (ObserveDuringDispatch)
        {
            PublishObservation(
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

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        _coreDisposed.TrySetResult();
        await base.DisposeCoreAsync();
    }
}