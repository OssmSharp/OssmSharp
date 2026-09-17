namespace OSSMDotNet.Models;

/// <summary>Provides the admin-owned asynchronous boundary to the device adapter.</summary>
public interface IDeviceBridge : IAsyncDisposable
{
    /// <summary>Publishes observed device samples to the admin-owned controller.</summary>
    event Action<DeviceSample>? SampleReceived;

    /// <summary>Writes requested device settings under the supplied command identity.</summary>
    Task WriteAsync(long commandId, DesiredState desired, CancellationToken cancellationToken);

    /// <summary>Requests a device stop under the supplied command identity.</summary>
    Task StopAsync(long commandId, CancellationToken cancellationToken);
}
