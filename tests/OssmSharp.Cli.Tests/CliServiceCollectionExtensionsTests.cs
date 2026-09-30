using Microsoft.Extensions.DependencyInjection;
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
        using var provider = new ServiceCollection()
            .AddSingleton<IBleManager>(context.Manager.Object)
            .AddOemRuntime()
            .AddCliCommands(output, error)
            .BuildServiceProvider();

        Assert.Same(output, provider.GetRequiredKeyedService<TextWriter>("output"));
        Assert.Same(error, provider.GetRequiredKeyedService<TextWriter>("error"));
        Assert.IsType<ConsoleInputReader>(provider.GetRequiredService<TextReader>());
        Assert.NotNull(provider.GetRequiredService<PromptConsole>());
        Assert.NotNull(provider.GetRequiredService<Func<string, IOssmDevice>>());
        Assert.NotNull(provider.GetRequiredService<Func<IOssmDevice, PromptHandler>>());
        Assert.IsType<ListCommand>(provider.GetRequiredService<ListCommand>());
        Assert.IsType<PromptCommand>(provider.GetRequiredService<PromptCommand>());
    }

    [Fact]
    public void ProviderDisposesOwnedInputButNotBorrowedWriters()
    {
        using var input = new StringReader("command");
        using var output = new StringWriter();
        using var error = new StringWriter();
        using (var provider = new ServiceCollection()
            .AddSingleton<TextReader>(_ => input)
            .AddCliCommands(output, error)
            .BuildServiceProvider())
        {
            Assert.Same(input, provider.GetRequiredService<TextReader>());
        }

        Assert.Throws<ObjectDisposedException>(() => input.ReadLine());
        output.Write("output is open");
        error.Write("error is open");
        Assert.Equal("output is open", output.ToString());
        Assert.Equal("error is open", error.ToString());
    }
}
