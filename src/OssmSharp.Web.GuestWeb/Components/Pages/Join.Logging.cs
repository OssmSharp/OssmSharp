namespace OssmSharp.Web.GuestWeb.Components.Pages;

public partial class Join
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Guest control initialization failed; closing the connection.")]
    private static partial void LogGuestControlInitializationFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guest leave was not accepted: {Code}.")]
    private static partial void LogGuestLeaveRejected(ILogger logger, string code);
}
