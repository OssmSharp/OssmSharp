using AwesomeAssertions;
using Xunit;

namespace OssmSharp.Cli.Tests;

public sealed class PrettyPromptInputTests
{
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
            var console = new PrettyPromptConsole(
                TextWriter.Null,
                TextWriter.Null,
                new PrettyPromptTestConsole(
                    [
                        Key('s', ConsoleKey.S),
                        Key('t', ConsoleKey.T),
                        Key('a', ConsoleKey.A),
                        Key('t', ConsoleKey.T),
                        Key('u', ConsoleKey.U),
                        Key('s', ConsoleKey.S),
                        Key('\r', ConsoleKey.Enter),
                    ],
                    reportKeysAvailable: true));
            await using (var input = new PrettyPromptInput(console, history))
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
}
