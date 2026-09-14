namespace OSSMDotNet.DeviceControl;

public interface IDeviceBridge : IAsyncDisposable
{
    event Action<DeviceSample>? SampleReceived;
    Task WriteAsync(long commandId, DesiredState desired, CancellationToken cancellationToken);
    Task StopAsync(long commandId, CancellationToken cancellationToken);
}
