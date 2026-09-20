namespace OSSMDotNet.Simulator;

internal static partial class Log
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Simulator process started.")]
    public static partial void Started(this ILogger logger);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Simulator process stopped.")]
    public static partial void Stopped(this ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "Simulator wrote diagnostic output.")]
    public static partial void DiagnosticsAvailable(this ILogger logger);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "Simulator did not stop in time and will be terminated.")]
    public static partial void ShutdownTimedOut(this ILogger logger);
}
