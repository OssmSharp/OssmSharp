namespace OssmSharp.Cli;

internal interface IPromptInput
{
    Task<PromptInputResult> ReadAsync(CancellationToken cancellationToken);
}
