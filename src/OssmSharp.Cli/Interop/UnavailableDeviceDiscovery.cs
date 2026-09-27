using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Interop;

internal sealed class UnavailableDeviceDiscovery : IDeviceDiscovery
{
    public Task<IReadOnlyList<DeviceIdentifier>> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IReadOnlyList<DeviceIdentifier>>(
            new PlatformNotSupportedException(
                "Stock BLE discovery requires a native Windows or macOS build."));
    }
}
