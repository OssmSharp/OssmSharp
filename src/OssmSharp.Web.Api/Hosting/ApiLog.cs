namespace OssmSharp.Web.Api.Hosting;

internal static partial class ApiLog
{
    [LoggerMessage(
        EventId = 201,
        EventName = "RequiredStopRetryFailed",
        Level = LogLevel.Warning,
        Message = "Retrying a required device Stop failed.")]
    internal static partial void RequiredStopRetryFailed(ILogger logger, Exception exception);
}
