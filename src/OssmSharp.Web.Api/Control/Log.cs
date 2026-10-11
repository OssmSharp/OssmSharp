namespace OssmSharp.Web.Api.Control;

internal static partial class Log
{
    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Guest session changed: {State}.")]
    internal static partial void GuestSessionChanged(this ILogger<ControlSession> logger, string state);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Shared preview dimensions rejected ({Code}).")]
    internal static partial void PreviewRejected(this ILogger<ControlSession> logger, string code);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Shared preview dimensions updated; rail {RailLengthCm} cm, attachment {AttachmentLengthCm} cm. Motion settings unchanged.")]
    internal static partial void PreviewUpdated(this ILogger<ControlSession> logger, double railLengthCm, double attachmentLengthCm);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Guest-session Stop cleanup did not complete ({Code}).")]
    internal static partial void GuestStopCleanupIncomplete(this ILogger<ControlSession> logger, string code);
}
