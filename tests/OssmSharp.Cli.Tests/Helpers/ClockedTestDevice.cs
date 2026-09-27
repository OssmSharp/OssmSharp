using Microsoft.Extensions.Logging.Abstractions;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class ClockedTestDevice : DeviceBase
{
    private readonly TimeProvider _timeProvider;
    private DeviceState? _state;

    internal ClockedTestDevice(TimeProvider timeProvider)
        : base(new DeviceRuntimeOptions(), timeProvider, NullLogger<ClockedTestDevice>.Instance)
    {
        _timeProvider = timeProvider;
    }

    internal int MotionCount { get; private set; }

    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        PublishConnected(new DeviceInformation("test", "test", null, [MotionPattern.SimpleStroke]));
        _state = new DeviceState
        {
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            MotionSettings = new MotionSettings(
                MotionDeviceState.StrokeEngineIdle,
                0,
                20,
                50,
                50,
                0,
                MotionPattern.SimpleStroke,
                0),
            SessionId = Guid.NewGuid(),
        };
        PublishState(_state, isOwnWrite: true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    protected override Task DispatchMotionAsync(
        MotionSettings motionSettings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MotionCount++;
        _state = _state! with
        {
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            MotionSettings = motionSettings,
        };
        PublishState(_state, isOwnWrite: true);
        return Task.CompletedTask;
    }
}
