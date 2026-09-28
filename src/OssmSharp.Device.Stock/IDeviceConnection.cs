namespace OssmSharp.Device.Stock;

internal interface IDeviceConnection : IAsyncDisposable
{
    Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken);

    Task WriteCommandAsync(Models.Request command, CancellationToken cancellationToken);

    Task EnableKnobLimitingAsync(CancellationToken requestCancellationToken, CancellationToken shutdownToken);
}
