namespace OssmSharp.Web.Api.SignalR;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Message = "Control API {Operation} started.")]
    internal static partial void OperationStarted(this ILogger<ControlHub> logger, LogLevel level, string operation);

    [LoggerMessage(EventId = 2, Message = "Control API {Operation} finished with {Result} ({Code}) in {DurationMilliseconds} ms; {CommandsWritten} commands written.")]
    internal static partial void OperationFinished(this ILogger<ControlHub> logger, LogLevel level, string operation, string result, string? code, double durationMilliseconds, int? commandsWritten);
}
