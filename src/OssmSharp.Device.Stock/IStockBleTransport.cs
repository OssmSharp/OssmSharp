namespace OssmSharp.Device.Stock;

internal interface IStockBleTransport
{
    Task<IStockBleConnection> ConnectAsync(string peripheralId, Guid service, CancellationToken cancellationToken);
}
