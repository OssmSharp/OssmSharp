using Microsoft.Extensions.Logging;

namespace OssmSharp.Cli;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Debug, "Discovery completed with {DeviceCount} devices.")]
    public static partial void DiscoveryCompleted(this ILogger logger, int deviceCount);

    [LoggerMessage(2, LogLevel.Debug, "Command {Command} was cancelled; awaiting cleanup.")]
    public static partial void CommandCancelled(this ILogger logger, string command);

    [LoggerMessage(3, LogLevel.Error, "Command {Command} failed with {FailureType}.")]
    public static partial void CommandFailed(this ILogger logger, string command, string failureType);

    [LoggerMessage(4, LogLevel.Error, "Cleanup of {Resource} failed with {FailureType}.")]
    public static partial void CleanupFailed(this ILogger logger, string resource, string failureType);

    [LoggerMessage(5, LogLevel.Debug, "Prompt device connection established.")]
    public static partial void PromptConnected(this ILogger logger);

    [LoggerMessage(6, LogLevel.Debug, "Prompt completed with exit code {ExitCode} after device cleanup.")]
    public static partial void PromptCompleted(this ILogger logger, int exitCode);
}
