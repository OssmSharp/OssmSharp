using OssmSharp.Device.Stock.Models;

namespace OssmSharp.Device.Stock.Tests;

internal sealed class RecordingStockConnection(StockGattProfile profile) : IDeviceConnection
{
    internal List<string> Commands { get; } = [];

    internal Action<Request>? AfterWrite { get; set; }

    public Task<StockGattProfile> ReadProfileAsync(CancellationToken cancellationToken) =>
        Task.FromResult(profile);

    public Task WriteCommandAsync(Request request, CancellationToken cancellationToken)
    {
        Commands.Add(request.ToPayload());
        AfterWrite?.Invoke(request);
        return Task.CompletedTask;
    }

    public Task EnableKnobLimitingAsync(
        CancellationToken requestCancellationToken,
        CancellationToken shutdownToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
