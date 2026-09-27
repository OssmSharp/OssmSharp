#if WINDOWS || MACOS
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal sealed class NativeStockBleTransport(IBleManager manager, IDeviceProvider provider) : IDeviceTransport
{
    public async Task<IDeviceConnection> ConnectAsync(
        DeviceIdentifier selected,
        Guid service,
        CancellationToken cancellationToken)
    {
        var peripheral = StockDeviceIdentifier.Validate(selected, manager);
        var access = await manager.RequestAccessAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (access != AccessState.Available)
        {
            throw new UnauthorizedAccessException("Bluetooth access is unavailable.");
        }

        var resolved = manager.GetKnownPeripheral(selected.Id);
        if (resolved is null)
        {
            var candidates = await provider.GetAvailableDevicesAsync(cancellationToken).ConfigureAwait(false);
            var found = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                resolved = StockDeviceIdentifier.Validate(found, manager);
        }
        if (resolved is null)
            throw new IOException("The selected stock peripheral was not found in the bounded scan.");

        if (!string.Equals(resolved.Uuid, peripheral.Uuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The resolved peripheral differs from the selected locator.");
        }

        var connection = new NativeStockBleConnection(resolved, service);
        try
        {
            await resolved.ConnectAsync(new ConnectionConfig(AutoConnect: false), cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
#endif
