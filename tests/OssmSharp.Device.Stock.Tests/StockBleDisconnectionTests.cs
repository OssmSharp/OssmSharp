namespace OssmSharp.Device.Stock.Tests;

/// <summary>
/// Tests fail-closed native BLE cleanup without opening an adapter.
/// </summary>
public sealed class StockBleDisconnectionTests
{
    [Fact]
    public async Task DisconnectThatNeverSettlesFailsAtDeadlineAndCancelsWait()
    {
        var clock = new FakeTimeProvider();
        var nativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken nativeToken = default;
        var cleanup = StockBleDisconnection.DisconnectAsync(
            token =>
            {
                nativeToken = token;
                return nativeCompletion.Task;
            },
            clock,
            TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => cleanup);
        Assert.Contains("did not settle", error.Message, StringComparison.Ordinal);
        Assert.True(nativeToken.IsCancellationRequested);
        Assert.False(nativeCompletion.Task.IsCompleted);
        nativeCompletion.TrySetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => cleanup);
    }

    [Fact]
    public async Task DisconnectThatSettlesBeforeDeadlineCompletesSuccessfully()
    {
        var clock = new FakeTimeProvider();
        var nativeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = StockBleDisconnection.DisconnectAsync(
            _ => nativeCompletion.Task, clock, TimeSpan.FromSeconds(5));

        nativeCompletion.TrySetResult();
        await cleanup;
    }

    [Fact]
    public async Task NativeDisconnectFailureIsNotReportedAsSuccessfulCleanup()
    {
        var nativeError = new IOException("Native disconnect failed.");

        var result = await Assert.ThrowsAsync<IOException>(() => StockBleDisconnection.DisconnectAsync(
            _ => Task.FromException(nativeError), new FakeTimeProvider(), TimeSpan.FromSeconds(5)));

        Assert.Same(nativeError, result);
    }
}
