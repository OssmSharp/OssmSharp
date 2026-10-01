namespace OssmSharp.Cli;

internal sealed class PromptConsole
{
    private const string SuccessColour = "\u001b[32m";
    private const string WarningColour = "\u001b[33m";
    private const string ErrorColour = "\u001b[31m";
    private const string ResetColour = "\u001b[0m";
    internal PromptConsole(TextWriter output, TextWriter error, bool colourOutput = false, bool colourError = false)
    {
        Output = TextWriter.Synchronized(output);
        Error = TextWriter.Synchronized(error);
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

    internal static PromptConsole FromConsole(TextWriter output, TextWriter error) => new(
        output,
        error,
        colourOutput: !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null && Environment.GetEnvironmentVariable("TERM") != "dumb",
        colourError: !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null && Environment.GetEnvironmentVariable("TERM") != "dumb");
}
