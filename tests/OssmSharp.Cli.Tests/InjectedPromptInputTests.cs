using AwesomeAssertions;
using FastMoq;
using FastMoq.Extensions;
using Moq;
using PrettyPrompt;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class InjectedPromptInputTests : MockerTestBase<object>, IAsyncDisposable
{
    private Mock<IPrompt> prompt = null!;

    protected override Action<Mocker> SetupMocksAction => mocks =>
    {
        prompt = new Mock<IPrompt>();
        prompt.As<IAsyncDisposable>()
            .Setup(prompt => prompt.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mocks.AddType<IPrompt>(prompt.Object);
    };

    protected override Func<Mocker, object> CreateComponentAction => mocks =>
        new PrettyPromptInput(mocks.GetRequiredObject<IPrompt>());

    private new PrettyPromptInput Component => (PrettyPromptInput)base.Component;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            base.Dispose();
        }
    }

    [Theory]
    [InlineData(true, "status", false)]
    [InlineData(false, "", true)]
    public async Task MapsPromptResult(bool isSuccess, string text, bool isInterrupted)
    {
        var console = new PrettyPromptConsole(
            TextWriter.Null,
            TextWriter.Null,
            new PrettyPromptTestConsole(
                [
                    new('s', ConsoleKey.S, false, false, false),
                    new('t', ConsoleKey.T, false, false, false),
                    new('a', ConsoleKey.A, false, false, false),
                    new('t', ConsoleKey.T, false, false, false),
                    new('u', ConsoleKey.U, false, false, false),
                    new('s', ConsoleKey.S, false, false, false),
                    new('\r', ConsoleKey.Enter, false, false, false),
                ],
                reportKeysAvailable: true));
        await using var realPrompt = new Prompt(console: console);
        var commandResult = await realPrompt.ReadLineAsync();
        commandResult.CancellationToken.CanBeCanceled.Should().BeTrue();
        var promptResult = isSuccess ? commandResult : new PromptResult(false, text, default);
        prompt.Setup(prompt => prompt.ReadLineAsync()).ReturnsAsync(promptResult);

        var result = await Component.ReadAsync(TestContext.Current.CancellationToken);

        result.Text.Should().Be(text);
        result.IsInterrupted.Should().Be(isInterrupted);
        result.CommandCancellationToken.Should().Be(isSuccess ? commandResult.CancellationToken : CancellationToken.None);
    }

    [Fact]
    public async Task DisposesOwnedPrompt()
    {
        await Component.DisposeAsync();

        prompt.As<IAsyncDisposable>()
            .Verify(prompt => prompt.DisposeAsync(), Times.Once);
    }
}
