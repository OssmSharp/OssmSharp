using Microsoft.Extensions.Logging.Abstractions;

namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Simulates stock speed matching without a transport.
/// </summary>
/// <param name="timeProvider">The clock used for reported device state.</param>
internal sealed class StockSpeedTestDevice(FakeTimeProvider timeProvider)
    : DeviceBase(
        new DeviceRuntimeOptions
        {
            ExpectationLifetime = TimeSpan.FromSeconds(1),
        },
        timeProvider,
        NullLogger.Instance)
{
    private readonly TaskCompletionSource _dispatchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockDispatch { get; set; }


    public void CompleteDispatch() => _dispatchCompleted.TrySetResult();

    public void Connect() => PublishConnected(new DeviceInformation(
        "simulator",
        "ossm-rs-sim-v1",
        null,
        [MotionPattern.SimpleStroke]));

    public void Disconnect() => PublishDisconnected();

    public void ReportState(MotionSettings settings) => ReportState(new DeviceState
    {
        Timestamp = timeProvider.GetUtcNow().UtcDateTime,
        MotionSettings = settings,
        SessionId = Guid.Empty,
    });

    public void ReportState(DeviceState state) => PublishState(state, null);

    protected override Task ConnectAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    protected override async Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken)
    {
        if (BlockDispatch)
        {
            await _dispatchCompleted.Task;
        }
    }

    protected override Task DispatchSafetyStopAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken) =>
        Task.CompletedTask;

    protected override bool IsExpectedState(
        int? previousRequestedSpeed,
        int? previousStateSpeed,
        int requestedSpeed,
        int stateSpeed) => StockOssmDevice.IsCurrentStateCongruent(
            previousRequestedSpeed,
            previousStateSpeed,
            requestedSpeed,
            stateSpeed);
}
