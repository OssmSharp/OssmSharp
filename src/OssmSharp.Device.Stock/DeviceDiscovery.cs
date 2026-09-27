#if WINDOWS || MACOS
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

/// <summary>Discovers stock peripherals advertising the required GATT service.</summary>
public sealed class DeviceDiscovery(
    Lazy<IBleManager> nativeManager,
    IScheduler? scheduler = null) : IDeviceDiscovery
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceIdentifier>> ScanAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nativeManager);
        cancellationToken.ThrowIfCancellationRequested();
        var manager = nativeManager.Value;
        var access = await manager
            .RequestAccessAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (access != AccessState.Available)
        {
            throw new UnauthorizedAccessException("Bluetooth access is unavailable.");
        }

        var service = StockFirmwareProfile.Service;
        var candidates = await manager
            .Scan(new ScanConfig(service.ToString()))
            .Where(result => HasService(result.AdvertisementData.ServiceUuids, service))
            .Where(result => !string.IsNullOrWhiteSpace(result.Peripheral.Uuid))
            .Distinct(result => result.Peripheral.Uuid, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .Buffer(TimeSpan.FromSeconds(5), scheduler ?? DefaultScheduler.Instance)
            .Take(1)
            .ToTask(cancellationToken).ConfigureAwait(false);

        return candidates
            .Select(result => (DeviceIdentifier)new StockDeviceIdentifier(
                result.Peripheral.Uuid,
                StockBleDeviceName.Select(result.AdvertisementData.LocalName, result.Peripheral.Name),
                result.Peripheral,
                manager))
            .ToArray();
    }

    private static bool HasService(IEnumerable<string>? advertisedServices, Guid service) =>
        advertisedServices?.Any(uuid => Guid.TryParse(uuid, out var advertised) && advertised == service) == true;
}
#endif
