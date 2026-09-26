#if WINDOWS
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal sealed class WindowsStockBleTransport(IBleManager manager) : IStockBleTransport
{
    public async Task<IStockBleConnection> ConnectAsync(
        string peripheralId,
        Guid service,
        CancellationToken cancellationToken)
    {
        var access = await manager.RequestAccessAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (access != AccessState.Available)
        {
            throw new UnauthorizedAccessException("Bluetooth access is unavailable.");
        }

        var peripheral = manager.GetKnownPeripheral(peripheralId);
        if (peripheral is null)
        {
            peripheral = await manager.Scan(new ScanConfig(service.ToString()))
                .Where(result => Matches(
                    result.Peripheral.Uuid, result.AdvertisementData.ServiceUuids, peripheralId, service))
                .Select(result => result.Peripheral)
                .Take(1)
                .ToTask(cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(peripheral.Uuid, peripheralId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The resolved peripheral differs from the enrolled locator.");
        }

        var connection = new WindowsStockBleConnection(peripheral, service);
        try
        {
            await peripheral.ConnectAsync(new ConnectionConfig(AutoConnect: false), cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static bool Matches(
        string seenLocator,
        IEnumerable<string>? advertisedServices,
        string expectedLocator,
        Guid service) =>
        string.Equals(seenLocator, expectedLocator, StringComparison.OrdinalIgnoreCase) &&
        advertisedServices?.Any(uuid => Guid.TryParse(uuid, out var advertised) && advertised == service) == true;
}
#endif
