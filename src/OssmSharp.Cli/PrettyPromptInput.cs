using PrettyPrompt;
using PrettyPrompt.Consoles;

namespace OssmSharp.Cli;

internal sealed class PrettyPromptInput : IPromptInput, IAsyncDisposable
{
    private readonly KeyPressCallbackResult endOfInput = new(string.Empty, output: null);
    private readonly Func<IPrompt>? promptFactory;
    private readonly PrettyPromptConsole? console;
    private IPrompt? prompt;

    internal PrettyPromptInput(TextWriter output, TextWriter error, string persistentHistoryFilepath)
        : this(new PrettyPromptConsole(output, error), persistentHistoryFilepath)
    {
    }

    internal PrettyPromptInput(PrettyPromptConsole console, string persistentHistoryFilepath)
    {
        this.console = console;
        promptFactory = () => new Prompt(
            persistentHistoryFilepath,
            new EndOfInputCallbacks(endOfInput),
            console,
            new PromptConfiguration(prompt: CliStrings.Prompt));
    }

    internal PrettyPromptInput(IPrompt prompt)
    {
        this.prompt = prompt;
    }

    internal PrettyPromptInput(Func<IPrompt> promptFactory)
    {
        this.promptFactory = promptFactory;
    }

    public async Task<PromptInputResult> ReadAsync(CancellationToken cancellationToken)
    {
        if (console is not null)
        {
            console.CancellationToken = cancellationToken;
        }

        try
        {
            var currentPrompt = prompt ??= promptFactory!();
            var result = await Task.Run(async () => await currentPrompt.ReadLineAsync().ConfigureAwait(false)).ConfigureAwait(false);
            if (ReferenceEquals(result, endOfInput))
            {
                return new(null);
            }

            return result.IsSuccess
                ? new(result.Text, CommandCancellationToken: result.CancellationToken)
                : new(string.Empty, IsInterrupted: true);
        }
        finally
        {
            if (console is not null)
            {
                console.CancellationToken = CancellationToken.None;
            }
        }
    }

    public ValueTask DisposeAsync() => prompt is IAsyncDisposable disposable
        ? disposable.DisposeAsync()
        : ValueTask.CompletedTask;

    internal static PrettyPromptInput Create(TextWriter error)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OssmSharp");
        Directory.CreateDirectory(directory);
        return new(error, error, Path.Combine(directory, "prompt-history"));
    }

    private sealed class EndOfInputCallbacks(KeyPressCallbackResult result) : PromptCallbacks
    {
        private bool endOfInputRequested;

        protected override IEnumerable<(KeyPressPattern Pattern, KeyPressCallbackAsync Callback)> GetKeyPressCallbacks()
        {
            yield return (new(ConsoleModifiers.Control, ConsoleKey.D), EndInputAsync);
        }

        protected override Task<KeyPress> TransformKeyPressAsync(
            string text,
            int caret,
            KeyPress keyPress,
            CancellationToken cancellationToken)
        {
            endOfInputRequested = text.Length == 0
                && keyPress.ConsoleKeyInfo is { Key: ConsoleKey.D, Modifiers: ConsoleModifiers.Control };
            return Task.FromResult(keyPress);
        }

        private Task<KeyPressCallbackResult?> EndInputAsync(string text, int caret, CancellationToken cancellationToken) =>
            Task.FromResult<KeyPressCallbackResult?>(endOfInputRequested ? result : null);
    }
}
