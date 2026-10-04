using OssmSharp.Runtime;
using OssmSharp.Runtime.Oem;
using Shiny.BluetoothLE;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class CliServiceCollectionExtensionsTests
{
    [Fact]
    public void CommandsResolveWithRuntimeFactoryKeyedWritersAndLoggers()
    {
        using var context = new CommandTestContext();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var input = new PromptInput();
        using var provider = new ServiceCollection()
            .AddSingleton<IBleManager>(context.Manager.Object)
            .AddSingleton<IPromptInput>(input)
            .AddOssmSharpOemRuntime()
            .AddCliCommands(output, error)
            .BuildServiceProvider();

        Assert.Same(output, provider.GetRequiredKeyedService<TextWriter>("output"));
        Assert.Same(error, provider.GetRequiredKeyedService<TextWriter>("error"));
        Assert.Same(input, provider.GetRequiredService<IPromptInput>());
        Assert.IsType<PromptConsole>(provider.GetRequiredService<PromptConsole>());
        Assert.IsType<PromptHandlerFactory>(provider.GetRequiredService<PromptHandlerFactory>());
        Assert.NotNull(provider.GetRequiredService<Func<string, IOssmDevice>>());
        Assert.IsType<ListCommand>(provider.GetRequiredService<ListCommand>());
        Assert.IsType<PromptCommand>(provider.GetRequiredService<PromptCommand>());
    }

    [Fact]
    public async Task ProviderDisposesOwnedInputButNotBorrowedWriters()
    {
        await using var input = new DisposablePromptInput();
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using (var provider = new ServiceCollection()
            .AddSingleton<IPromptInput>(_ => input)
            .AddCliCommands(output, error)
            .BuildServiceProvider())
        {
            Assert.Same(input, provider.GetRequiredService<IPromptInput>());
        }

        Assert.True(input.IsDisposed);
        await output.WriteAsync("output is open");
        await error.WriteAsync("error is open");
        Assert.Equal("output is open", output.ToString());
        Assert.Equal("error is open", error.ToString());
    }

    private sealed class DisposablePromptInput : IPromptInput, IAsyncDisposable
    {
        internal bool IsDisposed { get; private set; }

        public Task<PromptInputResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PromptInputResult(null));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
