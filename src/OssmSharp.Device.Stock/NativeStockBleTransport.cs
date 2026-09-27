#if WINDOWS || MACOS
using Shiny;
using Shiny.BluetoothLE;

namespace OssmSharp.Device.Stock;

internal sealed class NativeStockBleTransport(IBleManager manager) : IStockBleTransport
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

        var peripheral = manager.GetKnownPeripheral(peripheralId)
            ?? await StockBleDiscovery.FindPeripheralAsync(manager, peripheralId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new IOException("The selected stock peripheral was not found in the bounded scan.");

        if (!string.Equals(peripheral.Uuid, peripheralId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The resolved peripheral differs from the selected locator.");
        }

        var connection = new NativeStockBleConnection(peripheral, service);
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
}
#endif
