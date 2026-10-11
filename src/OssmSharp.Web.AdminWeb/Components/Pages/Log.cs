namespace OssmSharp.Web.AdminWeb.Components.Pages;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Preview attachment length rejected before sharing.")]
    internal static partial void InvalidPreviewLength(this ILogger<Home> logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Unable to share preview dimensions; the guest preview may retain earlier dimensions.")]
    internal static partial void PreviewUpdateFailed(this ILogger<Home> logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Preview dimensions shared, but the browser preference could not be saved.")]
    internal static partial void PreviewPreferenceSaveFailed(this ILogger<Home> logger, Exception exception);
}
