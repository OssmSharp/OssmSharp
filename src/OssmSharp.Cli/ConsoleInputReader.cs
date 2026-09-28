using System.Text;

namespace OssmSharp.Cli;

internal sealed class ConsoleInputReader : TextReader
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
                    await Console.Error.WriteLineAsync().ConfigureAwait(false);
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    await Console.Error.WriteLineAsync().ConfigureAwait(false);
                    if (overlong)
                    {
                        await Console.Error.WriteLineAsync("ERROR input: command exceeds 4096 characters; entire line discarded. Nothing sent.").ConfigureAwait(false);
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
                        if (!Console.IsErrorRedirected)
                        {
                            await Console.Error.WriteAsync("\b \b").ConfigureAwait(false);
                        }
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
                        await Console.Error.WriteAsync(key.KeyChar).ConfigureAwait(false);
                    }
                }
            }

            await Console.Error.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
