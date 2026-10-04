using FastMoq;
using Xunit;

namespace OssmSharp.Runtime.Oem.Tests;

public sealed class BleTestContextTests : MockerTestBase<object>, IAsyncDisposable
{
    protected override Func<Mocker, object> CreateComponentAction => mocks => new BleTestContext(mocks);

    private new BleTestContext Component => (BleTestContext)base.Component;

    public ValueTask DisposeAsync()
    {
        try
        {
            Component.Dispose();
        }
        finally
        {
            base.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StreamCancellationStopsPumpWithoutCompletingCallerOperation()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = Component.StreamUntilAsync(operation.Task, cancellation.Token);

        try
        {
            Assert.False(pump.IsCompleted);
            await cancellation.CancelAsync();

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pump.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.False(operation.Task.IsCompleted);
        }
        finally
        {
            operation.TrySetResult();
            await operation.Task;
            try
            {
                await pump;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }
}
