using AwesomeAssertions;
using PrettyPrompt;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PrettyPromptInputTests
{
    [Theory]
    [InlineData(true, "status", false)]
    [InlineData(false, "", true)]
    public async Task MapsPromptResult(bool isSuccess, string text, bool isInterrupted)
    {
        await using var prompt = new StubPrompt(new PromptResult(isSuccess, text, default));
        await using var input = new PrettyPromptInput(prompt);

        var result = await input.ReadAsync(TestContext.Current.CancellationToken);

        result.Text.Should().Be(text);
        result.IsInterrupted.Should().Be(isInterrupted);
    }

    [Fact]
    public async Task DisposesOwnedPrompt()
    {
        await using var prompt = new StubPrompt(new PromptResult(true, string.Empty, default));
        var input = new PrettyPromptInput(prompt);

        await input.DisposeAsync();

        prompt.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task ConstructsPromptOnFirstRead()
    {
        var constructions = 0;
        await using var prompt = new StubPrompt(new PromptResult(true, string.Empty, default));
        await using var input = new PrettyPromptInput(() =>
        {
            constructions++;
            return prompt;
        });

        constructions.Should().Be(0);

        await input.ReadAsync(TestContext.Current.CancellationToken);

        constructions.Should().Be(1);
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
        var directory = Directory.CreateTempSubdirectory("OssmSharp-layout-");
        try
        {
            using var output = new StringWriter();
            var terminal = new PrettyPromptTestConsole(
                [Key('h', ConsoleKey.H), Key('\r', ConsoleKey.Enter), Key('h', ConsoleKey.H), Key('\r', ConsoleKey.Enter)],
                reportKeysAvailable: true)
            {
                WindowHeight = height,
                CursorTop = cursorTop,
            };
            var console = new PrettyPromptConsole(
                output,
                TextWriter.Null,
                terminal);
            await using var input = new PrettyPromptInput(console, Path.Combine(directory.FullName, "history"));

            for (var readIndex = 0; readIndex < 2; readIndex++)
            {
                output.GetStringBuilder().Clear();

                var result = await input.ReadAsync(TestContext.Current.CancellationToken);

                result.Text.Should().Be("h");
                var rendered = output.ToString();
                rendered.Count(character => character == '\n').Should().Be(1);
                Assert.DoesNotMatch(@"\u001b\[[0-9;]*[ABHfST]", rendered);
                console.WindowHeight.Should().Be(height);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ControlDOnEmptyInputReturnsEndOfInput()
    {
        var directory = Directory.CreateTempSubdirectory("OssmSharp-eof-");
        try
        {
            await using var input = CreateInput(directory, [Key('\0', ConsoleKey.D, control: true)]);

            var result = await input.ReadAsync(TestContext.Current.CancellationToken);

            result.Text.Should().BeNull();
            result.IsInterrupted.Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ControlDOnNonEmptyInputDeletesForward()
    {
        var directory = Directory.CreateTempSubdirectory("OssmSharp-delete-");
        try
        {
            await using var input = CreateInput(
                directory,
            [
                Key('a', ConsoleKey.A),
                Key('b', ConsoleKey.B),
                Key('\0', ConsoleKey.LeftArrow),
                Key('\0', ConsoleKey.D, control: true),
                Key('\r', ConsoleKey.Enter),
            ]);

            var result = await input.ReadAsync(TestContext.Current.CancellationToken);

            result.Text.Should().Be("a");
            result.IsInterrupted.Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ControlZUsesNativeUndo()
    {
        var directory = Directory.CreateTempSubdirectory("OssmSharp-undo-");
        try
        {
            await using var input = CreateInput(
                directory,
            [
                Key('a', ConsoleKey.A),
                Key('b', ConsoleKey.B),
                Key('\0', ConsoleKey.Z, control: true),
                Key('\r', ConsoleKey.Enter),
            ]);

            var result = await input.ReadAsync(TestContext.Current.CancellationToken);

            result.Text.Should().Be("a");
            result.IsInterrupted.Should().BeFalse();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExternalCancellationEndsIdleRead()
    {
        var directory = Directory.CreateTempSubdirectory("OssmSharp-cancel-");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var console = new PrettyPromptConsole(output, error, new PrettyPromptTestConsole([]));
            await using var input = new PrettyPromptInput(console, Path.Combine(directory.FullName, "history"));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

            var read = input.ReadAsync(cancellation.Token);
            await cancellation.CancelAsync();
            var result = await read.WaitAsync(TestContext.Current.CancellationToken);

            result.IsInterrupted.Should().BeTrue();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PersistentHistoryRecallsSubmittedCommandAfterRestart()
    {
        var directory = Directory.CreateTempSubdirectory("OssmSharp-history-");
        var history = Path.Combine(directory.FullName, "prompt-history");
        try
        {
            await using (var prompt = new Prompt(
                history,
                console: PrettyPromptTestConsole.ForText("status"),
                configuration: new PromptConfiguration(prompt: CliStrings.Prompt)))
            {
                (await prompt.ReadLineAsync()).Text.Should().Be("status");
            }

            await using var restarted = new Prompt(
                history,
                console: PrettyPromptTestConsole.ForHistoryRecall(),
                configuration: new PromptConfiguration(prompt: CliStrings.Prompt));

            (await restarted.ReadLineAsync()).Text.Should().Be("status");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static PrettyPromptInput CreateInput(DirectoryInfo directory, IEnumerable<ConsoleKeyInfo> keys)
    {
        var console = new PrettyPromptConsole(
            TextWriter.Null,
            TextWriter.Null,
            new PrettyPromptTestConsole(keys, reportKeysAvailable: true));
        return new PrettyPromptInput(console, Path.Combine(directory.FullName, "history"));
    }

    private static ConsoleKeyInfo Key(
        char character,
        ConsoleKey key,
        bool shift = false,
        bool alt = false,
        bool control = false) => new(character, key, shift, alt, control);

    private sealed class StubPrompt(PromptResult result) : IPrompt, IAsyncDisposable
    {
        internal bool IsDisposed { get; private set; }

        public Task<PromptResult> ReadLineAsync() => Task.FromResult(result);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
