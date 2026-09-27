#if WINDOWS || MACOS
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal static class StockBleDiscovery
{
    /// <summary>
    /// Finds up to 64 distinct Stock peripherals in one five-second scan.
    /// </summary>
    internal static async Task<IReadOnlyList<(string Name, string Id)>> ScanAsync(
        IBleManager manager,
        CancellationToken cancellationToken)
    {
        var candidates = await ScanResultsAsync(manager, cancellationToken).ConfigureAwait(false);
        return candidates.Select(result => (
            Name: StockBleDeviceName.Select(
                result.AdvertisementData.LocalName,
                result.Peripheral.Name),
            Id: result.Peripheral.Uuid)).ToArray();
    }

    /// <summary>
    /// Resolves only the selected ID from the same bounded discovery used for listing.
    /// </summary>
    internal static async Task<IPeripheral?> FindPeripheralAsync(
        IBleManager manager,
        string peripheralId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peripheralId);
        var candidates = await ScanResultsAsync(manager, cancellationToken).ConfigureAwait(false);
        return candidates
            .FirstOrDefault(result => Matches(
                result.Peripheral.Uuid,
                result.AdvertisementData.ServiceUuids,
                peripheralId,
                StockFirmwareProfile.Service))
            ?.Peripheral;
    }

    internal static bool Matches(
        string seenLocator,
        IEnumerable<string>? advertisedServices,
        string expectedLocator,
        Guid service) =>
        string.Equals(seenLocator, expectedLocator, StringComparison.OrdinalIgnoreCase)
        && HasService(advertisedServices, service);

    private static bool HasService(IEnumerable<string>? advertisedServices, Guid service) =>
        advertisedServices?.Any(uuid => Guid.TryParse(uuid, out var advertised) && advertised == service) == true;

    private static async Task<IReadOnlyList<ScanResult>> ScanResultsAsync(
        IBleManager manager,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var access = await manager.RequestAccessAsync().WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (access != AccessState.Available)
        {
            throw new UnauthorizedAccessException("Bluetooth access is unavailable.");
        }

        var service = StockFirmwareProfile.Service;
        var candidates = await manager.Scan(new ScanConfig(service.ToString()))
            .Where(result => HasService(result.AdvertisementData.ServiceUuids, service))
            .Where(result => !string.IsNullOrWhiteSpace(result.Peripheral.Uuid))
            .Distinct(result => result.Peripheral.Uuid, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .Buffer(TimeSpan.FromSeconds(5))
            .Take(1)
            .ToTask(cancellationToken).ConfigureAwait(false);

        return candidates.ToArray();
    }
}
#endif
