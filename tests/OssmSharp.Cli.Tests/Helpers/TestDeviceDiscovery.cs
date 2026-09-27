using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class TestDeviceDiscovery : IDeviceDiscovery
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DeviceIdentifier>>> _scan;

    internal TestDeviceDiscovery(IReadOnlyList<DeviceIdentifier> devices)
        : this(_ => Task.FromResult(devices))
    {
    }

    internal TestDeviceDiscovery(
        Func<CancellationToken, Task<IReadOnlyList<DeviceIdentifier>>> scan)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
    }

    internal int ScanCount { get; private set; }

    internal CancellationToken LastCancellationToken { get; private set; }

    public Task<IReadOnlyList<DeviceIdentifier>> ScanAsync(CancellationToken cancellationToken)
    {
        ScanCount++;
        LastCancellationToken = cancellationToken;
        return _scan(cancellationToken);
    }
}
