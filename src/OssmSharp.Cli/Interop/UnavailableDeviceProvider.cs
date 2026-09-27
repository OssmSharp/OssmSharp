using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Interop;

internal sealed class UnavailableDeviceProvider : IDeviceProvider
{
    public Task<DeviceIdentityDetails> ReadInformationAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<DeviceIdentityDetails>(
            new PlatformNotSupportedException(
                "Stock BLE details require a Windows or macOS native BLE build."));
    }
}
