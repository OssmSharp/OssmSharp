#if WINDOWS || MACOS
using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;
#endif

namespace OssmSharp.Cli.IntegrationTests;

#if WINDOWS || MACOS
internal sealed class NativeStockTestDiscovery(DeviceIdentifier selected) : IDeviceDiscovery
{
    private int _scanCount;

    internal DeviceIdentifier Selected { get; } = selected;

    internal int ScanCount => Volatile.Read(ref _scanCount);

    public Task<IReadOnlyList<DeviceIdentifier>> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _scanCount);
        return Task.FromResult<IReadOnlyList<DeviceIdentifier>>([Selected]);
    }
}
#endif
