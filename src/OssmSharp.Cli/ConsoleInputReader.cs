using System.Text;

namespace OssmSharp.Cli;

internal sealed class ConsoleInputReader(IPromptConsole console) : TextReader
{
    private const int MaximumLineLength = 4096;
    private bool endOfInput;

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endOfInput)
        {
            return null;
        }

        var line = new StringBuilder();
        var overlong = false;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            while (Console.KeyAvailable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key is ConsoleKey.D or ConsoleKey.Z)
                {
                    endOfInput = true;
                    console.EndRead();
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    console.EndRead();
                    if (overlong)
                    {
                        await console.Error.WriteLineAsync(CliStrings.Error("input", CliStrings.OversizedInput, console.Failure, console.ErrorReset)).ConfigureAwait(false);
                        return string.Empty;
                    }

                    return line.ToString();
                }

                if (overlong)
                {
                    continue;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (line.Length > 0)
                    {
                        line.Length--;
                        console.Backspace();
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    if (line.Length == MaximumLineLength)
                    {
                        overlong = true;
                        line.Clear();
                    }
                    else
                    {
                        line.Append(key.KeyChar);
                        console.Append(key.KeyChar);
                    }
                }
            }

        }

        return null;
    }
}
