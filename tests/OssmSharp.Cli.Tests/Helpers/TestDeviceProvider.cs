using OssmSharp.Device.Primitives;
using OssmSharp.Device.Primitives.Models;

namespace OssmSharp.Cli.Tests.Helpers;

internal sealed class TestDeviceProvider : IDeviceProvider
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DeviceIdentifier>>> _scan;
    private readonly Func<DeviceIdentifier, CancellationToken, Task<DeviceInformation>> _read;

    internal TestDeviceProvider(
        IReadOnlyList<DeviceIdentifier> devices,
        DeviceInformation? result = null)
        : this(_ => Task.FromResult(devices), (_, _) => Task.FromResult(
            result ?? throw new InvalidOperationException("Device read was not expected.")))
    {
    }

    internal TestDeviceProvider(
        IReadOnlyList<DeviceIdentifier> devices,
        Func<DeviceIdentifier, CancellationToken, Task<DeviceInformation>> read)
        : this(_ => Task.FromResult(devices), read)
    {
    }

    internal TestDeviceProvider(
        Func<CancellationToken, Task<IReadOnlyList<DeviceIdentifier>>> scan,
        Func<DeviceIdentifier, CancellationToken, Task<DeviceInformation>>? read = null)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _read = read ?? ((_, _) => throw new InvalidOperationException("Device read was not expected."));
    }

    internal int ScanCount { get; private set; }

    internal CancellationToken LastScanCancellationToken { get; private set; }

    internal DeviceIdentifier? Identifier { get; private set; }

    internal CancellationToken LastReadCancellationToken { get; private set; }

    public Task<IReadOnlyList<DeviceIdentifier>> GetAvailableDevicesAsync(CancellationToken cancellationToken)
    {
        ScanCount++;
        LastScanCancellationToken = cancellationToken;
        return _scan(cancellationToken);
    }

    public Task<DeviceInformation> GetDeviceAsync(
        DeviceIdentifier device,
        CancellationToken cancellationToken)
    {
        Identifier = device;
        LastReadCancellationToken = cancellationToken;
        return _read(device, cancellationToken);
    }
}
