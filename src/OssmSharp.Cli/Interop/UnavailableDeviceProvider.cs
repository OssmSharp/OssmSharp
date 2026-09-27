using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Interop;

internal sealed class UnavailableDeviceProvider : IDeviceProvider
{
    public Task<IReadOnlyList<DeviceIdentifier>> GetAvailableDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IReadOnlyList<DeviceIdentifier>>(
            new PlatformNotSupportedException(
                "Stock BLE discovery requires a native Windows or macOS build."));
    }

    public Task<DeviceInformation> GetDeviceAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<DeviceInformation>(
            new PlatformNotSupportedException(
                "Stock BLE details require a Windows or macOS native BLE build."));
    }
}
