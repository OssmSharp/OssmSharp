namespace OssmSharp.Web.AdminWeb.Components.Pages;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Preview attachment length rejected before sharing.")]
    internal static partial void InvalidPreviewLength(this ILogger<Home> logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Unable to share preview dimensions; the guest preview may retain earlier dimensions.")]
    internal static partial void PreviewUpdateFailed(this ILogger<Home> logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Preview dimensions shared, but the browser preference could not be saved.")]
    internal static partial void PreviewPreferenceSaveFailed(this ILogger<Home> logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "User action {Action} could not refresh the latest session state.")]
    internal static partial void SessionStateRefreshFailed(this ILogger<Home> logger, string action);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Earlier user action {Action} completed ({Result}); the newer action keeps its notice.")]
    internal static partial void SupersededActionCompleted(this ILogger<Home> logger, string action, string result);
}
