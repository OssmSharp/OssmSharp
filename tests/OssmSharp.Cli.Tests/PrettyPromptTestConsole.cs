using PrettyPrompt.Consoles;

namespace OssmSharp.Cli.Tests;

internal sealed class PrettyPromptTestConsole(
    IEnumerable<ConsoleKeyInfo> keys,
    bool reportKeysAvailable = false) : IConsole
{
    private readonly Queue<ConsoleKeyInfo> keys = new(keys);
    private bool keyWasRead;

    public event ConsoleCancelEventHandler? CancelKeyPress
    {
        add { }
        remove { }
    }

    public int CursorTop => 0;

    public int BufferWidth => 120;

    public int WindowHeight => 40;

    public int WindowTop => 0;

    public bool KeyAvailable
    {
        get
        {
            if (!reportKeysAvailable)
            {
                return false;
            }

            if (keyWasRead)
            {
                keyWasRead = false;
                return false;
            }

            return keys.Count > 0;
        }
    }

    public bool IsErrorRedirected => false;

    public bool CaptureControlC { get; set; }

    public void Clear()
    {
    }

    public void HideCursor()
    {
    }

    public void InitVirtualTerminalProcessing()
    {
    }

    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        keyWasRead = reportKeysAvailable;
        return keys.Dequeue();
    }

    public void ShowCursor()
    {
    }

    public void Write(string? value)
    {
    }

    public void Write(ReadOnlySpan<char> value)
    {
    }

    public void WriteError(string? value)
    {
    }

    public void WriteError(ReadOnlySpan<char> value)
    {
    }

    public void WriteErrorLine(string? value)
    {
    }

    public void WriteErrorLine(ReadOnlySpan<char> value)
    {
    }

    public void WriteLine(string? value)
    {
    }

    public void WriteLine(ReadOnlySpan<char> value)
    {
    }

    internal static PrettyPromptTestConsole ForText(string text) => new(
        text.Select(character => new ConsoleKeyInfo(
                character,
                Enum.Parse<ConsoleKey>(char.ToUpperInvariant(character).ToString()),
                shift: false,
                alt: false,
                control: false))
            .Append(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false)));

    internal static PrettyPromptTestConsole ForHistoryRecall() => new(
    [
        new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: false, alt: false, control: false),
        new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false),
    ]);
}