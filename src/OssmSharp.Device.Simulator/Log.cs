namespace OssmSharp.Device.Simulator;

static partial class Log
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Simulator connection opened.")]
    public static partial void Connected(this ILogger logger);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Simulator connection closed.")]
    public static partial void Disconnected(this ILogger logger);
}
