using OSSMDotNet.DeviceControl;

namespace OSSMDotNet.Web.ServiceBoundary.Tests;

internal sealed class EndpointBridge : IDeviceBridge
{
    private long _sequence;
    private long _revision;
    private DesiredState _desired = DesiredState.Stopped;
    public event Action<DeviceSample>? SampleReceived;
    public int Writes { get; private set; }
    public int Stops { get; private set; }

    public void Publish(long? command = null) => SampleReceived?.Invoke(
        new(++_sequence, _revision, command, _desired, _desired.Speed > 0, _desired.Speed > 0,
            0, 0, true, "ossm-rs-sim-v1"));

    public Task WriteAsync(long commandId, DesiredState desired, CancellationToken cancellationToken)
    {
        Writes++;
        _desired = desired;
        _revision++;
        Publish(commandId);
        return Task.CompletedTask;
    }

    public Task StopAsync(long commandId, CancellationToken cancellationToken)
    {
        Stops++;
        _desired = _desired with { Speed = 0 };
        _revision++;
        Publish(commandId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
