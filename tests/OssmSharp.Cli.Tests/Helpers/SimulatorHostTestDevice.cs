using Microsoft.Extensions.Logging.Abstractions;
using OssmSharp.Device.Primitives.Models;
using OssmSharp.Device.Runtime;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class SimulatorHostTestDevice()
    : DeviceBase(new DeviceRuntimeOptions(), TimeProvider.System, NullLogger<SimulatorHostTestDevice>.Instance)
{
    private DeviceState? _state;

    internal bool Disposed { get; private set; }
    internal int MotionCount { get; private set; }
    internal MotionSettings? LastMotion { get; private set; }
    internal TaskCompletionSource? ShutdownGate { get; init; }

    internal void Disconnect() => PublishDisconnected();

    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        PublishConnected(new DeviceInformation("test", "test", null, [MotionPattern.SimpleStroke]));
        _state = new DeviceState
        {
            Timestamp = DateTime.UtcNow,
            MotionSettings = new MotionSettings(MotionDeviceState.StrokeEngineIdle, 0, 20, 50, 50, 0, MotionPattern.SimpleStroke, 0),
            SessionId = Guid.NewGuid(),
        };
        PublishState(_state, isOwnWrite: true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && ShutdownGate is not null)
        {
            await ShutdownGate.Task;
        }
    }

    protected override Task DispatchMotionAsync(MotionSettings motionSettings, CancellationToken cancellationToken)
    {
        MotionCount++;
        LastMotion = motionSettings;
        _state = _state! with { Timestamp = DateTime.UtcNow, MotionSettings = motionSettings };
        PublishState(_state, isOwnWrite: true);
        return Task.CompletedTask;
    }

    protected override ValueTask DisposeCoreAsync()
    {
        Disposed = true;
        return base.DisposeCoreAsync();
    }
}
