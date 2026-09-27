namespace OssmSharp.Cli.Tests;

public sealed class ForegroundInputReaderTests
{
    [Fact]
    public async Task CancelledConsumerSettlesWhileConsoleReadRemainsBlocked()
    {
        using var source = new BlockingReader();
        using var input = new ForegroundInputReader(source);
        using var cancellation = new CancellationTokenSource();
        var read = Task.Run(async () => await input.ReadLineAsync(cancellation.Token));

        await source.Entered.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
        finally
        {
            source.Release();
            try
            {
                await read.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task RedirectedInputPreservesLinesAndEof()
    {
        using var source = new StringReader("first\nsecond\n");
        using var input = new ForegroundInputReader(source);

        Assert.Equal("first", await input.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("second", await input.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Null(await input.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InputReadFailureIsReportedToTheConsumer()
    {
        using var source = new ThrowingReader();
        using var input = new ForegroundInputReader(source);

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            input.ReadLineAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("Input failed", failure.Message, StringComparison.Ordinal);
    }

    private sealed class BlockingReader : TextReader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public override string? ReadLine()
        {
            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            return null;
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            new(ReadLine());

        public void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string? ReadLine() => throw new IOException("Input failed.");
    }
}
