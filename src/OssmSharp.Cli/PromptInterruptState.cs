namespace OssmSharp.Cli;

internal sealed class PromptInterruptState
{
    private static readonly Task PendingCommand = new TaskCompletionSource().Task;
    private Task? command;

    internal bool IsCommandPending => Volatile.Read(ref command) is { IsCompleted: false };

    internal void BeginCommand() => Volatile.Write(ref command, PendingCommand);

    internal void TrackCommand(Task operation) => Volatile.Write(ref command, operation);

    internal void EndCommand() => Volatile.Write(ref command, null);
}