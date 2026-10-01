namespace OssmSharp.Cli;

internal interface IPromptInput
{
    Task<PromptInputResult> ReadAsync(CancellationToken cancellationToken);
}

internal readonly record struct PromptInputResult(
    string? Text,
    bool IsInterrupted = false,
    CancellationToken CommandCancellationToken = default);
