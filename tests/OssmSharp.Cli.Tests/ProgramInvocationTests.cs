using OssmSharp.Cli.Commands;
using OssmSharp.Cli.Tests.Helpers;

namespace OssmSharp.Cli.Tests;

/// <summary>
/// Verifies that foreground commands finish without a GUI message loop.
/// </summary>
public sealed class ProgramInvocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListCompletesWithoutPumpingCallerContext(bool foundDevice)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var scan = new TaskCompletionSource<IReadOnlyList<(string Name, string Id)>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new CliRootCommand(
            new DeviceCommand(
                new DeviceGetCommand((_, _) => Task.FromResult(0)),
                new DeviceListCommand(_ => scan.Task, output, error)),
            new PromptCommand((_, _) => Task.FromResult(0)));
        using var context = new PausedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        Task<int> invocation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            invocation = Program.InvokeAsync(root, ["devices", "list"], TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        scan.SetResult(foundDevice ? [("Stock", "selected-id")] : []);
        try
        {
            var exitCode = await invocation.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                (foundDevice ? "1. Stock (selected-id)" : "No stock devices found.") + Environment.NewLine,
                output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            context.Dispose();
            await invocation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
