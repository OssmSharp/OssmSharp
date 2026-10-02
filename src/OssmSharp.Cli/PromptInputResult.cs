namespace OssmSharp.Cli;

internal readonly record struct PromptInputResult(
    string? Text,
    bool IsInterrupted = false,
    CancellationToken CommandCancellationToken = default);