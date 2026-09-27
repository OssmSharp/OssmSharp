namespace OssmSharp.Device.Stock;

/// <summary>
/// Bounds the wait for a native disconnect without mistaking cancellation for settlement.
/// </summary>
internal static class StockBleDisconnection
{
    internal static async Task DisconnectAsync(
        Func<CancellationToken, Task> disconnect,
        TimeProvider timeProvider,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(disconnect);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        try
        {
            await disconnect(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Stock BLE disconnect did not settle before the deadline.", exception);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("Stock BLE disconnect did not settle before the deadline.", exception);
        }
    }
}
