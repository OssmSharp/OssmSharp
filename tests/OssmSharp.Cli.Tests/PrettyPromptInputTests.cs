using AwesomeAssertions;
using FastMoq;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PrettyPromptInputTests : MockerTestBase<object>, IAsyncDisposable
{
    private DirectoryInfo directory = null!;
    private StringWriter output = null!;
    private StringWriter error = null!;
    private PrettyPromptTestConsole terminal = null!;
    private PrettyPromptConsole console = null!;

    protected override Action<Mocker> SetupMocksAction => _ =>
    {
        directory = Directory.CreateTempSubdirectory("OssmSharp-prompt-");
        output = new StringWriter();
        error = new StringWriter();
        terminal = new PrettyPromptTestConsole([], reportKeysAvailable: true);
        console = new PrettyPromptConsole(output, error, terminal);
    };

    protected override Func<Mocker, object> CreateComponentAction => _ =>
        new PrettyPromptInput(console, Path.Combine(directory.FullName, "history"));

    private new PrettyPromptInput Component => (PrettyPromptInput)base.Component;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Component.DisposeAsync();
        }
        finally
        {
            output.Dispose();
            error.Dispose();
            directory.Delete(recursive: true);
            base.Dispose();
        }
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 3)]
    [InlineData(12, 5)]
    [InlineData(12, 11)]
    [InlineData(40, 39)]
    [InlineData(100, 99)]
    public async Task RepeatedReadsDoNotReserveBlankRows(int height, int cursorTop)
    {
        terminal.EnqueueKeys([Key('h', ConsoleKey.H), Key('\r', ConsoleKey.Enter), Key('h', ConsoleKey.H), Key('\r', ConsoleKey.Enter)]);
        terminal.WindowHeight = height;
        terminal.CursorTop = cursorTop;

        for (var readIndex = 0; readIndex < 2; readIndex++)
        {
            output.GetStringBuilder().Clear();

            var result = await Component.ReadAsync(TestContext.Current.CancellationToken);

            result.Text.Should().Be("h");
            var rendered = output.ToString();
            rendered.Count(character => character == '\n').Should().Be(1);
            Assert.DoesNotMatch(@"\u001b\[[0-9;]*[ABHfST]", rendered);
            console.WindowHeight.Should().Be(height);
        }
    }

    [Fact]
    public async Task ControlDOnEmptyInputReturnsEndOfInput()
    {
        terminal.EnqueueKeys([Key('\0', ConsoleKey.D, control: true)]);

        var result = await Component.ReadAsync(TestContext.Current.CancellationToken);

        result.Text.Should().BeNull();
        result.IsInterrupted.Should().BeFalse();
    }

    [Fact]
    public async Task ControlDOnNonEmptyInputDeletesForward()
    {
        terminal.EnqueueKeys(
            [
                Key('a', ConsoleKey.A),
                Key('b', ConsoleKey.B),
                Key('\0', ConsoleKey.LeftArrow),
                Key('\0', ConsoleKey.D, control: true),
                Key('\r', ConsoleKey.Enter),
            ]);

        var result = await Component.ReadAsync(TestContext.Current.CancellationToken);

        result.Text.Should().Be("a");
        result.IsInterrupted.Should().BeFalse();
    }

    [Fact]
    public async Task ControlZUsesNativeUndo()
    {
        terminal.EnqueueKeys(
            [
                Key('a', ConsoleKey.A),
                Key('b', ConsoleKey.B),
                Key('\0', ConsoleKey.Z, control: true),
                Key('\r', ConsoleKey.Enter),
            ]);

        var result = await Component.ReadAsync(TestContext.Current.CancellationToken);

        result.Text.Should().Be("a");
        result.IsInterrupted.Should().BeFalse();
    }

    [Fact]
    public async Task ExternalCancellationEndsIdleRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var read = Component.ReadAsync(cancellation.Token);
        await cancellation.CancelAsync();
        var result = await read.WaitAsync(TestContext.Current.CancellationToken);

        result.IsInterrupted.Should().BeTrue();
    }

    [Fact]
    public async Task PersistentHistoryRecallsSubmittedCommandAfterRestart()
    {
        var history = Path.Combine(directory.FullName, "history");
        terminal.EnqueueKeys(
            [
                Key('s', ConsoleKey.S),
                Key('t', ConsoleKey.T),
                Key('a', ConsoleKey.A),
                Key('t', ConsoleKey.T),
                Key('u', ConsoleKey.U),
                Key('s', ConsoleKey.S),
                Key('\r', ConsoleKey.Enter),
            ]);
        await using (var input = Component)
        {
            var result = await input.ReadAsync(TestContext.Current.CancellationToken);

            result.Text.Should().Be("status");
            result.IsInterrupted.Should().BeFalse();
        }

        Assert.True(File.Exists(history));
        var restartedConsole = new PrettyPromptConsole(
            TextWriter.Null,
            TextWriter.Null,
            new PrettyPromptTestConsole(
                [
                    Key('\0', ConsoleKey.UpArrow),
                    Key('\r', ConsoleKey.Enter),
                ],
                reportKeysAvailable: true));
        await using var restarted = new PrettyPromptInput(restartedConsole, history);

        var recalled = await restarted.ReadAsync(TestContext.Current.CancellationToken);

        recalled.Text.Should().Be("status");
        recalled.IsInterrupted.Should().BeFalse();
    }

    private static ConsoleKeyInfo Key(
        char character,
        ConsoleKey key,
        bool shift = false,
        bool alt = false,
        bool control = false) => new(character, key, shift, alt, control);
}
