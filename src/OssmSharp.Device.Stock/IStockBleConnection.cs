namespace OssmSharp.Device.Stock;

internal interface IStockBleConnection : IAsyncDisposable
{
    Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken);
}
