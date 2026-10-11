namespace OssmSharp.Web.Api.Control;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Shared preview dimensions rejected ({Code}).")]
    internal static partial void PreviewRejected(this ILogger<ControlSession> logger, string code);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Shared preview dimensions updated; rail {RailLengthCm} cm, attachment {AttachmentLengthCm} cm. Motion settings unchanged.")]
    internal static partial void PreviewUpdated(this ILogger<ControlSession> logger, double railLengthCm, double attachmentLengthCm);
}
