using PrettyPrompt.Consoles;

namespace OssmSharp.Cli;

internal sealed class PrettyPromptConsole(TextWriter output, TextWriter error) : IConsole
{
    private static readonly ConsoleKeyInfo InterruptKey = new('\0', ConsoleKey.C, shift: false, alt: false, control: true);
    private readonly IConsole system = new SystemConsole();
    private CancellationToken cancellationToken;

    internal PrettyPromptConsole(TextWriter output, TextWriter error, IConsole system)
        : this(output, error)
    {
        this.system = system;
    }

    public event ConsoleCancelEventHandler CancelKeyPress
    {
        add => system.CancelKeyPress += value;
        remove => system.CancelKeyPress -= value;
    }

    public int CursorTop => system.CursorTop;

    public int BufferWidth => system.BufferWidth;

    public int WindowHeight => system.WindowHeight;

    public int WindowTop => system.WindowTop;

    public bool KeyAvailable => system.KeyAvailable;

    public bool IsErrorRedirected => system.IsErrorRedirected;

    public bool CaptureControlC
    {
        get => system.CaptureControlC;
        set => system.CaptureControlC = value;
    }

    internal CancellationToken CancellationToken
    {
        set => cancellationToken = value;
    }

    public void Clear() => system.Clear();

    public void HideCursor() => system.HideCursor();

    public void InitVirtualTerminalProcessing() => system.InitVirtualTerminalProcessing();

    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        while (!system.KeyAvailable)
        {
            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(30)))
            {
                return InterruptKey;
            }
        }

        return system.ReadKey(intercept);
    }

    public void SetModifyOtherKeys(bool enabled) => system.SetModifyOtherKeys(enabled);

    public void SetNewlineAutoReturn(bool enabled) => system.SetNewlineAutoReturn(enabled);

    public void ShowCursor() => system.ShowCursor();

    public void Write(string? value) => output.Write(value);

    public void Write(ReadOnlySpan<char> value) => output.Write(value);

    public void WriteError(string? value) => error.Write(value);

    public void WriteError(ReadOnlySpan<char> value) => error.Write(value);

    public void WriteErrorLine(string? value) => error.WriteLine(value);

    public void WriteErrorLine(ReadOnlySpan<char> value) => error.WriteLine(value);

    public void WriteLine(string? value) => output.WriteLine(value);

    public void WriteLine(ReadOnlySpan<char> value) => output.WriteLine(value);
}