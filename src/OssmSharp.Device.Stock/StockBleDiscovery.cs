#if WINDOWS || MACOS
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal static class StockBleDiscovery
{
    internal static async Task<IReadOnlyList<(string Name, string Id)>> ScanAsync(
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
            .Where(result => result.AdvertisementData.ServiceUuids?.Any(
                uuid => Guid.TryParse(uuid, out var advertised) && advertised == service) == true)
            .Distinct(result => result.Peripheral.Uuid)
            .Take(64)
            .Buffer(TimeSpan.FromSeconds(5))
            .Take(1)
            .ToTask(cancellationToken).ConfigureAwait(false);

        return candidates.Select(result => (
            Name: result.AdvertisementData.LocalName ?? result.Peripheral.Name ?? "Unnamed device",
            Id: result.Peripheral.Uuid)).ToArray();
    }
}
#endif
