using System.Text;

namespace OssmSharp.Cli;

internal sealed class PromptConsole : IPromptConsole
{
    private const string SuccessColour = "\u001b[32m";
    private const string WarningColour = "\u001b[33m";
    private const string ErrorColour = "\u001b[31m";
    private const string ResetColour = "\u001b[0m";
    private readonly Lock sync = new();
    private readonly TextWriter error;
    private readonly bool interactive;
    private readonly Func<int> columns;
    private readonly StringBuilder input = new();
    private bool reading;

    internal PromptConsole(TextWriter output, TextWriter error, bool interactive, bool colourOutput = false, bool colourError = false, Func<int>? columns = null)
    {
        this.error = error;
        this.interactive = interactive;
        this.columns = columns ?? (() => Console.BufferWidth);
        Output = new PromptTextWriter(this, output);
        Error = new PromptTextWriter(this, error);
        Success = colourOutput ? SuccessColour : string.Empty;
        Warning = colourOutput ? WarningColour : string.Empty;
        Failure = colourError ? ErrorColour : string.Empty;
        OutputReset = colourOutput ? ResetColour : string.Empty;
        ErrorReset = colourError ? ResetColour : string.Empty;
    }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    public string Success { get; }

    public string Warning { get; }

    public string Failure { get; }

    public string OutputReset { get; }

    public string ErrorReset { get; }

    public void BeginRead()
    {
        lock (sync)
        {
            reading = true;
            input.Clear();
            if (interactive)
            {
                RenderInput();
            }
        }
    }

    public void Append(char character)
    {
        lock (sync)
        {
            input.Append(character);
            if (reading && interactive)
            {
                RenderInput();
            }
        }
    }

    public void Backspace()
    {
        lock (sync)
        {
            if (input.Length > 0)
            {
                input.Length--;
            }

            if (reading && interactive)
            {
                RenderInput();
            }
        }
    }

    public void EndRead()
    {
        lock (sync)
        {
            var wasReading = reading;
            reading = false;
            input.Clear();
            if (wasReading && interactive)
            {
                error.WriteLine();
                error.Flush();
            }
        }
    }

    public void WriteLine(TextWriter target, string value)
    {
        lock (sync)
        {
            if (reading && interactive)
            {
                error.Write("\r\u001b[2K");
                error.Flush();
            }

            target.WriteLine(value);
            target.Flush();
            if (reading && interactive)
            {
                RenderInput();
            }
        }
    }

    internal static PromptConsole FromConsole(TextWriter output, TextWriter error) => new(
        output,
        error,
        interactive: !Console.IsErrorRedirected,
        colourOutput: !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null && Environment.GetEnvironmentVariable("TERM") != "dumb",
        colourError: !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null && Environment.GetEnvironmentVariable("TERM") != "dumb");

    private void RenderInput()
    {
        var text = input.ToString();
        var available = Math.Max(1, columns() - CliStrings.Prompt.Length - 1);
        if (text.Any(character => character > 127))
        {
            available = Math.Max(1, available / 2);
        }

        var start = Math.Max(0, text.Length - available);
        if (start < text.Length && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        error.Write($"\r\u001b[2K{CliStrings.Prompt}{text[start..]}");
        error.Flush();
    }
}
