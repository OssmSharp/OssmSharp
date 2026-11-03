namespace OssmSharp.Web.AdminWeb.Components.Pages;

internal static partial class Log
{
    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "User action {Action} could not refresh the latest session state.")]
    internal static partial void SessionStateRefreshFailed(this ILogger<Home> logger, string action);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Earlier user action {Action} completed ({Result}); the newer action keeps its notice.")]
    internal static partial void SupersededActionCompleted(this ILogger<Home> logger, string action, string result);
}
