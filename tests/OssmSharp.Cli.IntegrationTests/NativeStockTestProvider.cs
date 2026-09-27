#if WINDOWS || MACOS
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
#endif

namespace OssmSharp.Cli.IntegrationTests;

#if WINDOWS || MACOS
internal sealed class NativeStockTestProvider(DeviceIdentifier selected) : IDeviceProvider
{
    private int _scanCount;

    internal DeviceIdentifier Selected { get; } = selected;

    internal int ScanCount => Volatile.Read(ref _scanCount);

    public Task<IReadOnlyList<DeviceIdentifier>> GetAvailableDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _scanCount);
        return Task.FromResult<IReadOnlyList<DeviceIdentifier>>([Selected]);
    }

    public Task<DeviceInformation> GetDeviceAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The prompt must not read device details before connecting.");
}
#endif
